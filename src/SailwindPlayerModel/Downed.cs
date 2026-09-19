using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>Why a player went down. Carried with the fall so whoever asked for it can be told apart in the log.</summary>
    public enum DownedReason : byte
    {
        Shoved = 1,
        Fell = 2,
        Hit = 3,
        Slipped = 4,
    }

    /// <summary>Settings for getting knocked down.</summary>
    public static class DownedTuning
    {
        private const string Section = "9. Knocked Down";

        public static ConfigEntry<bool> Enabled { get; private set; }
        public static ConfigEntry<bool> RagdollLongFalls { get; private set; }
        public static ConfigEntry<KeyboardShortcut> TestKey { get; private set; }

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "Enabled", false,
                "Unfinished, off by default. Getting knocked off your feet as a ragdoll: falls from the rigging, and being shoved once crewmates can shove. A boat that brushes the fallen body still throws it, which is why this waits.");
            RagdollLongFalls = cfg.Bind(Section, "RagdollLongFalls", true,
                "Go limp and fall as a ragdoll when a fall will take more than a second coming down and ends at least 2 meters below where you left the ground (off the rigging, down a hatch, over the side), or when a flying leap lands hard on a deck at least 1.5 meters lower. Ordinary jumps never do.");
            TestKey = cfg.Bind(Section, "TestKey", KeyboardShortcut.Empty,
                "Testing only, leave unbound for normal play. Press to get knocked over, pushed from a random direction.");
        }
    }

    /// <summary>
    /// The local player knocked off their feet: the body goes over as a ragdoll, lands on whatever is under it, lies
    /// there a while, and gets back up where it landed.
    ///
    /// A REAL FALL. Nothing about going over is animated. The ragdoll (see <see cref="Ragdoll"/>) is built from the body
    /// as it stands and given the push, and Unity takes it onto the actual deck, down the actual companionway, off the
    /// actual yard. The view rides its head and the body's bones copy its parts. Getting up is animated, from however
    /// the body landed.
    ///
    /// Built on the same handover as sitting: the physics controller and the observer mirror are switched off, the
    /// controller stays where the player stood, and the observer (which the eye camera hangs off) is moved. When the
    /// fall is over the controller is put where the body landed.
    ///
    /// THE SEA. Falling over the side, the game moves the player off the boat when the view goes into the water, and the
    /// ragdoll moves with them. Once the body is in the water the fall ends there and the game's own swimming takes over.
    /// </summary>
    public class Downed : MonoBehaviour
    {
        public static Downed Instance { get; private set; }

        private enum Phase { None, Falling, Down, Rising }

        private const float MinDownSeconds = 0.8f;
        private const float StillSeconds = 0.4f;
        private const float MaxFallSeconds = 15f;
        private const float GetUpSeconds = 1.9f;
        private const float CapsuleRiseSeconds = 0.8f;
        private const float LongFallSeconds = 1f;
        // A fall has to end at least this far below where the player left the ground to count as a long one.
        private const float LongFallMinDrop = 2f;
        // Or land at least this fast (across and down together, m/s) at least this far down: a flying leap off a deck.
        // Deliberately steep: jumping from a ship to the dock is an everyday thing and must not put the player down.
        private const float LeapImpactSpeed = 11f;
        private const float LeapMinDrop = 1.5f;
        // How long a body keeps its arms over its face or head after landing before going limp.
        private const float ReactionHoldSeconds = 1.4f;

        private Phase _phase;
        private float _phaseTime;
        private float _downFor;
        private float _stillFor;
        private DownedReason _reason;
        private FallReaction _reaction;
        private bool _rolledForStairs;
        private float _startPelvisLocalY;
        private float _lastPelvisSpeed;
        private int _impacts;
        private bool _bodyGetsUp;

        private Ragdoll _rag;
        private Transform _physics, _visual;
        private Vector3 _standLocal;
        private Vector3 _riseFrom;

        private Camera _rolledCam;
        private Quaternion _view = Quaternion.identity;
        private bool _viewInit;
        private bool _lookTaken;
        private readonly DazeEffect _daze = new DazeEffect();
        // In the sea: since this time, floating at the surface until that one (0 while still coming up), on water at this height.
        private bool _inWater;
        private float _waterTime;
        private float _floatUntil;
        private float _waterY;
        private bool _renderHooked;

        // Watching for a long fall.
        private Vector3 _lastLocal;
        private Transform _lastParent;
        private bool _haveLast;
        private float _takeoffY;
        private float _fallTime;
        private float _lastVy;
        private float _gravity = 9.81f;
        private float _nextLongFallTry;
        private float _lastSwimTime = -10f;
        // How hard the fall was, from the fastest the body moved: sets how long a fall (not a shove) lies there, and how
        // long anyone takes to get up.
        private float _peakSpeed;
        private float _getUpSeconds = GetUpSeconds;

        private static readonly Collider[] _overlap = new Collider[32];
        private static readonly RaycastHit[] _hits = new RaycastHit[16];
        private static readonly Crest.SampleHeightHelper _waterAtFeet = new Crest.SampleHeightHelper();
        private static readonly Crest.SampleHeightHelper _waterAtBody = new Crest.SampleHeightHelper();

        private static readonly AccessTools.FieldRef<PlayerCrouching, bool> CrouchingRef =
            AccessTools.FieldRefAccess<PlayerCrouching, bool>("crouching");

        private void Awake() { Instance = this; }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_renderHooked) Application.onBeforeRender -= OnBeforeRender;
            ResetRoll();
            if (_rag != null) _rag.Destroy();
        }

        /// <summary>True from the moment the player is knocked over until they start getting up.</summary>
        public static bool IsDown
        {
            get { return Instance != null && (Instance._phase == Phase.Falling || Instance._phase == Phase.Down); }
        }

        /// <summary>True while this has the controls: down, and getting back up.</summary>
        internal static bool HoldsView { get { return Instance != null && Instance._phase != Phase.None; } }

        /// <summary>True while the player is getting back up.</summary>
        internal static bool IsRising { get { return Instance != null && Instance._phase == Phase.Rising; } }

        /// <summary>
        /// The fallen body in the frame the deck is drawn in: the pelvis, each part's rotation (in <see cref="RagdollPart"/>
        /// order, <paramref name="rotations"/> at least <see cref="SyntyBody.RagdollParts"/> long), about where the floor is
        /// under it, and what the arms are doing.
        /// </summary>
        public static bool TryGetRagdoll(out Vector3 pelvis, Quaternion[] rotations, out float floorY, out FallReaction reaction)
        {
            pelvis = Vector3.zero; floorY = 0f; reaction = FallReaction.Limp;
            var d = Instance;
            if (d == null || !IsDown || d._rag == null || !d._rag.Full || rotations == null || rotations.Length < Ragdoll.Count) return false;
            d._rag.GetPose(out pelvis, rotations);
            floorY = d._rag.FloorVisualY();
            reaction = d._reaction;
            return true;
        }

        /// <summary>How far through getting back up the player is, 0 to 1. False when not getting up.</summary>
        public static bool TryGetGettingUp(out float progress01)
        {
            progress01 = 0f;
            var d = Instance;
            if (d == null || d._phase != Phase.Rising || !d._bodyGetsUp) return false;
            progress01 = Mathf.Clamp01((Time.time - d._phaseTime) / d._getUpSeconds);
            return true;
        }

        // ---- going down ---------------------------------------------------------------------------------

        /// <summary>
        /// Knock the local player over. <paramref name="impulse"/> is the push in meters per second, in the frame the
        /// deck renders in, pointing the way the body should go; <paramref name="seconds"/> is how long to lie there
        /// after landing. Hit again while already down, the body is pushed again and stays down at least that long.
        /// False, with the reason logged, when the player cannot fall right now.
        /// </summary>
        public bool GoDown(DownedReason reason, Vector3 impulse, float seconds)
        {
            if (_phase == Phase.Falling || _phase == Phase.Down)
            {
                if (_rag == null) return false;
                if (_rag.Kinematic)
                {
                    _rag.SetKinematic(false);
                    _phase = Phase.Falling;
                    _phaseTime = Time.time;
                    _stillFor = 0f;
                }
                _rag.Push(_rag.DirToPhysics(impulse));
                _downFor = Mathf.Max(_downFor, seconds);
                Plugin.Log.LogInfo($"[Downed] {reason} again while down");
                return true;
            }
            return Begin(reason, impulse, null, seconds);
        }

        private bool Begin(DownedReason reason, Vector3 impulseVisual, Vector3? fallVelocityPhysics, float seconds)
        {
            if (_phase == Phase.Rising) GiveBack();

            string why = CannotGoDownReason();
            if (why != null)
            {
                Plugin.Log.LogInfo($"[Downed] not going down ({reason}): {why}");
                return false;
            }

            var cc = Refs.charController;
            Transform observer = Refs.observerMirror.transform;
            Transform physics = cc.transform.parent, visual = observer.parent;
            if (physics == null || visual == null)
            {
                Plugin.Log.LogInfo($"[Downed] not going down ({reason}): no frame to fall in");
                return false;
            }

            // Where the controller stands, not where a seated player's view is.
            Vector3 feet = cc.transform.position - Vector3.up * VanillaPlayer.ControllerFeetGap();
            string blocked = Obstruction(feet + Vector3.up * 0.9f, Vector3.up, 1.7f, 0.18f);
            if (blocked != null)
            {
                Plugin.Log.LogInfo($"[Downed] not going down ({reason}): no room to fall, {blocked} is in the way");
                return false;
            }

            Vector3 facing = observer.forward;
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.forward;
            facing.Normalize();

            // The ragdoll takes the body's pose as it is right now, seated included, so it goes over from there.
            var body = PlayerModel.Body;
            string noRig = "no body";
            Ragdoll rag = null;
            if (body != null && body.RigReady && body.Fitted)
                rag = Ragdoll.Build(body, facing, visual, physics, out noRig);
            if (rag == null)
            {
                Plugin.Log.LogInfo($"[Downed] falling as a plain capsule: {noRig}");
                rag = Ragdoll.BuildCapsule(feet, facing, visual, physics);
            }

            if (Seating.IsSeated && Seating.Instance != null) Seating.Instance.StandUpNow("knocked down");
            LetGo();

            _rag = rag;
            _physics = physics;
            _visual = visual;
            _standLocal = cc.transform.localPosition;
            _reason = reason;
            _downFor = seconds;
            _stillFor = 0f;
            _viewInit = false;
            _floatUntil = 0f;
            _inWater = false;
            _impacts = 0;
            _peakSpeed = 0f;
            _getUpSeconds = GetUpSeconds;
            _lastPelvisSpeed = 0f;
            _rolledForStairs = false;
            _startPelvisLocalY = rag.Pelvis.transform.localPosition.y;

            Refs.SetPlayerControl(false);
            Refs.observerMirror.enabled = false;
            if (Refs.ovrCameraRig != null)
            {
                var crouch = Refs.ovrCameraRig.GetComponent<PlayerCrouching>();
                if (crouch != null) CrouchingRef(crouch) = false;
            }

            Vector3 pushDir = Vector3.forward;
            if (fallVelocityPhysics.HasValue)
            {
                rag.SetVelocity(fallVelocityPhysics.Value);
                _reaction = FallReaction.Flail;
            }
            else
            {
                Vector3 push = rag.DirToPhysics(impulseVisual);
                push.y = 0f;
                if (push.sqrMagnitude < 0.04f) push = -physics.TransformDirection(visual.InverseTransformDirection(facing)) * 1.2f;
                pushDir = push.normalized;
                rag.Push(push);
                _reaction = ChooseReaction(feet, pushDir);
            }

            _phase = Phase.Falling;
            _phaseTime = Time.time;
            if (!_renderHooked)
            {
                Application.onBeforeRender += OnBeforeRender;
                _renderHooked = true;
            }
            Plugin.Log.LogInfo($"[Downed] {reason}: going over as a {(rag.Full ? "ragdoll" : "capsule")} " +
                $"({(fallVelocityPhysics.HasValue ? fallVelocityPhysics.Value.magnitude : impulseVisual.magnitude):F1} m/s, " +
                $"down {seconds:F1} s, {(GameState.currentBoat != null ? "aboard" : "on land")}, arms {_reaction})");
            return true;
        }

        /// <summary>Why the player cannot be knocked over right now, or null.</summary>
        private static string CannotGoDownReason()
        {
            if (DownedTuning.Enabled == null || !DownedTuning.Enabled.Value) return "knocking down is switched off";
            if (!GameState.playing || GameState.currentlyLoading) return "not in the world";
            if (Refs.charController == null || Refs.observerMirror == null) return "no player";
            if (GameState.inBed || GameState.sleeping || GameState.recovering) return "in bed or asleep";
            if (GameState.currentShipyard != null) return "in the shipyard";
            if (PlayerSwimming.swimming) return "swimming";
            // A chart, a market or a blackout that took the controls during a seat keeps them after standing up. A control
            // held from the seat (a tiller, another mod's oars) is counted there too, but Begin stands the player up and lets
            // go of it, the same as for a standing player.
            if (Seating.ControlsHeldElsewhere && LocalInteraction.StickyControl(LocalInteraction.Pointer) == null)
                return "something else has the controls";
            // Sitting, and steering or cranking, both take the controls in the game's own way and are let go of first.
            if (!Refs.charController.enabled && !Seating.IsSeated && LocalInteraction.StickyControl(LocalInteraction.Pointer) == null)
                return "something else has the controls";
            return null;
        }

        /// <summary>
        /// What the arms do going over. Something to fall down (a companionway, a hatch, a drop behind) and the head gets
        /// covered most of the time; otherwise it is a toss-up between the face, the head, and nothing.
        /// </summary>
        private FallReaction ChooseReaction(Vector3 feetPhysics, Vector3 pushDir)
        {
            float roll = Random.value;
            if (DropAhead(feetPhysics, pushDir))
            {
                Plugin.Log.LogInfo("[Downed] there is a drop that way");
                return roll < 0.8f ? FallReaction.CoverHead : roll < 0.9f ? FallReaction.CoverFace : FallReaction.Limp;
            }
            return roll < 0.4f ? FallReaction.CoverFace : roll < 0.7f ? FallReaction.CoverHead : FallReaction.Limp;
        }

        /// <summary>Whether the ground falls away somewhere in the next two meters the push goes.</summary>
        private static bool DropAhead(Vector3 feetPhysics, Vector3 dir)
        {
            for (int i = 1; i <= 3; i++)
            {
                Vector3 from = feetPhysics + dir * (0.6f * i) + Vector3.up * 0.5f;
                float d = GroundBelow(from, 4f);
                if (d - 0.5f > 0.45f) return true;
            }
            return false;
        }

        /// <summary>Hands off everything. True when letting go of a control gave the controls back, so they have to be taken again.</summary>
        private static bool LetGo()
        {
            var pointer = LocalInteraction.Pointer;
            if (pointer == null) return false;
            var held = pointer.GetHeldItem();
            if (held != null)
            {
                held.OnDrop();
                pointer.DropItem();
            }
            var control = LocalInteraction.StickyControl(pointer);
            if (control == null) return false;
            control.UnStickyClick();
            pointer.UnStickyClick();
            return true;
        }

        // ---- per frame ----------------------------------------------------------------------------------

        private void Update()
        {
            try
            {
                if (_phase == Phase.None)
                {
                    HoldLook();
                    if (DownedTuning.Enabled == null || !DownedTuning.Enabled.Value) return;
                    if (!GameState.inCursorMenu && DownedTuning.TestKey.Value.IsDown() && Refs.observerMirror != null)
                        GoDown(DownedReason.Shoved, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward * 3.2f, 5f);
                    else
                        WatchForLongFall();
                    return;
                }

                string lost = LostReason();
                if (lost != null)
                {
                    Plugin.Log.LogInfo("[Downed] cut short: " + lost);
                    GiveBack();
                    return;
                }
                FollowFrames();

                // Nothing gets picked up or steered from the floor.
                if (LetGo()) Refs.SetPlayerControl(false);
                HoldLook();

                switch (_phase)
                {
                    case Phase.Falling: WatchFall(); break;
                    case Phase.Down:
                        if (_reaction != FallReaction.Limp && Time.time - _phaseTime > ReactionHoldSeconds) _reaction = FallReaction.Limp;
                        if (Time.time - _phaseTime >= Mathf.Max(_reason == DownedReason.Fell ? 0.2f : MinDownSeconds, _downFor)) BeginRise();
                        break;
                    case Phase.Rising: Rise(); break;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("[Downed] " + e);
                GiveBack();
            }
        }

        private void FixedUpdate()
        {
            if (_phase != Phase.Falling || _rag == null) return;
            if (_inWater) _rag.Float(_waterY, Time.fixedDeltaTime);
            else _rag.CatchTunneling();
        }

        private void LateUpdate()
        {
            if (_phase != Phase.None) PinView();
            // Dark at the knock, lightening while down and getting up, and gone within a second and a half of standing.
            float clear = _phase == Phase.Falling ? 0.05f : _phase == Phase.Down ? 0.16f : _phase == Phase.Rising ? 0.22f
                : Mathf.Max(0.7f, _daze.Level / 1.5f);
            _daze.Tick(clear, Time.unscaledDeltaTime);
        }

        /// <summary>
        /// In first person the view is the fallen body's, so the mouse does nothing until the player is back up. In the
        /// orbit camera the mouse moves the camera as usual. Handed back after any pause menu has closed, because the
        /// menu puts back whatever mouse look it found when it opened.
        /// </summary>
        private void HoldLook()
        {
            bool want = _phase != Phase.None && !BoatCamera.on;
            if (want && !_lookTaken && !GameState.inCursorMenu && MouseLook.MouseLookIsEnabled())
            {
                MouseLook.ToggleMouseLook(false);
                _lookTaken = true;
            }
            else if (!want && _lookTaken && !GameState.inCursorMenu)
            {
                MouseLook.ToggleMouseLook(true);
                _lookTaken = false;
            }
        }

        private string LostReason()
        {
            if (!GameState.playing || Refs.charController == null || Refs.observerMirror == null) return "left the world";
            if (GameState.inBed || GameState.sleeping || GameState.recovering || GameState.currentShipyard != null) return "the game took over";
            if (Refs.charController.enabled) return "something else gave back control";
            if (_phase != Phase.Rising && _rag == null) return "the body was removed";
            if (Refs.charController.transform.parent == null || Refs.observerMirror.transform.parent == null) return "the player has no frame";
            return null;
        }

        /// <summary>
        /// The game moved the player to another frame mid-fall (off a boat onto the dock or into the sea, onto another
        /// boat): the body goes with them, each part where it was on screen.
        /// </summary>
        private void FollowFrames()
        {
            Transform visual = Refs.observerMirror.transform.parent, physics = Refs.charController.transform.parent;
            if (visual == _visual && physics == _physics) return;
            Plugin.Log.LogInfo($"[Downed] the game moved the player from '{(_physics != null ? _physics.name : "none")}' to '{physics.name}'; the body goes with them");
            if (_rag != null)
            {
                _rag.MoveToFrames(visual, physics);
                _startPelvisLocalY = _rag.Pelvis.transform.localPosition.y;
            }
            _visual = visual;
            _physics = physics;
            _standLocal = Refs.charController.transform.localPosition;
        }

        private void WatchFall()
        {
            float dt = Time.deltaTime;
            float elapsed = Time.time - _phaseTime;
            Vector3 pelvis = _rag.PelvisVisual;

            // In the sea, and not inside a hull (whose hold can sit below the waterline): the body floats a moment, then
            // the game's swimming takes over.
            float water = 0f;
            if (_inWater)
            {
                if (WaterHeight(_waterAtBody, pelvis, out water)) _waterY = water;
                float inFor = Time.time - _waterTime;
                // Up to the surface first, however deep it went in, then a few seconds bobbing there.
                if (_floatUntil <= 0f && inFor > 0.3f && pelvis.y > _waterY - 0.35f)
                {
                    _floatUntil = Time.time + Random.Range(2.5f, 4.5f);
                    Plugin.Log.LogInfo($"[Downed] surfaced after {inFor:F1} s, floating {_floatUntil - Time.time:F1} s");
                }
                if ((_floatUntil > 0f && Time.time >= _floatUntil) || inFor > 15f) Splash(pelvis);
                return;
            }
            if (_visual == _physics && WaterHeight(_waterAtBody, pelvis, out water) && pelvis.y < water - 0.2f)
            {
                _inWater = true;
                _waterTime = Time.time;
                _floatUntil = 0f;
                _waterY = water;
                _reaction = FallReaction.Limp;
                _daze.Hit(Mathf.Clamp01((_rag.PelvisVelocity.magnitude - 4f) / 12f) * 0.8f);
                Plugin.Log.LogInfo($"[Downed] into the water at {_rag.PelvisVelocity.magnitude:F1} m/s");
                return;
            }

            Vector3 v = _rag.PelvisVelocity;
            float speed = v.magnitude;
            float jolt = _lastPelvisSpeed - speed;
            if (jolt > 1.8f)
            {
                _impacts++;
                _daze.Hit(0.45f + (jolt - 1.8f) / 7f);
            }
            _lastPelvisSpeed = speed;
            _peakSpeed = Mathf.Max(_peakSpeed, speed);
            if (_reaction != FallReaction.Flail && v.y < -5.5f)
            {
                _reaction = FallReaction.Flail;
                Plugin.Log.LogInfo("[Downed] falling a long way, arms out");
            }
            if (!_rolledForStairs && _reaction != FallReaction.Flail && _impacts >= 2
                && _startPelvisLocalY - _rag.Pelvis.transform.localPosition.y > 0.9f)
            {
                _rolledForStairs = true;
                float roll = Random.value;
                _reaction = roll < 0.8f ? FallReaction.CoverHead : roll < 0.9f ? FallReaction.CoverFace : FallReaction.Limp;
                Plugin.Log.LogInfo($"[Downed] tumbling down something, arms {_reaction}");
            }

            _stillFor = _rag.IsStill() ? _stillFor + dt : 0f;
            if (_stillFor > StillSeconds && elapsed > 0.8f) Land("landed");
            else if (elapsed > MaxFallSeconds) Land("still falling after " + MaxFallSeconds + " s, stopping it there");
        }

        private void Land(string how)
        {
            if (_reaction == FallReaction.Flail) _reaction = FallReaction.Limp;
            _rag.SetKinematic(true);
            _phase = Phase.Down;
            _phaseTime = Time.time;
            // Knocked over is always a knock, even onto something soft enough not to register as one.
            if (_reason != DownedReason.Fell) _daze.Hit(0.55f);
            // The harder the landing, the longer to come round and the slower to get up. A fall nobody pushed them into
            // lies there only as long as it hurt; a shove keeps the time it was given.
            float hard = Mathf.Clamp01((_peakSpeed - 4f) / 10f);
            if (_reason == DownedReason.Fell) _downFor = Mathf.Lerp(0.3f, 6f, hard);
            _getUpSeconds = Mathf.Lerp(1.2f, 2.6f, hard);
            Plugin.Log.LogInfo($"[Downed] {how}: {_impacts} impact(s), {_startPelvisLocalY - _rag.Pelvis.transform.localPosition.y:F1} m below where it started, " +
                $"hit at {_peakSpeed:F1} m/s, down {Mathf.Max(_downFor, 0.2f):F1} s, getting up over {_getUpSeconds:F1} s");
        }

        /// <summary>The body went into the sea: put the controller there and let the game's swimming have the player.</summary>
        private void Splash(Vector3 pelvisVisual)
        {
            Plugin.Log.LogInfo("[Downed] into the water");
            Refs.charController.transform.position = _physics.TransformPoint(_visual.InverseTransformPoint(pelvisVisual));
            GiveBack();
        }

        /// <summary>The view on the ragdoll's head, or on the body's head while it gets up, easing onto the standing view at the end.</summary>
        private void PinView()
        {
            Transform observer = Refs.observerMirror != null ? Refs.observerMirror.transform : null;
            if (observer == null) return;
            if ((_phase == Phase.Falling || _phase == Phase.Down) && _rag != null)
            {
                Vector3 eye, up;
                _rag.GetEye(out eye, out up);
                observer.position += eye - Seating.EyeWorld(observer);
            }
            else if (_phase == Phase.Rising && _bodyGetsUp && Refs.charController != null)
            {
                var body = PlayerModel.Body;
                Vector3 eye, up;
                Vector3 standing = _visual.TransformPoint(Refs.charController.transform.localPosition);
                if (body == null || !body.TryGetEye(out eye, out up)) { observer.position = standing; return; }
                float t = Mathf.Clamp01((Time.time - _phaseTime) / _getUpSeconds);
                float toStanding = Smooth01((t - 0.55f) / 0.45f);
                Vector3 onHead = observer.position + (eye - Seating.EyeWorld(observer));
                observer.position = Vector3.Lerp(onHead, standing, toStanding);
            }
        }

        // ---- getting up ---------------------------------------------------------------------------------

        private void BeginRise()
        {
            var cc = Refs.charController;
            Vector3 spot;
            string why = FindStandSpot(out spot);
            if (why == null)
            {
                cc.transform.localPosition = spot;
                Plugin.Log.LogInfo("[Downed] getting up where the body landed");
            }
            else
            {
                cc.transform.localPosition = _standLocal;
                Plugin.Log.LogInfo("[Downed] getting up where the fall started: " + why);
            }
            _bodyGetsUp = _rag.Full && PlayerModel.Body != null;
            _rag.Destroy();
            _rag = null;
            _riseFrom = Refs.observerMirror.transform.localPosition;
            _phase = Phase.Rising;
            _phaseTime = Time.time;
        }

        private void Rise()
        {
            float seconds = _bodyGetsUp ? _getUpSeconds : CapsuleRiseSeconds;
            float t = Mathf.Clamp01((Time.time - _phaseTime) / seconds);
            if (!_bodyGetsUp)
                Refs.observerMirror.transform.localPosition = Vector3.Lerp(_riseFrom, Refs.charController.transform.localPosition, Smooth01(t));
            if (t >= 1f)
            {
                GiveBack();
                Plugin.Log.LogInfo("[Downed] back on their feet");
            }
        }

        /// <summary>
        /// Where the controller goes to stand the player up where the body lies, as a local position in the frame it
        /// walks in: solid, level enough ground under the hips (or the chest, or the head), and room for the controller
        /// there. Null reason when found.
        /// </summary>
        private string FindStandSpot(out Vector3 spotLocal)
        {
            spotLocal = _standLocal;
            if (_rag == null) return "the body is gone";
            var cc = Refs.charController;
            string last = "nothing solid under the body";
            int[] tryParts = _rag.Full ? new[] { (int)RagdollPart.Pelvis, (int)RagdollPart.Chest, (int)RagdollPart.Head } : new[] { 0 };
            foreach (int part in tryParts)
            {
                Vector3 from = PartPosition(part) + Vector3.up * 0.6f;
                RaycastHit ground;
                if (!GroundHit(from, 2.2f, out ground)) continue;
                if (ground.normal.y < 0.7f) { last = "too steep where it landed"; continue; }
                Vector3 feet = ground.point;
                string blocked = Obstruction(feet + Vector3.up * (cc.height * 0.5f + 0.04f), Vector3.up, cc.height, cc.radius * 0.95f);
                if (blocked != null) { last = "no room to stand, " + blocked + " is in the way"; continue; }
                spotLocal = _physics.InverseTransformPoint(feet + Vector3.up * (VanillaPlayer.ControllerFeetGap() + 0.03f));
                return null;
            }
            return last;
        }

        private Vector3 PartPosition(int part)
        {
            return _rag.Full ? _rag.PartTransform(part).position : _rag.Pelvis.transform.position;
        }

        private void GiveBack()
        {
            if (_rag != null) _rag.Destroy();
            _rag = null;
            ResetRoll();
            _phase = Phase.None;
            _floatUntil = 0f;
            _inWater = false;
            _haveLast = false;
            HoldLook();
            if (Refs.observerMirror != null) Refs.observerMirror.enabled = true;
            if (Refs.charController != null && GameState.playing && !GameState.inBed && !GameState.sleeping && !GameState.recovering)
                Refs.SetPlayerControl(true);
        }

        // ---- long falls ---------------------------------------------------------------------------------

        /// <summary>
        /// While the player is coming down, how long until they land, from how fast they are dropping, how hard the game's
        /// own gravity is pulling (measured from the fall, since it is floatier than real life), and what is under them (the
        /// sea included). A fall that will take more than a second coming down, and lands well below where they left the
        /// ground, goes over as a ragdoll at the speed they already had. Only the way down counts: the rise of a jump does
        /// not, and neither does jumping back onto the level you jumped from.
        /// </summary>
        private void WatchForLongFall()
        {
            var cc = Refs.charController;
            if (PlayerSwimming.swimming) _lastSwimTime = Time.time;
            // Jumping up out of the water and dropping back in is swimming, not a fall.
            bool justSwam = Time.time - _lastSwimTime < 1.5f;
            if (DownedTuning.RagdollLongFalls == null || !DownedTuning.RagdollLongFalls.Value || cc == null || !cc.enabled
                || !GameState.playing || GameState.currentlyLoading || GameState.inBed || GameState.sleeping
                || GameState.currentShipyard != null || justSwam || Seating.IsSeated || Refs.observerMirror == null)
            {
                _haveLast = false;
                return;
            }

            float dt = Time.deltaTime;
            Transform parent = cc.transform.parent;
            Vector3 local = cc.transform.localPosition;
            if (!_haveLast || parent != _lastParent || dt <= 0f)
            {
                _lastLocal = local;
                _lastParent = parent;
                _haveLast = true;
                _takeoffY = local.y;
                _fallTime = 0f;
                _lastVy = 0f;
                return;
            }
            Vector3 vLocal = (local - _lastLocal) / dt;
            _lastLocal = local;
            if (vLocal.sqrMagnitude > 60f * 60f || cc.isGrounded)
            {
                _takeoffY = local.y;
                _fallTime = 0f;
                _lastVy = 0f;
                return;
            }

            Vector3 v = parent != null ? parent.TransformVector(vLocal) : vLocal;
            // The pull of gravity as this fall is actually going, eased.
            if (_lastVy != 0f)
            {
                float pull = (_lastVy - v.y) / dt;
                if (pull > 0.5f && pull < 40f) _gravity = Mathf.Lerp(_gravity, pull, 1f - Mathf.Exp(-6f * dt));
            }
            _lastVy = v.y;
            if (v.y > -0.5f) { _fallTime = 0f; return; }
            _fallTime += dt;
            if (v.y > -1.5f) return;

            Vector3 feet = cc.transform.position - Vector3.up * VanillaPlayer.ControllerFeetGap();
            string onto;
            float drop = GroundBelow(feet + Vector3.up * 0.3f, 80f, out onto) - 0.3f;
            Vector3 feetVisual = Refs.observerMirror.transform.position - Vector3.up * VanillaPlayer.ControllerFeetGap();
            float water;
            bool aboard = Refs.observerMirror.transform.parent != parent;
            if ((!aboard || onto == null) && WaterHeight(_waterAtFeet, feetVisual, out water))
            {
                // Already at the surface: a hop in the water.
                if (feetVisual.y < water + 0.6f) return;
                if (feetVisual.y - water < drop)
                {
                    drop = feetVisual.y - water;
                    onto = "the sea";
                }
            }
            drop = Mathf.Max(0f, drop);
            float belowTakeoff = _takeoffY - local.y + drop;

            float g = Mathf.Clamp(_gravity, 2f, 30f);
            float s = -v.y;
            float remaining = (-s + Mathf.Sqrt(s * s + 2f * g * drop)) / g;
            // A long way down, or a leap across that lands hard somewhere lower: fast forward and fast down together.
            float across = new Vector2(v.x, v.z).magnitude;
            float impact = Mathf.Sqrt(across * across + s * s + 2f * g * drop);
            bool longWay = _fallTime + remaining > LongFallSeconds && belowTakeoff >= LongFallMinDrop;
            bool leap = impact >= LeapImpactSpeed && belowTakeoff >= LeapMinDrop;
            if (!longWay && !leap) return;

            if (Time.time < _nextLongFallTry) return;
            Plugin.Log.LogInfo($"[Downed] {(longWay ? "a long fall" : "a hard leap")}: {_fallTime:F2} s coming down, {remaining:F2} s and {drop:F1} m to go onto {onto ?? "nothing"} " +
                $"at {s:F1} m/s down and {across:F1} m/s across (gravity {g:F1}, landing at {impact:F1} m/s), {belowTakeoff:F1} m below where they left the ground");
            // Refused (no room right beside a mast, say): try again a little further down, not every frame.
            if (!Begin(DownedReason.Fell, Vector3.zero, v, 0f)) _nextLongFallTry = Time.time + 0.25f;
        }

        // ---- the view ------------------------------------------------------------------------------------

        /// <summary>
        /// In first person the player sees what the fallen body's face points at, eased so a flopping head does not
        /// shake the picture, with most of its tilt; getting up hands the view back to where the player was looking
        /// before the fall. The view matrix is set rather than the camera turned, because the game's UI and outline
        /// cameras hang off the eye camera and would turn with it.
        /// </summary>
        private void OnBeforeRender()
        {
            try
            {
                ResetRoll();
                if (_phase == Phase.None || BoatCamera.on) { _viewInit = false; return; }
                PinView();
                Camera cam = Camera.main;
                if (cam == null) return;

                Vector3 eye, headUp, headFwd;
                float weight = 1f;
                if (_rag != null)
                {
                    _rag.GetEye(out eye, out headUp);
                    headFwd = _rag.HeadForwardVisual();
                }
                else if (_phase == Phase.Rising && _bodyGetsUp && PlayerModel.Body != null && PlayerModel.Body.TryGetEye(out eye, out headUp))
                {
                    headFwd = PlayerModel.Body.HeadForward;
                    weight = 1f - Smooth01(((Time.time - _phaseTime) / _getUpSeconds - 0.35f) / 0.65f);
                }
                else return;

                Vector3 upHint = Vector3.Slerp(Vector3.up, headUp, 0.6f);
                if (Vector3.Cross(headFwd, upHint).sqrMagnitude < 1e-4f) upHint = headUp;
                if (headFwd.sqrMagnitude < 1e-6f || Vector3.Cross(headFwd, upHint).sqrMagnitude < 1e-6f) return;
                Quaternion head = Quaternion.LookRotation(headFwd, upHint);
                if (!_viewInit)
                {
                    _view = cam.transform.rotation;
                    _viewInit = true;
                }
                _view = Quaternion.Slerp(_view, head, 1f - Mathf.Exp(-7f * Time.unscaledDeltaTime));
                Quaternion shown = Quaternion.Slerp(cam.transform.rotation, _view, weight);
                if (Quaternion.Angle(shown, cam.transform.rotation) < 0.05f) return;
                cam.worldToCameraMatrix = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(cam.transform.position, shown, Vector3.one).inverse;
                _rolledCam = cam;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("[Downed] view: " + e);
                Application.onBeforeRender -= OnBeforeRender;
                _renderHooked = false;
                ResetRoll();
            }
        }

        private void ResetRoll()
        {
            if (_rolledCam != null) _rolledCam.ResetWorldToCameraMatrix();
            _rolledCam = null;
        }

        // ---- physics helpers ----------------------------------------------------------------------------

        /// <summary>
        /// The sea's height under a point in the drawn frame, from <paramref name="sampler"/>. False with no ocean to ask.
        ///
        /// Crest answers a query a frame or more later, keyed by the sampler, so each kind of point (the player's feet, a
        /// fallen body's hips) keeps its own sampler rather than one being moved between them, which left every query
        /// unanswered. Until an answer arrives the calm sea level stands in: a wave's height off, not "no sea at all",
        /// which is what once read a swimmer as twelve meters above the seabed and ragdolled every jump out of the water.
        /// </summary>
        private static bool WaterHeight(Crest.SampleHeightHelper sampler, Vector3 visualWorld, out float height)
        {
            height = 0f;
            try
            {
                var ocean = Crest.OceanRenderer.Instance;
                if (ocean == null) return false;
                sampler.Init(visualWorld, 0f, true);
                if (!sampler.Sample(out height)) height = ocean.SeaLevel;
                return true;
            }
            catch { return false; }
        }

        /// <summary>How far down from <paramref name="from"/> the first solid thing is, up to <paramref name="max"/>.</summary>
        private static float GroundBelow(Vector3 from, float max)
        {
            string onto;
            return GroundBelow(from, max, out onto);
        }

        private static float GroundBelow(Vector3 from, float max, out string onto)
        {
            RaycastHit hit;
            bool found = GroundHit(from, max, out hit);
            onto = found ? "'" + hit.collider.name + "'" : null;
            return found ? hit.distance : max;
        }

        private static bool GroundHit(Vector3 from, float max, out RaycastHit best)
        {
            best = default(RaycastHit);
            int n = UnityEngine.Physics.SphereCastNonAlloc(from, 0.15f, Vector3.down, _hits, max, Seating.SeatLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i].collider;
                if (c == null || IgnoredForRoom(c)) continue;
                if (_hits[i].distance < nearest) { nearest = _hits[i].distance; best = _hits[i]; }
            }
            return best.collider != null;
        }

        /// <summary>
        /// The name of something solid inside a capsule of the given size standing at <paramref name="center"/>, or
        /// null if there is room. Loose items are left out: they get pushed aside, not stuck in.
        /// </summary>
        private static string Obstruction(Vector3 center, Vector3 up, float height, float radius)
        {
            float half = Mathf.Max(0f, height * 0.5f - radius);
            int n = UnityEngine.Physics.OverlapCapsuleNonAlloc(center + up * half, center - up * half, radius, _overlap, Seating.SeatLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _overlap[i];
                if (c == null || IgnoredForRoom(c)) continue;
                return c.name;
            }
            return null;
        }

        internal static bool IgnoredForRoom(Collider c)
        {
            if (c.isTrigger) return true;
            if (Refs.charController != null && c.transform.IsChildOf(Refs.charController.transform)) return true;
            if (Refs.observerMirror != null && c.transform.IsChildOf(Refs.observerMirror.transform)) return true;
            if (Ragdoll.IsPart(c)) return true;
            if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) return true;
            if (c.gameObject.layer == 2 && !Seating.IsStaticScenery(c)) return true;
            return false;
        }

        private static float Smooth01(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }
    }
}

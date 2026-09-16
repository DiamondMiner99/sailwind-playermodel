using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>Live tuning for sitting. Every value applies immediately from the F1 menu.</summary>
    public static class SeatingTuning
    {
        private const string Section = "7. Seating";

        public static ConfigEntry<bool> Enabled { get; private set; }
        public static ConfigEntry<KeyboardShortcut> SitKey { get; private set; }
        public static ConfigEntry<bool> ShowSeatedLabel { get; private set; }
        public static ConfigEntry<float> SettleSeconds { get; private set; }
        public static ConfigEntry<float> EyeAboveSeat { get; private set; }
        public static ConfigEntry<bool> StovesBurn { get; private set; }
        public static ConfigEntry<bool> ShowBodyWhenSeated { get; private set; }
        public static ConfigEntry<float> BodyFadeBelowShoulders { get; private set; }
        public static ConfigEntry<float> BodyFadeAboveHips { get; private set; }

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "Enabled", true,
                "Sit on chairs (right-click a chair that is set down) and on ledges, rails, crates, benches and spars (the sit key). Any movement key or jump stands you up.");
            SitKey = cfg.Bind(Section, "SitKey", new KeyboardShortcut(KeyCode.X),
                "Sit on whatever you are looking at, if it is at seat height, or on the floor. Pressed again while sitting on a ledge or rail, swings your legs over to the other side; on a spar such as the bowsprit, switches between straddling it and sitting with both legs on one side; on the floor, changes how you sit.");
            ShowSeatedLabel = cfg.Bind(Section, "ShowSeatedLabel", true,
                "Show a dim (seated) label at the bottom of the screen while sitting in first person.");
            SettleSeconds = cfg.Bind(Section, "SettleSeconds", 0.45f,
                new ConfigDescription("How long sitting down takes, in seconds.", new AcceptableValueRange<float>(0.1f, 1.5f)));
            EyeAboveSeat = cfg.Bind(Section, "EyeAboveSeat", 0.78f,
                new ConfigDescription("Height of your view above the seat while sitting, in meters.", new AcceptableValueRange<float>(0.5f, 1.1f)));
            StovesBurn = cfg.Bind(Section, "StovesBurn", true,
                "Sit on a lit stove for too long and your pants start smoking, then catch fire and throw you off it.");
            ShowBodyWhenSeated = cfg.Bind(Section, "ShowBodyWhenSeated", true,
                "While sitting in first person, show your own legs and body without the arms, fading out from the hips up to the chest.");
            BodyFadeBelowShoulders = cfg.Bind(Section, "BodyFadeBelowShoulders", 0.05f,
                new ConfigDescription("Where your seated body is fully see-through, in meters below the shoulders.", new AcceptableValueRange<float>(-0.2f, 0.6f)));
            BodyFadeAboveHips = cfg.Bind(Section, "BodyFadeAboveHips", 0f,
                new ConfigDescription("Where your seated body is fully solid, in meters above the hip joints. Everything lower shows completely.", new AcceptableValueRange<float>(-0.3f, 0.5f)));
        }
    }

    /// <summary>How a seated body sits: where its legs go and, on the floor, what its arms do.</summary>
    public enum SeatPose : byte
    {
        FeetDown = 0,           // feet on the floor in front
        Dangle = 1,             // hanging over a drop
        Straddle = 2,           // a leg down each side of a spar
        FloorLegsOut = 3,       // on the floor: legs out in front, leaning back on both hands
        FloorCrossLegged = 4,   // on the floor: cross-legged, hands on the knees
        FloorKneeUp = 5,        // on the floor: right knee up with the arm over it, left leg out
        FloorKneesHugged = 6,   // on the floor: both knees up, arms round the shins
    }

    /// <summary>
    /// The local player sitting down: on a chair (right-click it, the way the game's beds work) or on anything at
    /// seat height (the sit key). Held items and looking around keep working; any movement key or jump stands you
    /// back up where you sat down from.
    ///
    /// BUILT ON THE GAME'S OWN BED. Sleep.EnterBed turns off the physics controller and the observer mirror,
    /// then pins the observer (the visual player the camera hangs off) to the bed every frame. The physics
    /// controller stays frozen where the player was standing, in the boat's own frame, so turning both back on
    /// returns the player to that spot however far the boat has sailed. Sitting does the same, except that it
    /// leaves mouse look and the pointer alone, so items can still be used.
    ///
    /// TWO COPIES OF EVERY BOAT. The ship you see has almost no solid colliders: its items' own colliders
    /// (triggers) and a copy of the hull the game makes for cleaning (HullPlayerCollider, layer 12). The deck,
    /// rails, masts, crosstrees and bowsprit the player actually walks on belong to the boat's walking copy
    /// (layer 8), which sails somewhere else entirely, and the physics controller walks on that copy. The two
    /// share local coordinates (PlayerControllerMirror copies the controller's local position straight onto the
    /// observer), so every seat raycast is also cast through the walking copy, mapped through the boat's local
    /// space, and a seat found there is kept in the visible boat's local space.
    ///
    /// Every seat position is kept in its anchor's frame (the chair, the boat, or the collider sat on), so the
    /// seat rides with a boat, a rolling deck, or a chair sliding across it.
    /// </summary>
    public class Seating : MonoBehaviour
    {
        public static Seating Instance { get; private set; }

        private sealed class Seat
        {
            public Transform Anchor;
            public PickupableItem Item;     // the chair, crate or barrel, when sitting on one
            public Transform Ignore;        // what is sat on, left out of the probes that follow
            public bool OnBoat;             // anchored to the boat itself, through its walking copy
            public Vector3 LocalSurface;    // the seat surface under the hips, anchor-local
            public Vector3 LocalForward;    // which way the player faces, anchor-local
            public Vector3 LocalEdge;       // the edge the legs go over (ledge seats)
            public Vector3 LocalAxis;       // along the spar (spar seats)
            public bool Edge;               // a ledge seat, whose legs can swing to the other side
            public bool Spar;               // a spar seat, switching between straddling and one side
            public bool Floor;              // sitting on the floor itself, cycling through the floor poses
            public float SparWidth;
            public SeatPose Pose;
            public float FloorBelow;        // meters from the seat surface down to the floor the feet rest on
        }

        /// <summary>A raycast hit in the visible frame, whichever copy of the boat it was found on.</summary>
        private struct Hit
        {
            public Vector3 Point, Normal;
            public float Distance;
            public Collider Collider;
            public bool Walk;       // on the boat's walking copy, and brought back to the visible boat
        }

        private struct SparShape
        {
            public Vector3 Center;  // the crown, halfway across
            public Vector3 Axis;    // level, along the spar
            public Vector3 Across;  // level, across it
            public float Width;
        }

        private const float HipAboveSurface = 0.10f;   // the hip joint over the surface it sits on
        private const float HipSetback = 0.12f;        // hips in from the edge the legs go over
        private const float LegReach = 0.62f;          // a floor farther below the seat than this: legs dangle
        private const float RiseSeconds = 0.3f;
        // A top counts as level up to about 50 degrees of slope, so a rounded rail's crown and a bevelled ledge
        // can be sat on.
        private const float LevelNormal = 0.65f;
        // Highest seat above the feet: a balcony or quarterdeck rail is about a meter and a bit.
        private const float MaxSeatHeight = 1.25f;
        // A spar is at most this wide across its top, at least this long, and has nothing this far under either
        // side. A gunwale is as narrow, but the deck is right under its inboard side, so it stays a ledge.
        private const float SparMaxWidth = 0.34f;
        private const float SparMinLength = 0.7f;
        private const float SparOpenBelow = 0.9f;
        // A spar looked at within about 50 degrees of its length is straddled; looked at across, sat on sideways.
        private const float StraddleLookDot = 0.64f;

        private Seat _seat;
        private float _sitTime;
        private Vector3 _startEyeLocal;
        private float _startYaw, _targetYaw;
        private bool _settleYaw;
        private bool _rising;
        private float _riseTime;
        private Vector3 _riseFrom;
        private bool _controlTaken;

        // Turning the view with the body when the legs swing over or the seat changes: added on top of the
        // player's own mouse look, eased, so they can keep looking around during it.
        private float _turnGoal, _turnDone, _turnVel;
        // A swing the long way round goes back the way the last one came, so the legs pass over the same side.
        // SyntyBody turns the body by the same rule.
        private float _swingSign = 1f;

        private string _hint;
        private float _hintUntil;
        private GUIStyle _labelStyle;

        // The hot seat: seconds sat on a lit stove (cooling off twice as fast once off it), when the pants stop
        // burning, and a hop off the stove owed once control is back.
        private const float SmokeAfterSeconds = 3f;
        private const float IgniteAfterSeconds = 6f;     // the pants catch fire while still sitting...
        private const float ThrowAfterSeconds = 9f;      // ...and a few seconds later the player jumps off
        private const float BurnSeconds = 4f;            // still burning this long after getting off
        private float _heat;
        private float _burnUntil;
        private bool _smokeHinted;
        private bool _hopOwed;
        private float _smoke01, _fire01, _steam01;
        // Rain on the stove: the most it slows the heating (heavy rain takes about three times as long to catch)
        // and how much faster it burns out a fire that caught anyway.
        private const float RainHeatingFloor = 0.3f;
        private const float RainBurnsOutFaster = 1.5f;
        private StoveFuelTrigger[] _stoveFuel;
        private PickupableItem _stoveFuelOf;

        private static readonly AccessTools.FieldRef<PlayerCrouching, bool> CrouchingRef =
            AccessTools.FieldRefAccess<PlayerCrouching, bool>("crouching");
        private static readonly AccessTools.FieldRef<StoveFuelTrigger, int> CurrentFuelRef =
            AccessTools.FieldRefAccess<StoveFuelTrigger, int>("currentFuel");
        private static readonly AccessTools.FieldRef<OVRPlayerController, Vector3> MoveThrottleRef =
            AccessTools.FieldRefAccess<OVRPlayerController, Vector3>("MoveThrottle");

        /// <summary>Smoke coming off the local player's pants, 0 to 1.</summary>
        public static float Smoke01 { get { return Instance != null ? Instance._smoke01 : 0f; } }

        /// <summary>Flames on the local player's pants, 0 to 1.</summary>
        public static float Fire01 { get { return Instance != null ? Instance._fire01 : 0f; } }

        /// <summary>Steam off the local player's pants where rain meets the heat, 0 to 1.</summary>
        public static float Steam01 { get { return Instance != null ? Instance._steam01 : 0f; } }

        private void Awake() { Instance = this; }
        private void OnDestroy() { if (Instance == this) Instance = null; }

        /// <summary>True while the local player is sitting.</summary>
        public static bool IsSeated { get { return Instance != null && Instance._seat != null; } }

        /// <summary>
        /// Where the local player's hips are, which way they face, where the legs go, and the height of the floor
        /// under the feet. False when not sitting.
        /// </summary>
        public static bool TryGetSeat(out Vector3 hips, out Vector3 forward, out SeatPose pose, out float floorY)
        {
            hips = forward = Vector3.zero; pose = SeatPose.FeetDown; floorY = 0f;
            var s = Instance != null ? Instance._seat : null;
            if (s == null || s.Anchor == null) return false;
            Vector3 surface = s.Anchor.TransformPoint(s.LocalSurface);
            hips = surface + Vector3.up * HipAboveSurface;
            forward = Flat(s.Anchor.TransformDirection(s.LocalForward));
            pose = s.Pose;
            floorY = surface.y - s.FloorBelow;
            return true;
        }

        /// <summary>
        /// Where the local player lies in one of the game's beds: the head, the direction the feet point along the
        /// bed, and the direction the chest faces. False when not in a bed.
        ///
        /// The bed's first child ("sleep pos") sits at the pillow end facing the foot of the bed, and the game
        /// pins the view there, so the head is placed from the view itself.
        /// </summary>
        public static bool TryGetLying(out Vector3 head, out Vector3 along, out Vector3 up)
        {
            head = along = up = Vector3.zero;
            Transform bed = GameState.inBed;
            if (bed == null) return false;
            Transform sleepPos = bed.childCount > 0 ? bed.GetChild(0) : bed;
            up = sleepPos.up;
            along = Vector3.ProjectOnPlane(sleepPos.forward, up);
            if (along.sqrMagnitude < 1e-6f) return false;
            along.Normalize();
            Vector3 eyePos = Refs.observerMirror != null ? EyeWorld(Refs.observerMirror.transform) : sleepPos.position;
            // The head bone sits a little below and behind the eyes of a face turned to the sky.
            head = eyePos - up * 0.08f - along * 0.03f;
            return true;
        }

        // ---- per frame ---------------------------------------------------------------------------------

        private void Update()
        {
            try
            {
                if (!GameState.playing || Refs.observerMirror == null || Refs.charController == null)
                {
                    if (_seat != null || _rising) FinishStand();
                    _seat = null;
                    _heat = _smoke01 = _fire01 = _steam01 = 0f;
                    _burnUntil = 0f;
                    return;
                }
                if (HotSeat()) return;
                if (_rising) { Rise(); return; }

                bool keys = !GameState.inCursorMenu;
                if (_seat == null)
                {
                    if (keys && SeatingTuning.Enabled.Value && SeatingTuning.SitKey.Value.IsDown()) TrySitHere();
                    return;
                }

                string why = StillSeatedReason();
                if (why != null)
                {
                    Plugin.Log.LogInfo("[Seating] stood up: " + why);
                    StandUp(false);
                    return;
                }
                if (keys && (GameInput.GetKeyDown(InputName.MoveUp) || GameInput.GetKeyDown(InputName.MoveDown) ||
                             GameInput.GetKeyDown(InputName.MoveLeft) || GameInput.GetKeyDown(InputName.MoveRight) ||
                             GameInput.GetKeyDown(InputName.Jump)))
                {
                    StandUp(true);
                    return;
                }
                if (keys && SeatingTuning.SitKey.Value.IsDown())
                {
                    if (_seat.Spar) ToggleSpar();
                    else if (_seat.Edge) TrySwingLegs();
                    else if (_seat.Floor) NextFloorPose();
                }
                if (_seat != null) Pin();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("[Seating] " + e);
                if (_seat != null || _rising) FinishStand();
                _seat = null;
            }
        }

        /// <summary>
        /// Sitting on a lit stove: after a few seconds the pants smoke, a few more and they catch fire, and a few more
        /// with them burning throws the player off the stove with a hop, still burning a little while. Water puts it
        /// out. Rain, out from under a roof, slows the heating, turns the smoke to steam, and burns out a fire sooner.
        /// True on the frame the player is thrown off, which ends that frame's seating update.
        /// </summary>
        private bool HotSeat()
        {
            float dt = Time.deltaTime;
            // GameState.rainIntensity runs 0 to 10 (the game's rain sounds peak at 4, 7 and 10); heavy rain counts fully.
            float wet = GameState.indoors ? 0f : Mathf.Clamp01(GameState.rainIntensity / 8f);
            if (_hopOwed && Refs.charController.enabled && Refs.ovrController != null)
            {
                _hopOwed = false;
                MoveThrottleRef(Refs.ovrController) += Vector3.up * (Refs.ovrController.transform.lossyScale.y * Refs.ovrController.JumpForce * 1.4f);
            }

            if (Time.time < _burnUntil) _burnUntil -= dt * wet * RainBurnsOutFaster;
            bool burning = Time.time < _burnUntil;
            if (burning && PlayerSwimming.swimming)
            {
                _burnUntil = 0f;
                burning = false;
                Plugin.Log.LogInfo("[Seating] the water put the fire out");
            }

            bool onStove = _seat != null && SeatingTuning.StovesBurn.Value && IsLitStove(_seat.Item);
            _heat = onStove ? _heat + dt * Mathf.Lerp(1f, RainHeatingFloor, wet) : Mathf.Max(0f, _heat - dt * 2f);
            if (!onStove && _heat <= 0f) _smokeHinted = false;

            bool thrown = false;
            if (onStove && _heat >= IgniteAfterSeconds)
            {
                if (!burning) Plugin.Log.LogInfo("[Seating] sat on a lit stove too long: pants on fire");
                // Burning for as long as they stay on it, and a while after.
                _burnUntil = Time.time + BurnSeconds;
                burning = true;
                if (_heat >= ThrowAfterSeconds)
                {
                    _heat = 0f;
                    _smokeHinted = false;
                    Plugin.Log.LogInfo("[Seating] jumped off the stove");
                    StandUp(false);
                    _hopOwed = true;
                    thrown = true;
                }
            }
            else if (onStove && _heat >= SmokeAfterSeconds && !_smokeHinted)
            {
                _smokeHinted = true;
                Plugin.Log.LogInfo("[Seating] pants smoking");
            }

            float smoke = burning ? 1f : Mathf.Clamp01((_heat - SmokeAfterSeconds) / (IgniteAfterSeconds - SmokeAfterSeconds));
            // Rain steams off the heat well before anything smokes, and takes the place of most of the smoke.
            float steam = burning ? wet : wet * Mathf.Clamp01(_heat / SmokeAfterSeconds);
            smoke *= 1f - 0.7f * wet;
            _smoke01 = Mathf.MoveTowards(_smoke01, smoke, dt * 2f);
            _steam01 = Mathf.MoveTowards(_steam01, steam, dt * 2f);
            _fire01 = burning ? Mathf.Clamp01((_burnUntil - Time.time) / 1f) : 0f;
            return thrown;
        }

        private bool IsLitStove(PickupableItem item)
        {
            if (item == null || !(item is ShipItemStove)) return false;
            if (_stoveFuelOf != item)
            {
                _stoveFuelOf = item;
                _stoveFuel = item.GetComponentsInChildren<StoveFuelTrigger>(true);
            }
            for (int i = 0; i < _stoveFuel.Length; i++)
                if (_stoveFuel[i] != null && CurrentFuelRef(_stoveFuel[i]) > 0) return true;
            return false;
        }

        /// <summary>Why the player can no longer be sitting, or null if they still can.</summary>
        private string StillSeatedReason()
        {
            var s = _seat;
            if (s.Anchor == null || !s.Anchor.gameObject.activeInHierarchy) return "the seat is gone";
            if (s.OnBoat && Refs.observerMirror.transform.parent != s.Anchor) return "no longer aboard that boat";
            if (s.Item != null && s.Item.held != null) return "the chair was picked up";
            if (s.Item != null && Seating.IsChair(s.Item as ShipItem) && Vector3.Dot(s.Item.transform.TransformDirection(Vector3.forward), Vector3.up) < 0.6f) return "the chair tipped over";
            if (GameState.inBed || GameState.sleeping || GameState.recovering || GameState.currentShipyard != null) return "the game took over";
            if (PlayerSwimming.swimming) return "in the water";
            if (Refs.charController.enabled) return "something else gave back control";
            Transform control;
            if (PlayerModel.GetLocalControl(out control) != InteractionKind.None) return "took a control";
            return null;
        }

        /// <summary>
        /// Keep the view at seat height. The observer is moved so the camera lands on the seated eye point, which
        /// covers the camera rig's own offset whatever it is. While settling, the view eases down from where it
        /// was; sitting down turns it to face the way the seat faces, and swinging round turns it by the swing.
        /// </summary>
        private void Pin()
        {
            var s = _seat;
            Transform observer = Refs.observerMirror.transform;

            Vector3 surface = s.Anchor.TransformPoint(s.LocalSurface);
            Vector3 forward = Flat(s.Anchor.TransformDirection(s.LocalForward));
            Vector3 seated = surface + Vector3.up * SeatingTuning.EyeAboveSeat.Value + forward * 0.05f;

            float t = Smooth01((Time.time - _sitTime) / Mathf.Max(0.05f, SeatingTuning.SettleSeconds.Value));

            // Turn first, then place: the eye is not on the observer's axis, so turning the observer swings the eye
            // round, and placing afterwards keeps the eye where it belongs rather than circling it.
            if (_settleYaw && t < 1f)
            {
                Vector3 e = observer.localEulerAngles;
                observer.localEulerAngles = new Vector3(e.x, Mathf.LerpAngle(_startYaw, _targetYaw, t), e.z);
            }
            if (Mathf.Abs(_turnGoal - _turnDone) > 0.01f || Mathf.Abs(_turnVel) > 0.01f)
            {
                float before = _turnDone;
                _turnDone = Mathf.SmoothDamp(_turnDone, _turnGoal, ref _turnVel, 0.16f, 900f, Time.deltaTime);
                Vector3 e = observer.localEulerAngles;
                observer.localEulerAngles = new Vector3(e.x, e.y + (_turnDone - before), e.z);
            }
            else
            {
                _turnGoal = _turnDone = 0f;
                _turnVel = 0f;
            }

            Vector3 target = t < 1f ? Vector3.Lerp(s.Anchor.TransformPoint(_startEyeLocal), seated, t) : seated;
            // From the eye's place on the BODY, never from the camera itself: the orbit camera takes the eye camera
            // away to its own rig (BoatCamera.SwitchOn reparents it) and follows the body, so pinning to the
            // camera there flung the player further every frame, into the sea.
            Vector3 correction = target - EyeWorld(observer);
            // A seat never needs a big jump once settled (the observer rides the boat with the seat). One means
            // something else is moving the player or the camera, and chasing it is how a player ends up in the
            // sea, so stand up instead.
            if (t >= 1f && correction.sqrMagnitude > 1.5f * 1.5f)
            {
                Plugin.Log.LogWarning($"[Seating] stood up: the view was {correction.magnitude:F1} m from the seat");
                StandUp(false);
                return;
            }
            observer.position += correction;
        }

        // ---- sitting down ------------------------------------------------------------------------------

        /// <summary>Right-clicked a chair that is set down. Called from the ShipItem.OnAltActivate patch.</summary>
        internal void TrySitOnChair(ShipItem chair)
        {
            if (!SeatingTuning.Enabled.Value || _seat != null || _rising) return;
            string why = CannotSitReason();
            if (why != null) { Hint(why); return; }
            if (Vector3.Dot(chair.transform.TransformDirection(Vector3.forward), Vector3.up) < 0.85f) { Hint("The chair has to be standing up."); return; }

            // The game's chairs are modeled Z-up with their colliders ending at the seat, so a ray down onto the
            // chair's own colliders finds the seat whatever the chair's shape, a backrest included.
            RaycastHit top;
            if (!RaycastOwnColliders(chair.transform, chair.transform.position + Vector3.up * 0.8f, 1.6f, out top)) { Hint("Could not find the seat."); return; }

            Vector3 feet = Feet();
            Vector3 forward = ChairFacing(chair, top.point, feet);
            Vector3 surface = top.point - forward * (HasBackrest(chair) ? 0.04f : 0f);

            Hit floor;
            float below = RaycastDown(surface + forward * 0.35f + Vector3.up * 0.02f, 3f, chair.transform, out floor) ? surface.y - floor.Point.y : 99f;
            if (!HasHeadroom(surface, chair.transform)) { Hint("No room to sit there."); return; }

            var s = new Seat
            {
                Anchor = chair.transform,
                Item = chair,
                Ignore = chair.transform,
                LocalSurface = chair.transform.InverseTransformPoint(surface),
                LocalForward = chair.transform.InverseTransformDirection(forward),
                Pose = below > LegReach ? SeatPose.Dangle : SeatPose.FeetDown,
                FloorBelow = Mathf.Min(below, 3f),
            };
            Sit(s, "a chair ('" + chair.name + "')");
        }

        /// <summary>
        /// The sit key: sit on what is being looked at, if it is at seat height. On a ledge the player sits on the
        /// edge nearest where they stand, facing back toward it, with the legs over that edge; feet rest on the
        /// floor if it is near enough, otherwise the legs dangle. A spar is straddled or sat on sideways.
        /// </summary>
        private void TrySitHere()
        {
            _tryContext = null;
            string why = CannotSitReason();
            if (why != null) { Hint(why); return; }
            Transform eye = EyeTransform();
            if (eye == null) return;

            Hit hit;
            if (!Raycast(eye.position, eye.forward, 2.4f, null, out hit))
            {
                LogWhatTheRayPassed(eye.position, eye.forward, 2.4f);
                Hint("Nothing to sit on there.");
                return;
            }

            _tryContext = Describe(hit);
            // A chair looked at with the sit key sits the same way as right-clicking it.
            var item = hit.Walk ? null : hit.Collider.GetComponentInParent<ShipItem>();
            if (item != null && item.sold && item.held == null && IsChair(item)) { TrySitOnChair(item); return; }
            if (!hit.Walk && item == null && TrySitOnPropChair(hit.Collider)) return;

            Vector3 feet = Feet();
            Vector3 top;
            Hit on;
            if (hit.Normal.y > LevelNormal) { top = hit.Point; on = hit; }
            else
            {
                // Looking at the side of a rail, a crate or a spar: find its top just past the face.
                Vector3 into = Flat(-hit.Normal);
                if (!RaycastDown(hit.Point + into * 0.05f + Vector3.up * 1.2f, 1.5f, null, out on) || on.Normal.y < 0.3f) { Hint("Nothing level to sit on there."); return; }
                top = on.Point;
            }

            float height = top.y - feet.y;
            _tryContext = $"{Describe(on)}, {height:F2} m up";
            SparShape spar;
            if (height > -0.45f && height <= MaxSeatHeight && FindSpar(top, out spar))
            {
                TrySitOnSpar(spar, on, feet, eye, height);
                return;
            }
            if (on.Normal.y < LevelNormal) { Hint("Nothing level to sit on there."); return; }

            if (height < 0.2f)
            {
                // Looking at the floor, or something about as low: sit on the edge of it if there is a drop just
                // past where the player looks (a dock, a roof, the top of a wall), otherwise on the floor where they stand.
                if (height < -0.3f) { Hint("Too low to sit on."); return; }
                if (TrySitOnLedgeAhead(on, top, eye)) return;
                TrySitOnFloor(eye);
                return;
            }
            if (height > MaxSeatHeight) { Hint("Too high to sit on."); return; }

            Transform anchor = AnchorFor(on);
            Transform ignore = on.Collider.transform;

            // Face back toward where the player stands, and find the edge on that side.
            Vector3 toward = Flat(feet - top);
            if (toward.sqrMagnitude < 1e-4f) toward = Flat(-eye.forward);
            // Furniture is modeled square to its own axes, so a bench or a crate is faced straight on. A slatted
            // bench's gaps leave SquareToEdge nothing steady to measure.
            bool squared = SnapToPropAxis(on, ref toward);
            Vector3 edge;
            if (!FindEdge(top, toward, 0.9f, out edge)) { Hint("No edge to sit on there."); return; }
            // Square up to the edge: find it again a little to each side, and face straight out from the line
            // through the two, on the player's side. Without this the facing is whatever angle the player happened
            // to be looking from.
            Vector3 square;
            if (!squared && SquareToEdge(top, toward, out square))
            {
                Vector3 squaredEdge;
                if (FindEdge(top, square, 0.9f, out squaredEdge)) { toward = square; edge = squaredEdge; }
            }

            Vector3 surface = SeatBack(edge, toward, top.y);
            if (!HasWidth(surface, toward, top.y)) { Hint("Too narrow to sit on."); return; }
            if (!HasHeadroom(surface, ignore)) { Hint("No room to sit there."); return; }

            Hit floor;
            float below = RaycastDown(edge + toward * 0.30f + Vector3.up * 0.02f, 6f, ignore, out floor) ? top.y - floor.Point.y : 99f;

            var s = new Seat
            {
                Anchor = anchor,
                OnBoat = on.Walk,
                Ignore = ignore,
                // Sitting on a crate or barrel: picking it up stands you up, same as a chair.
                Item = on.Walk ? null : on.Collider.GetComponentInParent<PickupableItem>(),
                LocalSurface = anchor.InverseTransformPoint(surface),
                LocalForward = anchor.InverseTransformDirection(toward),
                LocalEdge = anchor.InverseTransformPoint(edge),
                Edge = true,
                Pose = below > LegReach ? SeatPose.Dangle : SeatPose.FeetDown,
                FloorBelow = Mathf.Min(below, 6f),
            };
            Sit(s, $"{Describe(on)}, {height:F2} m up");
        }

        /// <summary>
        /// The game's own furniture in towns (the tavern's 'furniture chair M', 'furniture chair E'): one convex
        /// collider round the whole chair, backrest included, so there is no seat surface for a ray to find. The meshes
        /// are modeled like the shop chairs: Z up, origin at the seat, backrest on local -Y. So the seat is placed from
        /// the mesh, facing away from a backrest if the chair has one and toward the player if not. True when the
        /// collider is one of these chairs, whether or not the player could sit.
        /// </summary>
        private bool TrySitOnPropChair(Collider c)
        {
            Transform t = c.transform;
            if (!IsChairName(t.name)) return false;
            var filter = t.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return false;
            if (Vector3.Dot(t.forward, Vector3.up) < 0.85f) return false;   // not stood up the modeled way: leave it to the ledge rules

            Bounds b = filter.sharedMesh.bounds;
            float seatZ = Mathf.Min(b.max.z, 0.03f);
            bool backrest = b.max.z > seatZ + 0.3f;
            Vector3 surface = t.TransformPoint(new Vector3(b.center.x, b.center.y + (backrest ? 0.03f : 0f), seatZ));
            Vector3 feet = Feet();
            float height = surface.y - feet.y;
            _tryContext = $"furniture '{t.name}', {height:F2} m up{(backrest ? ", backrest" : "")}";
            if (height < 0.15f || height > 0.9f) { Hint("Can't reach that seat from here."); return true; }

            Vector3 forward = backrest ? Flat(t.TransformDirection(Vector3.up)) : NearestAxis(t, Flat(feet - surface));
            if (forward.sqrMagnitude < 1e-4f) forward = Flat(t.TransformDirection(Vector3.up));

            Hit floor;
            float below = RaycastDown(surface + forward * 0.35f + Vector3.up * 0.02f, 3f, t, out floor) ? surface.y - floor.Point.y : 99f;
            if (!HasHeadroom(surface, t, true)) { Hint("No room to sit there."); return true; }

            var s = new Seat
            {
                Anchor = t,
                Ignore = t,
                LocalSurface = t.InverseTransformPoint(surface),
                LocalForward = t.InverseTransformDirection(forward),
                Pose = below > LegReach ? SeatPose.Dangle : SeatPose.FeetDown,
                FloorBelow = Mathf.Min(below, 3f),
            };
            Sit(s, $"a furniture chair ('{t.name}'{(backrest ? ", backrest" : "")})");
            return true;
        }

        private static bool IsChairName(string name)
        {
            return name.IndexOf("chair", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("stool", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Face a piece of furniture straight on: turn <paramref name="dir"/> onto the nearest of the object's own level
        /// axes, if one is within 40 degrees. Only for things in the visible world of furniture size; a boat's walking
        /// copy, the hull, terrain and whole buildings keep the measured edge.
        /// </summary>
        private static bool SnapToPropAxis(Hit on, ref Vector3 dir)
        {
            var c = on.Collider;
            if (on.Walk || c == null || c.GetType().Name == "TerrainCollider" || c.gameObject.layer == 12) return false;
            Vector3 size = c.bounds.size;
            if (size.x > 4f || size.z > 4f) return false;
            Vector3 axis = NearestAxis(c.transform, dir);
            if (axis.sqrMagnitude < 1e-4f || Vector3.Angle(axis, dir) > 40f) return false;
            dir = axis;
            return true;
        }

        /// <summary>The object's level axis (any of its six, flattened) pointing nearest <paramref name="dir"/>.</summary>
        private static Vector3 NearestAxis(Transform t, Vector3 dir)
        {
            Vector3 best = Vector3.zero;
            float bestDot = float.MinValue;
            Vector3[] axes = { t.right, -t.right, t.up, -t.up, t.forward, -t.forward };
            for (int i = 0; i < axes.Length; i++)
            {
                Vector3 a = axes[i];
                a.y = 0f;
                if (a.sqrMagnitude < 0.25f) continue;   // mostly vertical: not a way to face
                a.Normalize();
                float d = Vector3.Dot(a, dir);
                if (d > bestDot) { bestDot = d; best = a; }
            }
            return best;
        }

        /// <summary>
        /// Standing on something and looking down at its edge (the end of a dock, a cabin roof, the top of a wall):
        /// sit on that edge facing out, legs over the drop. False when there is no edge near where the player looks,
        /// or the drop past it is only a step, which leaves it to floor sitting.
        /// </summary>
        private bool TrySitOnLedgeAhead(Hit on, Vector3 top, Transform eye)
        {
            Vector3 look = Flat(eye.forward);
            if (look.sqrMagnitude < 1e-4f) return false;
            bool squared = SnapToPropAxis(on, ref look);
            Vector3 edge;
            if (!FindEdge(top, look, 0.7f, out edge)) return false;
            Vector3 square;
            if (!squared && SquareToEdge(top, look, out square))
            {
                Vector3 squaredEdge;
                if (FindEdge(top, square, 0.7f, out squaredEdge)) { look = square; edge = squaredEdge; }
            }

            Transform ignore = on.Collider.transform;
            Hit floor;
            float below = RaycastDown(edge + look * 0.25f + Vector3.up * 0.05f, 6f, ignore, out floor) ? top.y - floor.Point.y : 99f;
            if (below < 0.35f) return false;

            Vector3 surface = SeatBack(edge, look, top.y);
            if (!HasWidth(surface, look, top.y)) return false;
            if (!HasHeadroom(surface, ignore)) { Hint("No room to sit there."); return true; }

            Transform anchor = AnchorFor(on);
            var s = new Seat
            {
                Anchor = anchor,
                OnBoat = on.Walk,
                Ignore = ignore,
                Item = on.Walk ? null : on.Collider.GetComponentInParent<PickupableItem>(),
                LocalSurface = anchor.InverseTransformPoint(surface),
                LocalForward = anchor.InverseTransformDirection(look),
                LocalEdge = anchor.InverseTransformPoint(edge),
                Edge = true,
                Pose = below > LegReach ? SeatPose.Dangle : SeatPose.FeetDown,
                FloorBelow = Mathf.Min(below, 6f),
            };
            Sit(s, $"the edge of {Describe(on)} underfoot, {Mathf.Min(below, 99f):F2} m drop");
            return true;
        }

        /// <summary>
        /// Sit on a spar: straddling it when looking along it, facing the way the player looks, or sideways with the
        /// legs over the side toward the player when looking across it.
        /// </summary>
        private void TrySitOnSpar(SparShape spar, Hit on, Vector3 feet, Transform eye, float height)
        {
            Transform anchor = AnchorFor(on);
            Transform ignore = on.Collider.transform;
            Vector3 surface = spar.Center;
            if (!HasHeadroom(surface, ignore)) { Hint("No room to sit there."); return; }

            Vector3 look = Flat(eye.forward);
            bool straddle = Mathf.Abs(Vector3.Dot(look, spar.Axis)) >= StraddleLookDot;
            var s = new Seat
            {
                Anchor = anchor,
                OnBoat = on.Walk,
                Ignore = ignore,
                Item = on.Walk ? null : on.Collider.GetComponentInParent<PickupableItem>(),
                LocalSurface = anchor.InverseTransformPoint(surface),
                LocalAxis = anchor.InverseTransformDirection(spar.Axis),
                Spar = true,
                SparWidth = spar.Width,
            };
            if (straddle)
            {
                Vector3 forward = Vector3.Dot(look, spar.Axis) >= 0f ? spar.Axis : -spar.Axis;
                s.LocalForward = anchor.InverseTransformDirection(forward);
                s.Pose = SeatPose.Straddle;
                s.FloorBelow = FloorUnder(surface, spar.Across, spar.Width, ignore);
            }
            else
            {
                float side = Vector3.Dot(feet - surface, spar.Across);
                if (Mathf.Abs(side) < 0.05f) side = -Vector3.Dot(look, spar.Across);
                Vector3 forward = side >= 0f ? spar.Across : -spar.Across;
                SideSaddle(s, surface, forward, ignore);
            }
            Sit(s, $"a spar ({(straddle ? "straddling" : "sideways")}, {spar.Width:F2} m across, {Describe(on)}, {height:F2} m up)");
        }

        private static void SideSaddle(Seat s, Vector3 surface, Vector3 forward, Transform ignore)
        {
            Hit floor;
            Vector3 beyond = surface + forward * (s.SparWidth * 0.5f + 0.30f);
            float below = RaycastDown(beyond + Vector3.up * 0.02f, 6f, ignore, out floor) ? surface.y - floor.Point.y : 99f;
            s.LocalForward = s.Anchor.InverseTransformDirection(forward);
            s.LocalEdge = s.Anchor.InverseTransformPoint(surface + forward * (s.SparWidth * 0.5f));
            s.Pose = below > LegReach ? SeatPose.Dangle : SeatPose.FeetDown;
            s.FloorBelow = Mathf.Min(below, 6f);
        }

        /// <summary>The nearer of the drops under either side of a spar, for a straddling body's feet.</summary>
        private static float FloorUnder(Vector3 surface, Vector3 across, float width, Transform ignore)
        {
            float best = 6f;
            for (int side = -1; side <= 1; side += 2)
            {
                Hit floor;
                if (RaycastDown(surface + across * (side * (width * 0.5f + 0.2f)) + Vector3.up * 0.02f, 6f, ignore, out floor))
                    best = Mathf.Min(best, surface.y - floor.Point.y);
            }
            return best;
        }

        /// <summary>
        /// Sitting on a spar, the sit key again: from straddling it, turn to sit with both legs over the side being
        /// looked toward; from sitting sideways, turn back to straddle it, facing along it the way being looked.
        /// </summary>
        private void ToggleSpar()
        {
            var s = _seat;
            Transform eye = EyeTransform();
            if (eye == null) return;
            Vector3 surface = s.Anchor.TransformPoint(s.LocalSurface);
            Vector3 forward = Flat(s.Anchor.TransformDirection(s.LocalForward));
            Vector3 axis = Flat(s.Anchor.TransformDirection(s.LocalAxis));
            Vector3 across = Vector3.Cross(Vector3.up, axis).normalized;
            Vector3 look = Flat(eye.forward);
            if (axis.sqrMagnitude < 1e-4f) return;

            if (s.Pose == SeatPose.Straddle)
            {
                float side = Vector3.Dot(look, across);
                Vector3 newForward = side >= 0f ? across : -across;
                Reface(forward, newForward);
                SideSaddle(s, surface, newForward, s.Ignore);
                Plugin.Log.LogInfo($"[Seating] sat sideways on the spar, {(s.Pose == SeatPose.Dangle ? "dangling" : "feet down")} ({s.FloorBelow:F2} m to the floor)");
            }
            else
            {
                float along = Vector3.Dot(look, axis);
                if (Mathf.Abs(along) < 0.05f) along = Vector3.Dot(forward, axis);
                Vector3 newForward = along >= 0f ? axis : -axis;
                Reface(forward, newForward);
                s.LocalForward = s.Anchor.InverseTransformDirection(newForward);
                s.Pose = SeatPose.Straddle;
                s.FloorBelow = FloorUnder(surface, across, s.SparWidth, s.Ignore);
                Plugin.Log.LogInfo("[Seating] straddled the spar");
            }
        }

        /// <summary>Sit down on the floor where the player stands, facing where they look, legs out in front.</summary>
        private void TrySitOnFloor(Transform eye)
        {
            Vector3 facing = Flat(eye.forward);
            if (facing.sqrMagnitude < 1e-4f) facing = Flat(Refs.observerMirror.transform.forward);
            Hit floor;
            if (!RaycastDown(Feet() + Vector3.up * 0.4f + facing * 0.1f, 0.8f, null, out floor) || floor.Normal.y < LevelNormal)
            {
                Hint("Nothing level to sit on here.");
                return;
            }
            Transform anchor = AnchorFor(floor);
            if (!HasHeadroom(floor.Point, floor.Collider.transform)) { Hint("No room to sit there."); return; }
            var s = new Seat
            {
                Anchor = anchor,
                OnBoat = floor.Walk,
                Ignore = floor.Collider.transform,
                Item = floor.Walk ? null : floor.Collider.GetComponentInParent<PickupableItem>(),
                LocalSurface = anchor.InverseTransformPoint(floor.Point),
                LocalForward = anchor.InverseTransformDirection(facing),
                Floor = true,
                Pose = FloorPoses[Random.Range(0, FloorPoses.Length)],
                FloorBelow = 0f,
            };
            Sit(s, $"the floor ({Describe(floor)})");
        }

        private static readonly SeatPose[] FloorPoses =
            { SeatPose.FloorLegsOut, SeatPose.FloorCrossLegged, SeatPose.FloorKneeUp, SeatPose.FloorKneesHugged };

        /// <summary>Sitting on the floor, the sit key again: the next floor pose, round and round.</summary>
        private void NextFloorPose()
        {
            int i = System.Array.IndexOf(FloorPoses, _seat.Pose);
            _seat.Pose = FloorPoses[(i + 1) % FloorPoses.Length];
            Plugin.Log.LogInfo("[Seating] floor pose: " + PoseName(_seat.Pose));
        }

        private static string PoseName(SeatPose pose)
        {
            switch (pose)
            {
                case SeatPose.Straddle: return "straddling";
                case SeatPose.Dangle: return "legs dangling";
                case SeatPose.FloorLegsOut: return "legs out";
                case SeatPose.FloorCrossLegged: return "cross-legged";
                case SeatPose.FloorKneeUp: return "one knee up";
                case SeatPose.FloorKneesHugged: return "knees hugged";
                default: return "feet down";
            }
        }

        /// <summary>For a sit key press that found nothing: every collider the ray passed and why it did not count.</summary>
        private static void LogWhatTheRayPassed(Vector3 origin, Vector3 dir, float distance)
        {
            var sb = new System.Text.StringBuilder("[Seating] nothing to sit on;");
            int total = 0;
            total += DescribePassed(sb, "visible", origin, dir, distance, false);
            Transform v, w;
            if (WalkFrames(out v, out w))
                total += DescribePassed(sb, "walking copy", ToWalk(origin, v, w), ToWalkDir(dir, v, w), distance, true);
            else
                sb.Append(" (not aboard a boat, one frame only)");
            if (total == 0) sb.Append(" the ray hit no collider at all");
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static int DescribePassed(System.Text.StringBuilder sb, string frame, Vector3 origin, Vector3 dir, float distance, bool walk)
        {
            var all = Physics.RaycastAll(origin, dir, distance, ~0, QueryTriggerInteraction.Collide);
            if (all.Length == 0) return 0;
            System.Array.Sort(all, (x, y) => x.distance.CompareTo(y.distance));
            sb.Append($" {frame}:");
            foreach (var h in all)
            {
                var c = h.collider;
                string why = c.gameObject.layer == 2 && !IsStaticScenery(c) ? "Ignore Raycast layer, not solid scenery"
                    : (Physics.DefaultRaycastLayers & (1 << c.gameObject.layer)) == 0 ? "layer not raycast"
                    : Skip(c, walk) ? (c.isTrigger ? "trigger that is not an item" : walk ? "not part of the boat, or the player" : "rough shape on a rigidbody, or the player")
                    : "counted";
                sb.Append($" [{h.distance:F2} m '{Path(c.transform)}' {c.GetType().Name} layer {c.gameObject.layer}{(c.isTrigger ? " trigger" : "")}: {why}]");
            }
            return all.Length;
        }

        /// <summary>
        /// On a rail or a narrow ledge, swing the legs over to the other side. Standing up still returns the
        /// player to the side they sat down from, so this can never put them overboard.
        /// </summary>
        private void TrySwingLegs()
        {
            var s = _seat;
            Vector3 surface = s.Anchor.TransformPoint(s.LocalSurface);
            Vector3 forward = Flat(s.Anchor.TransformDirection(s.LocalForward));
            Vector3 back = -forward;

            Vector3 far;
            if (!FindEdge(surface, back, 0.6f, out far)) { Hint("Too wide to swing your legs over."); return; }
            Vector3 near = s.Anchor.TransformPoint(s.LocalEdge);
            float width = Vector3.Distance(Flat3(near), Flat3(far));
            Vector3 newSurface = width < 2f * HipSetback + 0.06f
                ? new Vector3((near.x + far.x) * 0.5f, surface.y, (near.z + far.z) * 0.5f)
                : SeatBack(far, back, surface.y);
            if (!HasHeadroom(newSurface, s.Ignore)) { Hint("No room to swing your legs over."); return; }

            Hit floor;
            float below = RaycastDown(far + back * 0.30f + Vector3.up * 0.02f, 6f, s.Ignore, out floor) ? surface.y - floor.Point.y : 99f;

            Reface(forward, back);
            s.LocalSurface = s.Anchor.InverseTransformPoint(newSurface);
            s.LocalForward = s.Anchor.InverseTransformDirection(back);
            s.LocalEdge = s.Anchor.InverseTransformPoint(far);
            s.Pose = below > LegReach ? SeatPose.Dangle : SeatPose.FeetDown;
            s.FloorBelow = Mathf.Min(below, 6f);
            Plugin.Log.LogInfo($"[Seating] swung legs over, {(s.Pose == SeatPose.Dangle ? "dangling" : "feet down")} ({below:F2} m to the floor)");
        }

        /// <summary>
        /// The seat now faces another way: ease the view round by the same turn, on top of wherever the player is
        /// looking, and ease the eye over to the new seat point. A half turn goes back the way the last one came.
        /// </summary>
        private void Reface(Vector3 from, Vector3 to)
        {
            float turn = Vector3.SignedAngle(from, to, Vector3.up);
            if (Mathf.Abs(turn) > 150f)
            {
                float round = Mathf.Repeat(turn, 360f);
                turn = _swingSign > 0f ? round : round - 360f;
                _swingSign = -_swingSign;
            }
            _turnGoal += turn;
            Transform observer = Refs.observerMirror.transform;
            _startEyeLocal = _seat.Anchor.InverseTransformPoint(EyeWorld(observer));
            _sitTime = Time.time;
            _settleYaw = false;
        }

        private void Sit(Seat s, string what)
        {
            Transform observer = Refs.observerMirror.transform;
            _seat = s;
            _sitTime = Time.time;
            _startEyeLocal = s.Anchor.InverseTransformPoint(EyeWorld(observer));
            _startYaw = observer.localEulerAngles.y;
            _targetYaw = YawInParent(observer, Flat(s.Anchor.TransformDirection(s.LocalForward)));
            _settleYaw = true;
            _turnGoal = _turnDone = _turnVel = 0f;
            _swingSign = 1f;

            Refs.SetPlayerControl(false);
            Refs.observerMirror.enabled = false;
            _controlTaken = true;
            if (Refs.ovrCameraRig != null)
            {
                var crouch = Refs.ovrCameraRig.GetComponent<PlayerCrouching>();
                if (crouch != null) CrouchingRef(crouch) = false;
            }
            Plugin.Log.LogInfo($"[Seating] sat on {what}: {PoseName(s.Pose)} ({s.FloorBelow:F2} m to the floor)");
        }

        // ---- standing up -------------------------------------------------------------------------------

        /// <summary>Stand up at once, handing the controls straight back, for something that needs the player on their feet.</summary>
        internal void StandUpNow(string why)
        {
            if (_seat == null && !_rising) return;
            Plugin.Log.LogInfo("[Seating] stood up: " + why);
            StandUp(false);
        }

        private void StandUp(bool smooth)
        {
            _seat = null;
            _turnGoal = _turnDone = _turnVel = 0f;
            if (!smooth || !FramesMatch()) { FinishStand(); return; }
            _rising = true;
            _riseTime = Time.time;
            _riseFrom = Refs.observerMirror.transform.localPosition;
        }

        /// <summary>Ease the view back up to the standing player before handing control back.</summary>
        private void Rise()
        {
            if (!FramesMatch()) { FinishStand(); return; }
            float t = Smooth01((Time.time - _riseTime) / RiseSeconds);
            Refs.observerMirror.transform.localPosition = Vector3.Lerp(_riseFrom, Refs.charController.transform.localPosition, t);
            if (t >= 1f) FinishStand();
        }

        private void FinishStand()
        {
            _rising = false;
            if (!_controlTaken) return;
            _controlTaken = false;
            if (Refs.observerMirror != null) Refs.observerMirror.enabled = true;
            if (Refs.charController != null && !GameState.inBed && !GameState.sleeping) Refs.SetPlayerControl(true);
        }

        /// <summary>
        /// The observer mirror copies the controller's local position onto the observer, which only means the
        /// same place when both hang off matching parents (the boat and its walking copy, or the shifting world).
        /// </summary>
        private static bool FramesMatch()
        {
            var o = Refs.observerMirror.transform.parent;
            var c = Refs.charController.transform.parent;
            return o != null && c != null && (o == c || o.name == c.name || GameState.currentBoat != null);
        }

        // ---- checks ------------------------------------------------------------------------------------

        private static string CannotSitReason()
        {
            if (GameState.inBed || GameState.sleeping || GameState.recovering || GameState.currentlyLoading) return "";
            if (GameState.currentShipyard != null || GameState.inCursorMenu || BoatCamera.on) return "";
            if (PlayerSwimming.swimming) return "Can't sit while swimming.";
            if (!Refs.charController.enabled) return "";
            Transform control;
            if (PlayerModel.GetLocalControl(out control) != InteractionKind.None) return "";
            var pointer = LocalInteraction.Pointer;
            var held = pointer != null ? pointer.GetHeldItem() : null;
            if (held != null && held.big) return "Put it down first.";
            return null;
        }

        // ShipItem has its own `name` field, the shop display name ("chair"), which hides the GameObject's name
        // ("75 chair M large 1(Clone)"), so both are read through the GameObject where the model matters.
        internal static bool IsChair(ShipItem item)
        {
            return item != null && (item.name.IndexOf("chair", System.StringComparison.OrdinalIgnoreCase) >= 0
                || item.gameObject.name.IndexOf("chair", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>The game's large M chairs have a backrest on their local -Y side (measured from the meshes).</summary>
        private static bool HasBackrest(ShipItem chair)
        {
            return chair.gameObject.name.IndexOf("chair M large", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Vector3 ChairFacing(ShipItem chair, Vector3 seat, Vector3 feet)
        {
            if (HasBackrest(chair)) return Flat(chair.transform.TransformDirection(Vector3.up));
            Vector3 toward = Flat(feet - seat);
            return toward.sqrMagnitude > 1e-4f ? toward : Flat(chair.transform.TransformDirection(Vector3.up));
        }

        /// <summary>Walk from a point on a surface toward a direction until the surface drops away.</summary>
        private static bool FindEdge(Vector3 top, Vector3 dir, float maxDistance, out Vector3 edge)
        {
            edge = top;
            for (float d = 0.04f; d <= maxDistance; d += 0.04f)
            {
                Vector3 p = top + dir * d;
                Hit h;
                if (!RaycastDown(p + Vector3.up * 0.25f, 0.34f, null, out h) || Mathf.Abs(h.Point.y - top.y) > 0.06f || h.Normal.y < LevelNormal - 0.05f)
                {
                    edge = top + dir * (d - 0.04f);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The direction straight out from an edge, on the side of <paramref name="toward"/>: the edge is found
        /// 20 cm to either side of <paramref name="top"/>, and the facing is perpendicular to the line through
        /// both. False when either side is off the surface (a post rather than a rail) or the edge curves too much.
        /// A rail that climbs toward the bow is followed: each side is measured from its own height.
        /// </summary>
        private static bool SquareToEdge(Vector3 top, Vector3 toward, out Vector3 square)
        {
            square = toward;
            Vector3 side = Vector3.Cross(Vector3.up, toward).normalized;
            Hit ha, hb;
            if (!RaycastDown(top + side * 0.2f + Vector3.up * 0.25f, 0.5f, null, out ha) || Mathf.Abs(ha.Point.y - top.y) > 0.12f) return false;
            if (!RaycastDown(top - side * 0.2f + Vector3.up * 0.25f, 0.5f, null, out hb) || Mathf.Abs(hb.Point.y - top.y) > 0.12f) return false;
            Vector3 a, b;
            if (!FindEdge(ha.Point, toward, 0.9f, out a) || !FindEdge(hb.Point, toward, 0.9f, out b)) return false;
            Vector3 along = Flat(a - b);
            if (along.sqrMagnitude < 1e-4f) return false;
            Vector3 n = Vector3.Cross(along, Vector3.up).normalized;
            if (Vector3.Dot(n, toward) < 0f) n = -n;
            if (Vector3.Angle(n, toward) > 60f) return false;
            square = n;
            return true;
        }

        /// <summary>The hips go a little in from the edge, if the surface is there; otherwise right at the edge.</summary>
        private static Vector3 SeatBack(Vector3 edge, Vector3 toward, float topY)
        {
            Vector3 p = edge - toward * HipSetback;
            Hit h;
            if (RaycastDown(p + Vector3.up * 0.25f, 0.34f, null, out h) && Mathf.Abs(h.Point.y - topY) < 0.06f) return h.Point;
            return new Vector3(edge.x, topY, edge.z);
        }

        /// <summary>
        /// Room for the hips along the edge: the surface carries on 14 cm to either side, level with the seat, or
        /// climbing steadily through it, the way a gunwale rises toward the bow.
        /// </summary>
        private static bool HasWidth(Vector3 surface, Vector3 toward, float topY)
        {
            Vector3 side = Vector3.Cross(Vector3.up, toward).normalized;
            Hit a, b;
            if (!RaycastDown(surface + side * 0.14f + Vector3.up * 0.25f, 0.5f, null, out a)) return false;
            if (!RaycastDown(surface - side * 0.14f + Vector3.up * 0.25f, 0.5f, null, out b)) return false;
            float da = a.Point.y - topY, db = b.Point.y - topY;
            if (Mathf.Abs(da) < 0.08f && Mathf.Abs(db) < 0.08f) return true;
            return Mathf.Abs(da + db) < 0.05f && Mathf.Abs(da) < 0.16f;
        }

        /// <summary>
        /// A bowsprit, a crosstree, a yard: narrow across its top, long along it, with nothing under either side
        /// for a good way down. Measured by walking the surface out from <paramref name="top"/> in eight directions.
        /// </summary>
        private static bool FindSpar(Vector3 top, out SparShape spar)
        {
            spar = default(SparShape);
            float narrowest = float.MaxValue;
            Vector3 across = Vector3.zero;
            Vector3 end;
            for (int i = 0; i < 8; i++)
            {
                Vector3 d = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
                float w = Reach(top, d, 0.4f, out end) + Reach(top, -d, 0.4f, out end);
                if (w < narrowest) { narrowest = w; across = d; }
            }
            if (narrowest > SparMaxWidth) return false;

            Vector3 a, b;
            Reach(top, across, 0.4f, out a);
            Reach(top, -across, 0.4f, out b);
            Vector3 mid = (a + b) * 0.5f;
            Hit crown;
            if (!RaycastDown(new Vector3(mid.x, Mathf.Max(top.y, Mathf.Max(a.y, b.y)) + 0.2f, mid.z), 0.5f, null, out crown)) return false;

            Vector3 axis = Vector3.Cross(Vector3.up, across).normalized;
            float length = Reach(crown.Point, axis, 0.6f, out end) + Reach(crown.Point, -axis, 0.6f, out end);
            if (length < SparMinLength) return false;

            for (int side = -1; side <= 1; side += 2)
            {
                Hit below;
                Vector3 beside = crown.Point + across * (side * (narrowest * 0.5f + 0.14f));
                if (RaycastDown(beside + Vector3.up * 0.05f, SparOpenBelow, null, out below)) return false;
            }
            spar = new SparShape { Center = crown.Point, Axis = axis, Across = across, Width = narrowest };
            return true;
        }

        /// <summary>
        /// How far a surface carries on from <paramref name="start"/> (a point on it) in a level direction, over
        /// slopes up to about 50 degrees: across a round spar that is most of the way to its sides, and along a
        /// bowsprit it follows the climb.
        /// </summary>
        private static float Reach(Vector3 start, Vector3 dir, float max, out Vector3 end)
        {
            const float step = 0.03f;
            end = start;
            float d = 0f;
            while (d + step <= max + 1e-4f)
            {
                Hit h;
                if (!RaycastDown(end + dir * step + Vector3.up * 0.1f, 0.2f, null, out h) || Mathf.Abs(h.Point.y - end.y) > step * 1.2f) break;
                end = h.Point;
                d += step;
            }
            return d;
        }

        /// <summary>Room for a sitting torso above the seat, in both copies of the boat.</summary>
        /// <param name="snug">A chair pulled up to a table: a slimmer torso starting higher, so the table edge a hand's
        /// width in front of the hips does not count as in the way.</param>
        private static bool HasHeadroom(Vector3 surface, Transform ignore, bool snug = false)
        {
            float radius = snug ? 0.11f : 0.16f;
            Vector3 p0 = surface + Vector3.up * (snug ? 0.45f : 0.35f), p1 = surface + Vector3.up * 0.95f;
            // Solid colliders only. Items' own colliders are triggers (see Skip), and counting those here blocked
            // every seat: something item-owned is always in that space.
            if (Blocked(Physics.OverlapCapsule(p0, p1, radius, SeatLayers, QueryTriggerInteraction.Ignore), false, ignore, "visible")) return false;
            Transform v, w;
            if (WalkFrames(out v, out w)
                && Blocked(Physics.OverlapCapsule(ToWalk(p0, v, w), ToWalk(p1, v, w), radius, SeatLayers, QueryTriggerInteraction.Ignore), true, ignore, "walking copy"))
                return false;
            return true;
        }

        private static bool Blocked(Collider[] cols, bool walk, Transform ignore, string frame)
        {
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (Skip(c, walk) || (ignore != null && c.transform.IsChildOf(ignore))) continue;
                Plugin.Log.LogInfo($"[Seating] no room: '{Path(c.transform)}' ({c.GetType().Name}, layer {c.gameObject.layer}, {frame}) is in the way");
                return true;
            }
            return false;
        }

        // ---- raycasts ----------------------------------------------------------------------------------

        private static readonly RaycastHit[] _hits = new RaycastHit[32];

        /// <summary>
        /// Nearest hit that is not the player, a held item or a trigger, in the visible frame and, aboard a boat,
        /// on its walking copy. A walking copy hit is reported where it sits on the visible boat.
        /// </summary>
        private static bool Raycast(Vector3 origin, Vector3 dir, float distance, Transform ignore, out Hit best)
        {
            best = default(Hit);
            float bestD = float.MaxValue;
            CastOne(origin, dir, distance, ignore, false, null, null, ref best, ref bestD);
            Transform v, w;
            // The visible frame wins a tie: its hull copy and the walking hull are the same mesh.
            if (WalkFrames(out v, out w))
                CastOne(ToWalk(origin, v, w), ToWalkDir(dir, v, w), distance, ignore, true, v, w, ref best, ref bestD, 0.02f);
            return bestD < float.MaxValue;
        }

        private static void CastOne(Vector3 origin, Vector3 dir, float distance, Transform ignore, bool walk, Transform v, Transform w,
            ref Hit best, ref float bestD, float mustBeCloserBy = 0f)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, _hits, distance, SeatLayers, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i].collider;
                if (c == null || Skip(c, walk)) continue;
                if (ignore != null && c.transform.IsChildOf(ignore)) continue;
                if (_hits[i].distance >= bestD - mustBeCloserBy) continue;
                bestD = _hits[i].distance;
                best = new Hit
                {
                    Point = walk ? v.TransformPoint(w.InverseTransformPoint(_hits[i].point)) : _hits[i].point,
                    Normal = walk ? v.TransformDirection(w.InverseTransformDirection(_hits[i].normal)) : _hits[i].normal,
                    Distance = _hits[i].distance,
                    Collider = c,
                    Walk = walk,
                };
            }
        }

        private static bool RaycastDown(Vector3 origin, float distance, Transform ignore, out Hit hit)
        {
            return Raycast(origin, Vector3.down, distance, ignore, out hit);
        }

        private static bool RaycastOwnColliders(Transform item, Vector3 origin, float distance, out RaycastHit best)
        {
            best = default(RaycastHit);
            bool any = false;
            var ray = new Ray(origin, Vector3.down);
            // Triggers included: a placed item's own collider is a trigger (see Skip).
            foreach (var c in item.GetComponentsInChildren<Collider>())
            {
                RaycastHit h;
                if (!c.enabled || c.gameObject.layer == 2 || !c.Raycast(ray, out h, distance)) continue;
                if (!any || h.point.y > best.point.y) { best = h; any = true; }
            }
            return any;
        }

        /// <summary>
        /// The visible boat and its walking copy, when the player is aboard one. Both null-free and different, or
        /// false: on land the player and the view share one frame.
        /// </summary>
        private static bool WalkFrames(out Transform visual, out Transform walk)
        {
            visual = Refs.observerMirror != null ? Refs.observerMirror.transform.parent : null;
            walk = Refs.charController != null ? Refs.charController.transform.parent : null;
            return visual != null && walk != null && visual != walk && GameState.currentBoat != null;
        }

        private static Vector3 ToWalk(Vector3 p, Transform v, Transform w) { return w.TransformPoint(v.InverseTransformPoint(p)); }
        private static Vector3 ToWalkDir(Vector3 d, Transform v, Transform w) { return w.TransformDirection(v.InverseTransformDirection(d)); }

        /// <summary>What a seat found by this hit is kept relative to: the boat itself for its walking copy.</summary>
        private static Transform AnchorFor(Hit h)
        {
            if (h.Walk && Refs.observerMirror.transform.parent != null) return Refs.observerMirror.transform.parent;
            return h.Collider.transform;
        }

        private static string Describe(Hit h)
        {
            var c = h.Collider;
            return $"'{c.name}' ({c.GetType().Name}, layer {c.gameObject.layer}{(c.isTrigger ? ", trigger" : "")}, {(h.Walk ? "walking copy" : "visible")})";
        }

        /// <summary>
        /// Colliders that are not something to sit on or bump into.
        ///
        /// Visible frame: triggers are skipped (boarding zones, water, areas) EXCEPT an item's own: the game turns a
        /// placed item's collider into a trigger and lets an invisible copy on Ignore Raycast do the physics
        /// (ShipItem.CreateRigidbody), so without this a chair, crate or barrel could never be found.
        ///
        /// Walking copy: only the boat's own solid colliders. Whatever else happens to share that stretch of the
        /// world is not part of the boat.
        /// </summary>
        private static bool Skip(Collider c, bool walk)
        {
            if (c.gameObject.layer == 2 && !IsStaticScenery(c)) return true;
            if (Refs.charController != null && c.transform.IsChildOf(Refs.charController.transform)) return true;
            if (Refs.observerMirror != null && c.transform.IsChildOf(Refs.observerMirror.transform)) return true;
            if (walk)
            {
                Transform w = Refs.charController != null ? Refs.charController.transform.parent : null;
                return c.isTrigger || w == null || !c.transform.IsChildOf(w);
            }
            var item = c.GetComponentInParent<PickupableItem>();
            // Only a shop item's own trigger (a chair, a crate, a stove). Mooring ropes are pickupable too, and their
            // click spheres float over the water.
            if (c.isTrigger && (!(item is ShipItem) || item.held != null)) return true;
            // A shape sitting right on a boat's rigidbody (the hull capsule, the convex hull) is a rough envelope
            // around the whole ship, not its rails or deck: sitting on one floats the player inside a balcony.
            if (item == null && c.attachedRigidbody != null && c.attachedRigidbody.transform == c.transform)
            {
                var mesh = c as MeshCollider;
                if (mesh == null || mesh.convex) return true;
            }
            return false;
        }

        /// <summary>
        /// Layers the seat probes look at: everything a ray normally hits, plus Ignore Raycast. The game puts its
        /// docks there (island scenery 'dock' boxes, so its pointer looks straight through them), next to held items,
        /// items' physics copies and boats' rough hull shapes, which <see cref="IsStaticScenery"/> tells apart.
        /// </summary>
        internal static readonly int SeatLayers = Physics.DefaultRaycastLayers | (1 << 2);

        /// <summary>
        /// An Ignore Raycast collider that is solid ground to sit on: not a trigger, on no rigidbody (items' physics
        /// copies and boats have one), and not part of an item.
        /// </summary>
        internal static bool IsStaticScenery(Collider c)
        {
            return !c.isTrigger && c.attachedRigidbody == null && c.GetComponentInParent<PickupableItem>() == null;
        }

        // ---- helpers -----------------------------------------------------------------------------------

        private static Vector3 _eyeLocal = new Vector3(0f, 0.75f, 0f);

        /// <summary>
        /// Where the first-person eye is on the body, world space. Read live while the eye camera hangs off the
        /// observer; while the orbit camera has taken it away, the last offset measured in first person stands in.
        /// </summary>
        internal static Vector3 EyeWorld(Transform observer)
        {
            Transform eye = EyeTransform();
            if (eye != null && eye.IsChildOf(observer)) _eyeLocal = observer.InverseTransformPoint(eye.position);
            return observer.TransformPoint(_eyeLocal);
        }

        private static Transform _eye;

        /// <summary>The first-person camera (CenterEyeAnchor), not whichever camera is main right now.</summary>
        private static Transform EyeTransform()
        {
            if (_eye != null) return _eye;
            if (Refs.ovrCameraRig == null) return null;
            foreach (var cam in Refs.ovrCameraRig.GetComponentsInChildren<Camera>(true))
                if (cam.CompareTag("MainCamera")) { _eye = cam.transform; break; }
            return _eye;
        }

        /// <summary>The player's soles in the visual frame the deck and seats render in.</summary>
        private static Vector3 Feet()
        {
            return Refs.observerMirror.transform.position - Vector3.up * VanillaPlayer.ControllerFeetGap();
        }

        private static float YawInParent(Transform t, Vector3 worldDir)
        {
            Vector3 d = t.parent != null ? t.parent.InverseTransformDirection(worldDir) : worldDir;
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.zero;
        }

        private static Vector3 Flat3(Vector3 v) { v.y = 0f; return v; }

        private static string Path(Transform t)
        {
            string p = t.name;
            for (int i = 0; i < 3 && t.parent != null; i++) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        private static float Smooth01(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        /// <summary>Show why sitting did not work, and log it with what was being looked at.</summary>
        private void Hint(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _hint = text;
            _hintUntil = Time.time + 2.2f;
            Plugin.Log.LogInfo("[Seating] refused: " + text + (_tryContext != null ? " (" + _tryContext + ")" : ""));
        }

        private string _tryContext;   // what the last sit attempt was looking at, for the log

        private void OnGUI()
        {
            if (!GameState.playing || GameState.inCursorMenu) return;
            bool seated = _seat != null && SeatingTuning.ShowSeatedLabel.Value && !BoatCamera.on;
            bool hint = _hint != null && Time.time < _hintUntil;
            if (!seated && !hint) return;

            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18 }.WithFont();
                _labelStyle.normal.textColor = Color.white;
            }
            var old = GUI.color;
            var rect = new Rect(0f, Screen.height - 64f, Screen.width, 28f);
            if (hint)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.8f);
                GUI.Label(rect, _hint, _labelStyle);
                rect.y -= 26f;
            }
            if (seated)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.45f);
                GUI.Label(rect, "(seated)", _labelStyle);
            }
            GUI.color = old;
        }
    }

    /// <summary>Right-clicking a chair that is set down sits on it, the same gesture the game uses for its beds.</summary>
    // Empty argument list: the base class also has OnAltActivate(GoPointer), and an unqualified lookup can be
    // ambiguous, which would fail the whole PatchAll.
    [HarmonyPatch(typeof(ShipItem), "OnAltActivate", new System.Type[0])]
    internal static class ChairAltActivatePatch
    {
        private static void Postfix(ShipItem __instance)
        {
            if (__instance == null || !__instance.sold || __instance.held != null || !Seating.IsChair(__instance)) return;
            if (Seating.Instance != null) Seating.Instance.TrySitOnChair(__instance);
        }
    }
}

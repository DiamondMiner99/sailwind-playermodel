using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// One visible humanoid body: a clone of the shared shopkeeper template, its Synty rig, and the procedural
    /// animation that poses it. Everything about HOW a body moves lives here; nothing about WHERE it is or
    /// where its inputs come from does.
    ///
    /// The caller owns a root transform, places it wherever it likes, feeds this three numbers each frame
    /// (crouch amount, look pitch, deck-relative speed) and calls <see cref="Tick"/>. Your own third-person
    /// body drives those from the vanilla player; the co-op mod drives them from the network. That split is
    /// the whole point: the gait, the squat and the look-lean used to be two separate copies of the same math
    /// with comments on both telling the next reader they MUST stay identical, and the one time they drifted
    /// the same defect existed in two places at once.
    ///
    /// ANIMATION IS 100% BONE WRITING IN LateUpdate. <see cref="BodyTemplate.Strip"/> removes the Animator,
    /// so there are no clips and no controller anywhere in this. A slump, a fall or a sit is therefore the
    /// same kind of operation the walk cycle already is.
    ///
    /// Synty rig conventions (verified): legs swing about local +Y, knees flex about -Z, arms swing about -Y,
    /// the spine leans +Z. Left and right are MIRRORED in the bind pose, so the same axis and sign is used for
    /// both and the two sides alternate by PHASE rather than by flipping an axis.
    /// </summary>
    public sealed class SyntyBody
    {
        // ---- identity / plumbing ---------------------------------------------------------------------

        private readonly string _name;
        private readonly Func<float> _feetLocalY;
        private GameObject _instance;
        private Renderer[] _renderers;
        private Transform _root;
        private bool _needsFit = true;
        private int _fitWaitFrames;          // frames the fit has waited for parts with bounds
        private readonly Dictionary<string, Transform> _boneCache = new Dictionary<string, Transform>(StringComparer.Ordinal);

        /// <summary>The caller-owned transform this body is parented to and posed relative to.</summary>
        public Transform Root { get { return _root; } }

        /// <summary>The body clone itself. Null once destroyed.</summary>
        public GameObject Instance { get { return _instance; } }

        /// <summary>True once the leg bones were found; without them nothing animates and the body stays static.</summary>
        public bool RigReady { get; private set; }

        /// <summary>True once the one-time feet-on-deck fit has run (skinned bounds were valid).</summary>
        public bool Fitted { get { return !_needsFit; } }

        /// <summary>World-space height of the body, measured at fit time. 0 before the fit.</summary>
        public float MeasuredHeight { get; private set; }

        /// <summary>Root-local Y the soles were planted at. Add the measured height to place a name tag.</summary>
        public float FittedFeetLocalY { get; private set; }

        /// <summary>
        /// How far the crouch has moved the body inside the root this frame (down, and back for the squat).
        /// A name tag kept as a SIBLING of the body does not inherit this, so it has to be moved by hand or it
        /// hangs at standing height over a crouched player - subtract this from the tag's own base.
        /// </summary>
        public Vector3 CrouchOffset { get; private set; }

        /// <summary>
        /// Where the body sits relative to its planted rest spot, root-local: the crouch drop and setback plus
        /// the step toward a control it is using. Add it to a name tag's base so the tag stays over the head.
        /// </summary>
        public Vector3 BodyOffset { get { return _ixOffset - Quaternion.Euler(0f, _ixYaw, 0f) * CrouchOffset; } }

        /// <summary>Claims currently held on this body's bones. See <see cref="PoseStack"/>.</summary>
        public PoseStack Poses { get; private set; }

        /// <summary>Take over some of this body's bones. Dispose the result to hand them back.</summary>
        public IDisposable ClaimPose(string owner, int priority, PoseParts parts, Action<SyntyBody> write = null)
        {
            return Poses.Claim(owner, priority, parts, write);
        }

        // ---- per-frame inputs the caller sets ----------------------------------------------------------

        /// <summary>Crouch amount 0..1 to ease toward. Eased here, so a 20Hz quantized source is fine.</summary>
        public float Crouch01Target;

        /// <summary>Vertical look angle in degrees, positive = looking UP. Eased here.</summary>
        public float LookPitchDegTarget;

        /// <summary>Speed feeding the gait, m/s. DECK-RELATIVE on a boat, or the body phantom-walks while sailing.</summary>
        public float SpeedMps;

        // ---- eased animation state ---------------------------------------------------------------------

        private float _crouch01;
        private float _lookPitch;
        private float _animSpeed;
        private float _gaitPhase;
        private Vector3 _bodyBaseLocalPos;
        private bool _hasBodyBase;

        /// <summary>The eased crouch amount actually being posed, 0..1. Claimants may want it.</summary>
        public float Crouch01 { get { return _crouch01; } }

        // ---- rig ---------------------------------------------------------------------------------------

        private Transform _bSpine, _bUpperLegL, _bUpperLegR, _bLowerLegL, _bLowerLegR, _bShoulderL, _bShoulderR, _bElbowL, _bElbowR;
        private Transform _bFootL, _bFootR, _bHandL, _bHandR, _bHead;
        private Quaternion _qSpine, _qUpperLegL, _qUpperLegR, _qLowerLegL, _qLowerLegR, _qShoulderL, _qShoulderR, _qElbowL, _qElbowR;
        // Foot bind LOCAL rotations. The crouch ankle write is an ABSOLUTE world write, so it must reset to
        // these first every frame - otherwise it reads its own previous output and becomes a self-feeding
        // filter that freezes a rotated ankle into the standing pose. See SolveLegIk.
        private Quaternion _qFootL = Quaternion.identity, _qFootR = Quaternion.identity;

        public Transform Spine { get { return _bSpine; } }
        public Transform Head { get { return _bHead; } }
        public Transform HandR { get { return _bHandR; } }
        public Transform HandL { get { return _bHandL; } }

        // Crouch leg IK: the body drop lowers the hips, then a per-leg 2-bone IK re-plants each ankle at its
        // captured STANDING world target so the feet stay on the deck at any depth (a squat, not a bow or a
        // kneel). All bind data is captured at FIT time (root scale 1, body planted at standing height) and
        // the aiming is axis-agnostic - aim the bone's captured local aim-axis at the target - so the rig's
        // unreliable, mirrored per-bone axes never enter the math.
        private bool _legIkReady;
        private float _thighLenL, _shinLenL, _thighLenR, _shinLenR;
        private Vector3 _footLocalL, _footLocalR;
        private Quaternion _footRotRootL = Quaternion.identity, _footRotRootR = Quaternion.identity;
        private Vector3 _thighAimLocalL, _shinAimLocalL, _thighAimLocalR, _shinAimLocalR;
        private float _hipAboveFoot;

        // Held-tool arm. The item's target pose is handed in each frame and goes STALE after a frame without a
        // refresh, which is how a drop lowers the arm without anyone having to say so.
        private readonly ArmIk _armR = new ArmIk();
        private readonly ArmIk _armL = new ArmIk();
        private Transform _heldItem, _heldFollower;
        private Vector3 _heldPos;
        private Quaternion _heldRot = Quaternion.identity;
        private bool _heldBig;
        private int _heldFrame = -10;
        private Transform _heldView;          // the local player's pointer, when this is the local body

        // Interaction poses (wheel, winch, pump, sail pusher, mooring rope, big items). Handed in each frame like
        // the held item and stale after a frame without a refresh. The last targets are kept so an arm lowers
        // along the path it came up on.
        private InteractionKind _ixKind;
        private Transform _ixTarget;
        private int _ixFrame = -10;
        private Vector3 _ixOffset;           // root-local step toward a control, eased
        private float _ixYaw;                // degrees the body turns to face a control, eased
        // The same two, eased in the world's orientation while a control holds the body in place, so a view turning
        // round the root does not swing the body off the control.
        private Vector3 _ixOffsetWorld;
        private float _ixYawWorld;
        private bool _ixWorldSynced;         // false until the pair above is first taken from the root-local pair
        private Vector3 _shoulderMidRoot;    // root-local shoulder midpoint at the planted rest pose
        private float _shoulderHalf = 0.18f; // half the distance between the shoulder joints
        private readonly RotorGrip _rotor = new RotorGrip();
        private readonly TillerGrip _tiller = new TillerGrip();
        private float _tillerIntrusion = float.PositiveInfinity;   // how far a held tiller may reach into the body (see KeepClearOfTiller)
        private bool _tillerRightHand;                               // the hand that reach was measured for
        private HandGrip _lastGripR, _lastGripL;
        private ItemPoseResult _itemPose;
        private bool _itemPoseValid;
        private float _itemBlend;

        // Sitting and lying, handed in each frame like an interaction and stale after a frame without a refresh.
        // The last seat is kept so the body rises from where it sat rather than from nowhere.
        private int _seatFrame = -10, _lieFrame = -10;
        private Vector3 _seatHips, _seatForward, _lieHead, _lieAlong, _lieUp;
        private SeatPose _seatPose;
        private float _seatFloorY;
        private float _seat01, _lie01;
        // Turning on the seat (legs swung over a rail, a spar straddled or sat on sideways) is animated here, from
        // the facing each SetSeat hands in, so a crewmate's body swings round the same way as your own. The legs
        // tuck up and the hands go down on the seat while it turns.
        private bool _seatTracking;
        private float _seatYaw, _seatYawGoal, _seatYawVel, _seatRawYaw;
        private float _seatNextSign = 1f;
        private float _tuck, _straddle01;
        private Vector3 _seatHipsOffset, _lastSeatHipsRel;
        // Sitting on the floor: how much of each floor pose is showing (legs out, cross-legged, one knee up, knees
        // hugged), eased, so changing pose moves the legs and arms over rather than jumping.
        private readonly float[] _floorW = new float[4];
        private static readonly float[] FloorLean = { -14f, 2f, -6f, 14f };
        // Leaning and turning off the seat toward a wheel, winch or pump worked from it, eased. Both are one
        // rotation on the spine after the seat has placed the body, and both ease back to nothing when it is let go.
        private float _seatLeanDeg, _seatTurnDeg;
        // How far in front of the hips the seated slouch carries the shoulders, in meters.
        private const float SeatedShoulderForward = 0.05f;
        // Knocked down: the bones copy a ragdoll's parts, handed in each frame and stale after a frame without a refresh.
        // The drawn pose follows the one handed in, so a crewmate's fall arriving a few times a second still moves
        // smoothly. The last drawn pose is kept: getting up starts from it.
        private Transform _bPelvis;
        private Vector3 _pelvisBindPos;
        private Quaternion _qPelvis = Quaternion.identity, _qHead = Quaternion.identity;
        private readonly Quaternion[] _ragRot = new Quaternion[RagdollParts];
        private readonly Quaternion[] _ragDrawn = new Quaternion[RagdollParts];
        private readonly Quaternion[] _ragBefore = new Quaternion[RagdollParts];
        private Vector3 _ragPelvis, _ragPelvisDrawn;
        private float _ragFloorY;
        private FallReaction _ragReaction;
        private int _ragFrame = -10;
        private float _rag01;
        private bool _ragHasDrawn;
        private float _coverFace01, _coverHead01, _flail01;
        // Getting up from a ragdoll, driven by a progress value handed in each frame.
        private int _getUpFrame = -10;
        private float _getUpTarget, _getUpShown, _getUp01;
        private bool _getUpBegun, _guFaceDown;
        private Vector3 _guPelvis, _guAlong;
        private float _guFloorY;
        private Vector3 _guFootL, _guFootR, _guPole, _guHandL, _guHandR;
        private float _guFootLevel, _guHandW;
        // Swimming, handed in each frame like sitting: treading water when still, a crawl with a flutter kick when moving,
        // tipped toward where the player looks when under the surface.
        private int _swimFrame = -10;
        private bool _swimUnder;
        private Vector3 _swimVel;
        private float _swim01, _swimMove01, _swimPhase, _swimBack, _swimFlat;
        // The head's and chest's facing and up at the planted rest pose, in their own bones' space.
        private Vector3 _headFwdLocal = Vector3.forward, _headUpLocal = Vector3.up, _chestFwdLocal = Vector3.forward, _chestUpLocal = Vector3.up;
        private Vector3 _hipMidRoot;        // root-local hip joint midpoint at the planted rest pose
        private float _headAboveSoles;      // head bone over the soles at the planted rest pose

        // Frame at which to check that vanilla's Start actually dressed the clone. 0 = done.
        private int _dressCheckFrame;
        // The sole-offset nudge the plant was fitted with, so a config change can be applied as a delta.
        private float _fittedNudge;

        private SyntyBody(string name, Transform root, Func<float> feetLocalY)
        {
            _name = name;
            _root = root;
            _feetLocalY = feetLocalY;
            Poses = new PoseStack(name);
        }

        // ---- construction ------------------------------------------------------------------------------

        /// <summary>
        /// Clone the shared template under <paramref name="root"/> and set up its rig. Returns null if no
        /// template exists yet (no shopkeeper has loaded this session) or the clone failed - callers are
        /// expected to retry on a timer rather than treat that as an error.
        /// </summary>
        /// <param name="root">Transform the body hangs under; the caller places it every frame.</param>
        /// <param name="name">Shown in pose-claim logs. Use something that identifies whose body this is.</param>
        /// <param name="appearance">Written BEFORE activation - see <see cref="PlayerAppearance"/>.</param>
        /// <param name="layer">Layer for the whole clone. 0 (Default) is what both cameras render.</param>
        /// <param name="feetLocalY">Root-local Y to plant the SOLES at, evaluated at fit time. Negative: the
        /// root sits above the ground. The sole-offset config nudge is added on top of whatever this returns.</param>
        public static SyntyBody TryBuild(Transform root, string name, PlayerAppearance appearance, int layer, Func<float> feetLocalY)
        {
            if (root == null || feetLocalY == null) return null;
            if (!BodyTemplate.Ensure()) return null;

            GameObject body = null;
            try
            {
                body = UnityEngine.Object.Instantiate(BodyTemplate.Instance);
                body.name = "Body";
                // Defensive: the LIVE clone must never act as a merchant or carry physics, even if the cached
                // template somehow retained anything. Disable immediately (Destroy is deferred) before it goes live.
                foreach (var sk in body.GetComponentsInChildren<Shopkeeper>(true)) { sk.enabled = false; UnityEngine.Object.Destroy(sk); }
                foreach (var pd in body.GetComponentsInChildren<PortDude>(true)) { pd.enabled = false; UnityEngine.Object.Destroy(pd); }
                foreach (var col in body.GetComponentsInChildren<Collider>(true)) { col.enabled = false; UnityEngine.Object.Destroy(col); }
                foreach (var rb in body.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.Destroy(rb);

                // Parent WITHOUT changing the clone's own local scale (the Synty rig carries a baked import
                // scale; resetting it to 1 would produce a giant). worldPositionStays:false keeps the
                // template's local TRS.
                body.transform.SetParent(root, false);
                body.transform.localRotation = Quaternion.identity;
                // The Synty modular root pivot is at the feet (verified: armature Root at localPos 0). Start
                // roughly right; the fit measures the real number once the skinned bounds exist.
                body.transform.localPosition = new Vector3(0f, -0.9f, 0f);

                // Appearance MUST be written while the clone is still INACTIVE. Activating is what fires
                // vanilla CharacterCustomizer.Start(), and that is the pass which actually rebuilds the mesh
                // from these fields; applying afterwards is silently ignored. That same rebuild is why every
                // body would otherwise wear the face of whichever shopkeeper loaded first.
                appearance.Apply(body);
                body.SetActive(true);

                foreach (var smr in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    smr.enabled = true;
                    smr.allowOcclusionWhenDynamic = false;
                }
                BodyTemplate.SetLayerRecursive(body.transform, layer);

                var self = new SyntyBody(name, root, feetLocalY);
                self._instance = body;
                self._renderers = body.GetComponentsInChildren<Renderer>(true);
                self._dressCheckFrame = Time.frameCount + 2; // Start runs before next frame's Update
                self.SetupRig(body);
                return self;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[PlayerModel] Body build failed for '{name}': {e}");
                if (body != null) UnityEngine.Object.Destroy(body);
                return null;
            }
        }

        /// <summary>Locate the Synty bones and capture their bind (rest) localRotations.</summary>
        private void SetupRig(GameObject body)
        {
            RigReady = false;
            var r = body.transform;
            _bSpine     = BodyTemplate.FindDeep(r, "Spine_01");
            _bUpperLegL = BodyTemplate.FindDeep(r, "UpperLeg_L"); _bUpperLegR = BodyTemplate.FindDeep(r, "UpperLeg_R");
            _bLowerLegL = BodyTemplate.FindDeep(r, "LowerLeg_L"); _bLowerLegR = BodyTemplate.FindDeep(r, "LowerLeg_R");
            _bShoulderL = BodyTemplate.FindDeep(r, "Shoulder_L"); _bShoulderR = BodyTemplate.FindDeep(r, "Shoulder_R"); // upper arm
            _bElbowL    = BodyTemplate.FindDeep(r, "Elbow_L");    _bElbowR    = BodyTemplate.FindDeep(r, "Elbow_R");    // forearm
            // Held-tool arm: the hand is the forearm's end effector. Fall back to the forearm's first child.
            _bHandR = BodyTemplate.FindDeep(r, "Hand_R") ?? BodyTemplate.FindDeep(r, "Wrist_R");
            if (_bHandR == null && _bElbowR != null && _bElbowR.childCount > 0) _bHandR = _bElbowR.GetChild(0);
            _bHandL = BodyTemplate.FindDeep(r, "Hand_L") ?? BodyTemplate.FindDeep(r, "Wrist_L");
            if (_bHandL == null && _bElbowL != null && _bElbowL.childCount > 0) _bHandL = _bElbowL.GetChild(0);
            _bHead = BodyTemplate.FindDeep(r, "Head");
            if (_bHead != null) _qHead = _bHead.localRotation;
            // The hips bone, which both thighs and the spine hang off. A ragdoll moves it; nothing else does.
            _bPelvis = _bUpperLegL != null ? _bUpperLegL.parent : null;
            if (_bPelvis != null && _bPelvis != r)
            {
                _qPelvis = _bPelvis.localRotation;
                _pelvisBindPos = _bPelvis.localPosition;
            }
            else _bPelvis = null;

            // Crouch IK ankle bones (Synty: UpperLeg -> LowerLeg -> Foot). Prefer Foot_L/R, then Ankle_L/R,
            // then the LowerLeg's first child; if none exist the ankle is approximated at capture time.
            _bFootL = BodyTemplate.FindDeep(r, "Foot_L") ?? BodyTemplate.FindDeep(r, "Ankle_L");
            _bFootR = BodyTemplate.FindDeep(r, "Foot_R") ?? BodyTemplate.FindDeep(r, "Ankle_R");
            if (_bFootL == null && _bLowerLegL != null && _bLowerLegL.childCount > 0) _bFootL = _bLowerLegL.GetChild(0);
            if (_bFootR == null && _bLowerLegR != null && _bLowerLegR.childCount > 0) _bFootR = _bLowerLegR.GetChild(0);
            _legIkReady = false; // captured once the body is planted (Fit -> CaptureLegIkBind)

            if (_bSpine != null) _qSpine = _bSpine.localRotation;
            if (_bUpperLegL != null) _qUpperLegL = _bUpperLegL.localRotation;
            if (_bUpperLegR != null) _qUpperLegR = _bUpperLegR.localRotation;
            if (_bLowerLegL != null) _qLowerLegL = _bLowerLegL.localRotation;
            if (_bLowerLegR != null) _qLowerLegR = _bLowerLegR.localRotation;
            if (_bFootL != null) _qFootL = _bFootL.localRotation;
            if (_bFootR != null) _qFootR = _bFootR.localRotation;
            if (_bShoulderL != null) _qShoulderL = _bShoulderL.localRotation;
            if (_bShoulderR != null) _qShoulderR = _bShoulderR.localRotation;
            if (_bElbowL != null) _qElbowL = _bElbowL.localRotation;
            if (_bElbowR != null) _qElbowR = _bElbowR.localRotation;

            RigReady = _bUpperLegL != null && _bUpperLegR != null; // legs are the minimum for a walk
            if (!RigReady)
                Plugin.Log.LogWarning($"[PlayerModel] {_name}: leg bones not found; the body will stay static.");
        }

        /// <summary>Any bone by name, cached. Returns null if this rig has no such bone.</summary>
        public Transform GetBone(string boneName)
        {
            if (_instance == null || string.IsNullOrEmpty(boneName)) return null;
            Transform t;
            if (_boneCache.TryGetValue(boneName, out t)) return t;
            t = BodyTemplate.FindDeep(_instance.transform, boneName);
            _boneCache[boneName] = t;
            return t;
        }

        // ---- lifecycle ---------------------------------------------------------------------------------

        public void SetRenderersEnabled(bool on)
        {
            if (_renderers == null) return;
            // The chest-down view never shows the arms, so they stay off while it is on (see SetChestFade).
            bool armsOff = on && _fadeSaved != null && _fadeArm != null;
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                r.enabled = on && !(armsOff && i < _fadeArm.Length && _fadeArm[i]);
            }
        }

        // The chest-down view of your own body in first person (see LocalBody): each renderer's own materials are kept
        // aside while copies on the fade shader stand in.
        private Material[][] _fadeSaved;
        private readonly Dictionary<Material, Material> _fadeCopies = new Dictionary<Material, Material>();

        /// <summary>
        /// Draw this body for its own eyes: the arms not at all, and the rest with <paramref name="fadeShader"/>
        /// (SailwindPlayerModel/SeatedBodyFade), solid up to <paramref name="aboveHips"/> meters over the hip joints
        /// and see-through from <paramref name="belowShoulders"/> meters under the shoulders, fading between. Both
        /// are measured from the pose every call, so the fade follows a lean. Call every frame while it should show;
        /// <see cref="ClearChestFade"/> puts the body back as it was.
        ///
        /// The arms go because the hands would never match what the eyes see: the game draws a held item framed in
        /// front of the view, not in the hands.
        /// </summary>
        public void SetChestFade(Shader fadeShader, float belowShoulders, float aboveHips)
        {
            if (_renderers == null || fadeShader == null) return;
            bool first = _fadeSaved == null;
            if (first)
            {
                var arms = ArmParts();
                _fadeArm = new bool[_renderers.Length];
                _fadeArmCount = 0;
                _fadeSaved = new Material[_renderers.Length][];
                for (int i = 0; i < _renderers.Length; i++)
                {
                    var r = _renderers[i];
                    if (r == null) continue;
                    var own = r.sharedMaterials;
                    _fadeSaved[i] = own;
                    var swapped = new Material[own.Length];
                    for (int m = 0; m < own.Length; m++) swapped[m] = FadeCopy(own[m], fadeShader);
                    r.sharedMaterials = swapped;
                    if (arms.Contains(r.gameObject)) { _fadeArm[i] = true; _fadeArmCount++; r.enabled = false; }
                }
            }
            // The arms are hidden once, here: while the fade is on, SetRenderersEnabled leaves them off when it shows
            // the body.

            float shoulders = _bShoulderL != null && _bShoulderR != null
                ? (_bShoulderL.position.y + _bShoulderR.position.y) * 0.5f
                : _instance.transform.position.y + MeasuredHeight * 0.8f;
            float hips = _bUpperLegL != null && _bUpperLegR != null
                ? (_bUpperLegL.position.y + _bUpperLegR.position.y) * 0.5f
                : shoulders - 0.5f;
            float top = shoulders - belowShoulders;
            float bottom = Mathf.Min(hips + aboveHips, top - 0.05f);
            foreach (var copy in _fadeCopies.Values)
            {
                if (copy == null) continue;
                copy.SetFloat("_FadeTop", top);
                copy.SetFloat("_FadeBottom", bottom);
            }
            // Once per chest-down view: a restyle from the character screen rebuilds the fade without ending it.
            if (first && !_fadeLogged)
            {
                _fadeLogged = true;
                Plugin.Log.LogInfo($"[PlayerModel] seated first-person body: shoulders y {shoulders:F2}, hips y {hips:F2}, " +
                    $"solid below {bottom:F2}, gone above {top:F2}; {_fadeCopies.Count} material(s), {_fadeArmCount} arm part(s) hidden");
            }
        }

        private bool[] _fadeArm;      // index-aligned with _renderers: true for an arm part while the fade is on
        private int _fadeArmCount;
        private bool _fadeLogged;     // the chest-down view has been logged; cleared by EndChestFade

        /// <summary>Every arm, hand, shoulder and elbow part the character customizer can put on this body.</summary>
        private HashSet<GameObject> ArmParts()
        {
            var parts = new HashSet<GameObject>();
            var c = _instance != null ? _instance.GetComponentInChildren<PsychoticLab.CharacterCustomizer>(true) : null;
            if (c == null) return parts;
            System.Action<List<GameObject>> add = list => { if (list != null) foreach (var go in list) if (go != null) parts.Add(go); };
            foreach (var g in new[] { c.male, c.female })
            {
                if (g == null) continue;
                add(g.arm_Upper_Right); add(g.arm_Upper_Left);
                add(g.arm_Lower_Right); add(g.arm_Lower_Left);
                add(g.hand_Right); add(g.hand_Left);
            }
            if (c.allGender != null)
            {
                add(c.allGender.shoulder_Attachment_Right); add(c.allGender.shoulder_Attachment_Left);
                add(c.allGender.elbow_Attachment_Right); add(c.allGender.elbow_Attachment_Left);
            }
            return parts;
        }

        private Material FadeCopy(Material own, Shader fadeShader)
        {
            if (own == null) return null;
            Material copy;
            if (_fadeCopies.TryGetValue(own, out copy) && copy != null) return copy;
            copy = new Material(fadeShader) { name = own.name + " (seated fade)" };
            copy.CopyPropertiesFromMaterial(own);
            // The copy brings the body's opaque render queue with it; see-through has to draw after the world.
            copy.renderQueue = -1;
            copy.shaderKeywords = new string[0];
            _fadeCopies[own] = copy;
            return copy;
        }

        /// <summary>Put the body's own materials and arms back after <see cref="SetChestFade"/>, and drop the copies.</summary>
        public void ClearChestFade()
        {
            if (_fadeSaved != null && _renderers != null)
            {
                for (int i = 0; i < _renderers.Length && i < _fadeSaved.Length; i++)
                {
                    if (_renderers[i] == null) continue;
                    if (_fadeSaved[i] != null) _renderers[i].sharedMaterials = _fadeSaved[i];
                    // Back on with the rest of the body; whoever hides the body next hides these too.
                    if (_fadeArm != null && i < _fadeArm.Length && _fadeArm[i]) _renderers[i].enabled = true;
                }
            }
            _fadeSaved = null;
            _fadeArm = null;
            _fadeArmCount = 0;
            foreach (var copy in _fadeCopies.Values)
                if (copy != null) UnityEngine.Object.Destroy(copy);
            _fadeCopies.Clear();
        }

        /// <summary>
        /// The chest-down view is over (the player stood up, or the view left first person): put the body back as
        /// <see cref="ClearChestFade"/> does, and log the next one again. Cheap to call every frame.
        /// </summary>
        internal void EndChestFade()
        {
            if (_fadeSaved != null) ClearChestFade();
            _fadeLogged = false;
        }

        /// <summary>True while <see cref="SetChestFade"/> has the body's materials swapped.</summary>
        public bool ChestFadeOn { get { return _fadeSaved != null; } }

        /// <summary>
        /// Restyle this body in place after an appearance change. Deliberately NOT a destroy-and-rebuild: a
        /// rebuild is throttled, it is gated on scaled Time.time (frozen while the pause menu holds timeScale
        /// at 0, which is exactly where a character screen lives), and it would re-run the leg-IK bind
        /// capture, which is unsafe to repeat - see CaptureLegIkBind.
        /// </summary>
        public void RefreshAppearance(PlayerAppearance appearance)
        {
            if (_instance == null) return;
            ClearChestFade();   // the restyle writes the body's own materials; the fade copies them again next frame
            var c = _instance.GetComponentInChildren<PsychoticLab.CharacterCustomizer>(true);
            if (c != null) appearance.ApplyLive(c);
        }

        public void Destroy()
        {
            Poses.Clear();
            ClearChestFade();
            if (_scorch != null) { _scorch.Destroy(); _scorch = null; }
            PlayerAppearance.ReleaseOwnedMaterial(_instance);
            if (_instance != null) UnityEngine.Object.Destroy(_instance);
            _instance = null;
            _renderers = null;
            _boneCache.Clear();
            RigReady = false;
            _legIkReady = false;
            _hasBodyBase = false;
            _crouch01 = 0f;
            _lookPitch = 0f;
            _animSpeed = 0f;
            _heldItem = null;
            _heldFollower = null;
            _ixTarget = null;
            _ixKind = InteractionKind.None;
            _ixOffset = Vector3.zero;
            _ixYaw = 0f;
            _ixOffsetWorld = Vector3.zero;
            _ixYawWorld = 0f;
            _ixWorldSynced = false;
            _heldView = null;
            _rotor.Release();
            _tiller.Release();
            _tillerIntrusion = float.PositiveInfinity;
            _seatLeanDeg = 0f;
            _seatTurnDeg = 0f;
            _fitWaitFrames = 0;
            _itemPoseValid = false;
            _itemBlend = 0f;
        }

        // ---- held items and controls -------------------------------------------------------------------

        /// <summary>
        /// Hand in this frame's pose for the item this body holds, as the game has it. How it ends up held is up
        /// to the item (see ItemPoses): most items are drawn in the hands in a pose that suits them, and the
        /// oar, fishing rod and chip log stay where the game has them with the hands reaching. <paramref name="follower"/>
        /// (optional) is moved with the item, for an item whose physics object is a separate transform.
        /// Co-op calls this for crewmates.
        /// </summary>
        public void SetHeldItemPose(Transform item, Transform follower, Vector3 worldPos, Quaternion worldRot, bool big)
        {
            SetHeldItem(item, follower, worldPos, worldRot, big, null);
        }

        /// <summary>
        /// Same as <see cref="SetHeldItemPose"/>, with the holder's own view transform (the local player's
        /// pointer). With it the pose reads exactly what the game is doing with the item: a swing, a tilt, the
        /// pull toward the mouth. Without it those are estimated from where the item is.
        /// </summary>
        public void SetHeldItem(Transform item, Transform follower, Vector3 worldPos, Quaternion worldRot, bool big, Transform view)
        {
            _heldItem = item;
            _heldFollower = follower;
            _heldPos = worldPos;
            _heldRot = worldRot;
            _heldBig = big;
            _heldView = view;
            _heldFrame = Time.frameCount;
        }

        /// <summary>
        /// Show this body using a control or holding a rope this frame: the ship's wheel, a winch or bilge pump,
        /// a sail pusher, or a mooring rope. Call every frame while it lasts; one missed frame lowers the arms.
        /// Carried items go through <see cref="SetHeldItemPose"/> instead. Co-op calls this for crewmates.
        /// </summary>
        public void SetInteraction(InteractionKind kind, Transform target)
        {
            _ixKind = kind;
            _ixTarget = target;
            _ixFrame = Time.frameCount;
        }

        /// <summary>The interaction posed on the last tick. None when the hands are free.</summary>
        public InteractionKind CurrentInteraction { get; private set; }

        /// <summary>
        /// When true, this body never moves the held item's transform itself; its owner draws the item at render
        /// time using <see cref="TryGetItemRenderPose"/>. The local body uses this, because the game repositions
        /// your held item every frame and its physics reads that transform.
        /// </summary>
        public bool HeldItemRenderOnly { get; set; }

        /// <summary>
        /// Where the held item should be drawn this frame, and at what extra scale (a scroll or map unrolling),
        /// blended from where the game holds it (<paramref name="floating"/>) as the pose eases in. False when the
        /// game's own placement stands.
        /// </summary>
        public bool TryGetItemRenderPose(Vector3 floating, Quaternion floatingRot, out Vector3 pos, out Quaternion rot, out Vector3 scaleMul)
        {
            pos = floating; rot = floatingRot; scaleMul = Vector3.one;
            if (!_itemPoseValid || !_itemPose.Repose || _itemBlend <= 0.001f) return false;
            if (CurrentInteraction != InteractionKind.Carry && CurrentInteraction != InteractionKind.CarryBig && CurrentInteraction != InteractionKind.Rope) return false;
            pos = Vector3.Lerp(floating, _itemPose.Pos, _itemBlend);
            rot = Quaternion.Slerp(floatingRot, _itemPose.Rot, _itemBlend);
            scaleMul = Vector3.Lerp(Vector3.one, _itemPose.ScaleMul, _itemBlend);
            return true;
        }

        /// <summary>
        /// True when this body draws the held item itself, so the caller must NOT write the item's transform.
        /// Only meaningful after this frame's SetHeldItemPose.
        ///
        /// This MUST mirror every gate on the path to the item being written. If it said yes while nothing wrote
        /// the item, it would freeze in mid-air - which is exactly what happens if the rig or the arm capture
        /// failed, or if something has claimed the arms.
        /// </summary>
        public bool PlacesHeldItemInHand
        {
            get
            {
                return _instance != null
                       && RigReady
                       && (_armR.Ready || _armL.Ready)
                       && !HeldItemRenderOnly
                       && !(Time.frameCount - _ixFrame <= 1 && _ixKind != InteractionKind.None)
                       && !Poses.IsSuppressed(PoseParts.Arms)
                       && InteractionTuning.Enabled != null && InteractionTuning.Enabled.Value
                       && HeldToolPose.Mode != null
                       && HeldToolPose.Mode.Value == HeldPoseMode.ItemInHand
                       && _heldItem != null
                       && ItemPoses.Reposes(_heldItem);
            }
        }

        // ---- per-frame ---------------------------------------------------------------------------------

        /// <summary>
        /// Advance this body one frame: the one-time fit, then the pose. Call from LateUpdate, after the root
        /// has been placed. Never throws on a missing rig - it simply does nothing.
        /// </summary>
        /// <summary>
        /// Sit this body down this frame: hip joints at <paramref name="hipsWorld"/>, facing
        /// <paramref name="forwardWorld"/>, legs as <paramref name="legs"/> says, over a floor at
        /// <paramref name="floorWorldY"/>. Call every frame while seated; the body stands back up on its own once the
        /// calls stop. A big change of facing between calls is turned through, not jumped to.
        /// </summary>
        public void SetSeat(Vector3 hipsWorld, Vector3 forwardWorld, SeatPose pose, float floorWorldY)
        {
            _seatHips = hipsWorld;
            _seatForward = forwardWorld;
            _seatPose = pose;
            _seatFloorY = floorWorldY;
            _seatFrame = Time.frameCount;
        }

        /// <summary>
        /// Lay this body down this frame, on its back: head at <paramref name="headWorld"/>, feet toward
        /// <paramref name="alongWorld"/>, chest toward <paramref name="upWorld"/>. Call every frame while lying.
        /// </summary>
        public void SetLying(Vector3 headWorld, Vector3 alongWorld, Vector3 upWorld)
        {
            _lieHead = headWorld;
            _lieAlong = alongWorld;
            _lieUp = upWorld;
            _lieFrame = Time.frameCount;
        }

        /// <summary>True while the body is sitting or getting up from a seat.</summary>
        public bool IsSeated { get { return _seat01 > 0.01f; } }

        /// <summary>How many parts a ragdoll pose has (see <see cref="RagdollPart"/>).</summary>
        public const int RagdollParts = 11;

        /// <summary>
        /// Pose this body as a ragdoll this frame: the pelvis bone at <paramref name="pelvisWorld"/> and each ragdoll bone
        /// turned to <paramref name="rotationsWorld"/> (in <see cref="RagdollPart"/> order), over a floor at about
        /// <paramref name="floorWorldY"/>, with the arms doing <paramref name="reaction"/>. Call every frame while down;
        /// follow it with <see cref="SetGettingUp"/> to get up from where it lies.
        /// </summary>
        public void SetRagdoll(Vector3 pelvisWorld, Quaternion[] rotationsWorld, float floorWorldY, FallReaction reaction)
        {
            if (rotationsWorld == null || rotationsWorld.Length < RagdollParts) return;
            _ragPelvis = pelvisWorld;
            for (int i = 0; i < RagdollParts; i++) _ragRot[i] = rotationsWorld[i];
            _ragFloorY = floorWorldY;
            _ragReaction = reaction;
            _ragFrame = Time.frameCount;
        }

        /// <summary>
        /// Get this body up from the last ragdoll pose it showed, <paramref name="progress01"/> of the way to standing where
        /// its root is. Call every frame while it gets up.
        /// </summary>
        public void SetGettingUp(float progress01)
        {
            _getUpTarget = Mathf.Clamp01(progress01);
            _getUpFrame = Time.frameCount;
        }

        /// <summary>True while the body is down or getting back up.</summary>
        public bool IsDowned { get { return _rag01 > 0.01f || _getUp01 > 0.01f; } }

        /// <summary>
        /// Swim this frame. The body lies along <paramref name="velocityWorld"/>, the way it is actually going: head
        /// first, or feet first when that is backward, and tipped up or down by the climb or dive of the swim itself. It
        /// treads water upright only at the surface with no way on; under the surface it stays flat. The head turns to
        /// look where the player is looking rather than the body turning. Call every frame while in the water.
        /// </summary>
        public void SetSwimming(bool underwater, Vector3 velocityWorld)
        {
            _swimUnder = underwater;
            _swimVel = velocityWorld;
            _swimFrame = Time.frameCount;
        }

        /// <summary>True while the body is swimming or coming out of it.</summary>
        public bool IsSwimming { get { return _swim01 > 0.01f; } }

        /// <summary>The bone a ragdoll part poses.</summary>
        internal Transform RagdollBone(RagdollPart part)
        {
            switch (part)
            {
                case RagdollPart.Pelvis: return _bPelvis;
                case RagdollPart.Chest: return _bSpine;
                case RagdollPart.Head: return _bHead;
                case RagdollPart.UpperArmL: return _bShoulderL;
                case RagdollPart.ForearmL: return _bElbowL;
                case RagdollPart.UpperArmR: return _bShoulderR;
                case RagdollPart.ForearmR: return _bElbowR;
                case RagdollPart.ThighL: return _bUpperLegL;
                case RagdollPart.ShinL: return _bLowerLegL;
                case RagdollPart.ThighR: return _bUpperLegR;
                case RagdollPart.ShinR: return _bLowerLegR;
            }
            return null;
        }

        /// <summary>Which way this body's face points, as posed right now.</summary>
        public Vector3 HeadForward { get { return _bHead != null ? _bHead.rotation * _headFwdLocal : Vector3.forward; } }

        /// <summary>Where this body's eyes are and which way is up for its head, as posed right now.</summary>
        public bool TryGetEye(out Vector3 eye, out Vector3 headUp)
        {
            eye = headUp = Vector3.zero;
            if (_bHead == null || _needsFit) return false;
            Quaternion r = _bHead.rotation;
            headUp = r * _headUpLocal;
            eye = _bHead.position + headUp * 0.09f + (r * _headFwdLocal) * 0.11f;
            return true;
        }

        /// <summary>
        /// Where this body's lips are and which way its face points, as posed right now. The same mouth drinks, food
        /// and the pipe are brought to.
        /// </summary>
        internal bool TryGetMouth(out Vector3 mouth, out Vector3 facing)
        {
            mouth = facing = Vector3.zero;
            if (_bHead == null || _needsFit) return false;
            facing = _bHead.rotation * _headFwdLocal;
            mouth = HeadMouth();
            return true;
        }

        /// <summary>
        /// The mouth, measured from the head bone along the face, so it tips and turns with the head. Along the body's
        /// flat heading and world up it stayed put while the head pitched down with the look-lean, and a body looking
        /// down drank at its nose.
        /// </summary>
        private Vector3 HeadMouth()
        {
            Quaternion r = _bHead.rotation;
            return _bHead.position + (r * _headFwdLocal) * ItemPoseTuning.MouthForward.Value + (r * _headUpLocal) * ItemPoseTuning.MouthUp.Value;
        }

        /// <summary>
        /// Smoke, flames and steam (each 0 to 1) from the seat of this body's pants this frame: sat on a lit stove for
        /// too long, in the rain or not. Call every frame while it lasts; all die away on their own once the calls stop.
        /// </summary>
        public void SetScorch(float smoke01, float fire01, float steam01)
        {
            _scorchSmoke = Mathf.Clamp01(smoke01);
            _scorchFire = Mathf.Clamp01(fire01);
            _scorchSteam = Mathf.Clamp01(steam01);
            _scorchFrame = Time.frameCount;
        }

        /// <summary>Re-place the scorch effect (see <see cref="SetScorch"/>) at <paramref name="seat"/>, for an owner that knows better where it should be this frame.</summary>
        public void MoveScorch(Vector3 seat)
        {
            if (_scorch != null) _scorch.MoveTo(seat);
        }

        private ScorchEffect _scorch;
        private float _scorchSmoke, _scorchFire, _scorchSteam;
        private int _scorchFrame = -10;

        private void UpdateScorch(float dt)
        {
            bool fresh = Time.frameCount - _scorchFrame <= 1;
            if (!fresh && _scorch == null) return;
            if (_scorch == null) _scorch = new ScorchEffect();
            Vector3 seat = _bUpperLegL != null && _bUpperLegR != null
                ? (_bUpperLegL.position + _bUpperLegR.position) * 0.5f - _instance.transform.up * 0.06f - _instance.transform.forward * 0.05f
                : _instance.transform.position + Vector3.up * 0.85f;
            _scorch.Update(seat, fresh ? _scorchSmoke : 0f, fresh ? _scorchFire : 0f, fresh ? _scorchSteam : 0f, dt);
        }

        public void Tick(float dt)
        {
            if (_instance == null || _root == null) return;
            _itemPoseValid = false;
            if (_dressCheckFrame > 0 && Time.frameCount >= _dressCheckFrame) { _dressCheckFrame = 0; VerifyDressed(); }
            if (_needsFit) Fit();
            else if (_hasBodyBase) ReplantIfNudged();
            DriveAnimation(Mathf.Max(dt, 1e-4f));
            UpdateScorch(dt);
            // OUTSIDE DriveAnimation on purpose: that returns early on a rig whose leg bones were not found,
            // and a claimant writing the body's own position (PoseParts.Body) needs no bones at all. A claim
            // that silently never ran would be far worse than a body that does not walk.
            Poses.RunWrites(this);
        }

        /// <summary>
        /// Vanilla's Start builds the outfit, and if it throws part-way (an index its lists cannot serve) the
        /// body is left at its bare defaults with no word from anyone. So count what it enabled: a dressed
        /// character has at least a dozen parts on. If it came up short, say so with the numbers a report
        /// needs, then run the build again ourselves so the exception, if any, lands in OUR log.
        /// </summary>
        private void VerifyDressed()
        {
            try
            {
                var c = _instance.GetComponentInChildren<PsychoticLab.CharacterCustomizer>(true);
                if (c == null) return;
                int n = c.enabledObjects != null ? c.enabledObjects.Count : 0;
                if (n >= 12) return;
                Plugin.Log.LogWarning($"[PlayerModel] '{_name}' came up with only {n} part(s) enabled " +
                    $"(female={c.isFemale} head={c.maleHeadAllElements} eyebrow={c.maleEyebrow} facialhair={c.maleFacialHair} " +
                    $"torso={c.maleTorso} hips={c.maleHips} legs={c.maleLeg_Right} hair={c.allGenderAll_Hair} hat={c.allGenderHeadCoverings_Base_Hair}; " +
                    $"{BodyTemplate.DescribeParts(_instance)}). Rebuilding the outfit.");
                try
                {
                    HarmonyLib.Traverse.Create(c).Method("UpdateModel").GetValue();
                    int n2 = c.enabledObjects != null ? c.enabledObjects.Count : 0;
                    Plugin.Log.LogInfo($"[PlayerModel] '{_name}' rebuild enabled {n2} part(s)");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[PlayerModel] '{_name}' outfit rebuild threw: {(e.InnerException ?? e).Message}");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[PlayerModel] VerifyDressed: " + e.Message); }
        }

        /// <summary>
        /// One-time vertical fit: plant the soles at the caller's requested root-local Y and capture the bind
        /// data the IK needs. Deferred until the skinned bounds are valid, so it may take a few frames.
        ///
        /// PLANT ON THE BODY PIVOT, NOT THE RENDERER BOUNDS. The Synty rig's armature root and mesh origin
        /// already sit at the soles, whereas the skinned-mesh bounds box extends roughly 0.1m BELOW them and
        /// is inflated above by whatever hat, hair or feather the character wears. Fitting to the bounds
        /// therefore lifted every body by that padding - the reported "I float a few inches above the deck".
        /// Bounds are also axis-aligned and character-dependent, so they shift again the moment appearance
        /// selection changes which parts are enabled; the pivot does not.
        /// </summary>
        private void Fit()
        {
            if (_renderers == null || _renderers.Length == 0) { _needsFit = false; return; }
            // Only the parts actually shown, and only those with real bounds. The list holds every part the customizer
            // can switch on, hundreds of them on inactive objects, and an empty box at the world origin from any of
            // them would stretch the measured height down to world y 0. A disabled renderer with real bounds still
            // counts, so a hidden body can still fit.
            Bounds wb = default(Bounds);
            int used = 0, hidden = 0, hiddenAtOrigin = 0, shownEmpty = 0;
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                Bounds rb = r.bounds;
                bool empty = rb.size.sqrMagnitude < 1e-6f;
                if (!r.gameObject.activeInHierarchy)
                {
                    hidden++;
                    if (empty && rb.center.sqrMagnitude < 1e-6f) hiddenAtOrigin++;
                    continue;
                }
                if (empty) { shownEmpty++; continue; }
                if (used++ == 0) wb = rb;
                else wb.Encapsulate(rb);
            }
            if (used == 0 || wb.size.y < 0.5f)
            {
                // Bounds not ready yet (degenerate) - retry next frame.
                if (++_fitWaitFrames == 300)
                    Plugin.Log.LogInfo($"[PlayerModel] {_name} fit still waiting: {used} of {_renderers.Length} parts have bounds");
                return;
            }

            _fittedNudge = BodyTuning.SoleOffsetMeters.Value;
            float feetLocalY = _feetLocalY() + _fittedNudge;
            _instance.transform.localPosition = new Vector3(0f, feetLocalY, 0f);
            _bodyBaseLocalPos = _instance.transform.localPosition;
            _hasBodyBase = true;
            MeasuredHeight = wb.size.y;
            FittedFeetLocalY = feetLocalY;

            // Capture the IK binds NOW: the body is planted at standing height with root scale 1, so the feet
            // sit at their true standing spot and the segment lengths and aim axes are correct.
            CaptureLegIkBind();
            CaptureArmIkBind();
            _needsFit = false;

            // Ground truth for the next session's log: if the plant is off, this says by how much and in which
            // direction without anyone having to eyeball it. boundsMinBelowPivot is the erroneous lift the
            // pivot plant removed.
            float boundsMinLocalY = _root.InverseTransformPoint(new Vector3(wb.center.x, wb.min.y, wb.center.z)).y;
            Plugin.Log.LogInfo($"[PlayerModel] {_name} fit: feetLocalY={feetLocalY:F3} (nudge {BodyTuning.SoleOffsetMeters.Value:F3}), " +
                $"bodyH={MeasuredHeight:F3}, boundsMinBelowPivot={(feetLocalY - boundsMinLocalY):F3}, " +
                $"legIk={_legIkReady}, armIk={_armR.Ready}/{_armL.Ready}, " +
                $"measured {used} of {_renderers.Length} parts ({hidden} hidden parts skipped, {hiddenAtOrigin} of them report an empty box at the world origin; " +
                $"{shownEmpty} shown parts had no bounds yet), bounds bottom y {wb.min.y:F2}, root y {_root.position.y:F2}");
        }

        /// <summary>
        /// Apply a changed sole-offset config live, as a delta on the planted base. The fit itself must not
        /// re-run (see CaptureLegIkBind), but a vertical shift of the whole body is safe as long as the
        /// root-local standing ankle targets move with it; otherwise the leg IK would pull the feet back to
        /// the old height and stretch the legs. Before this the knob only took effect on the next body
        /// build, which read as "nothing I adjust does anything".
        /// </summary>
        private void ReplantIfNudged()
        {
            float nudge = BodyTuning.SoleOffsetMeters != null ? BodyTuning.SoleOffsetMeters.Value : 0f;
            float dy = nudge - _fittedNudge;
            if (Mathf.Abs(dy) < 1e-4f) return;
            _fittedNudge = nudge;
            _bodyBaseLocalPos.y += dy;
            FittedFeetLocalY += dy;
            _footLocalL.y += dy;
            _footLocalR.y += dy;
            _hipMidRoot.y += dy;
            // The whole body moves by dy in root space, so every captured root-local bone point moves with
            // it. The shoulders have to keep step with the hips or the torso height the seated reach, the
            // seated lean, the tiller hold and the swim pivot are measured from is out by the nudge.
            _shoulderMidRoot.y += dy;
            _instance.transform.localPosition = _bodyBaseLocalPos;
            Plugin.Log.LogInfo($"[PlayerModel] {_name} re-planted: sole offset {nudge:F3} (feetLocalY now {FittedFeetLocalY:F3})");
        }

        private void CaptureArmIkBind()
        {
            if (!_armR.Capture(_bShoulderR, _bElbowR, _bHandR))
                Plugin.Log.LogWarning($"[PlayerModel] {_name}: right arm bones not found (shoulder={_bShoulderR != null}, " +
                    $"elbow={_bElbowR != null}, hand={_bHandR != null}); held items will float as before.");
            if (!_armL.Capture(_bShoulderL, _bElbowL, _bHandL))
                Plugin.Log.LogWarning($"[PlayerModel] {_name}: left arm bones not found (shoulder={_bShoulderL != null}, " +
                    $"elbow={_bElbowL != null}, hand={_bHandL != null}); two-handed poses will use one hand.");
            if (_bShoulderL != null && _bShoulderR != null)
            {
                _shoulderMidRoot = _root.InverseTransformPoint((_bShoulderL.position + _bShoulderR.position) * 0.5f);
                _shoulderHalf = 0.5f * Vector3.Distance(_bShoulderL.position, _bShoulderR.position);
            }
            else
                _shoulderMidRoot = new Vector3(0f, FittedFeetLocalY + MeasuredHeight * 0.8f, 0f);
        }

        /// <summary>
        /// Capture the standing bind data the crouch leg IK needs, once the body is planted. Forces the legs to
        /// their bind pose first so a mid-gait fit frame cannot pollute the capture.
        ///
        /// UNSAFE TO RE-RUN as written. The foot's WORLD rotation is only correct here because both its parents
        /// were just restored to bind AND because _legIkReady stays false until this completes, so SolveLegIk's
        /// foot write cannot have run yet. Any future re-capture MUST restore foot.localRotation to the foot
        /// binds first, or it will read this method's own earlier output.
        /// </summary>
        private void CaptureLegIkBind()
        {
            _legIkReady = false;
            if (_bUpperLegL == null || _bUpperLegR == null || _bLowerLegL == null || _bLowerLegR == null)
            {
                Plugin.Log.LogWarning($"[PlayerModel] {_name}: leg bones missing; feet will not be IK-planted (body drop only).");
                return;
            }
            _bUpperLegL.localRotation = _qUpperLegL; _bUpperLegR.localRotation = _qUpperLegR;
            _bLowerLegL.localRotation = _qLowerLegL; _bLowerLegR.localRotation = _qLowerLegR;

            bool okL = CaptureOneLeg(_root, _bUpperLegL, _bLowerLegL, _bFootL,
                out _thighLenL, out _shinLenL, out _footLocalL, out _thighAimLocalL, out _shinAimLocalL, out _footRotRootL);
            bool okR = CaptureOneLeg(_root, _bUpperLegR, _bLowerLegR, _bFootR,
                out _thighLenR, out _shinLenR, out _footLocalR, out _thighAimLocalR, out _shinAimLocalR, out _footRotRootR);
            _legIkReady = okL && okR;
            // Standing hip height over the ankle, root-local; feeds the squat setback solve.
            if (_legIkReady)
                _hipAboveFoot = _root.InverseTransformPoint(_bUpperLegL.position).y - _footLocalL.y;
            // Where the hips and head sit on the standing body, for putting it on a seat or in a bed.
            _hipMidRoot = _root.InverseTransformPoint((_bUpperLegL.position + _bUpperLegR.position) * 0.5f);
            _headAboveSoles = _bHead != null ? _root.InverseTransformPoint(_bHead.position).y - FittedFeetLocalY : MeasuredHeight * 0.9f;
            // Which way the face and chest point, for aiming arms at the head while down and for the eyes.
            Transform inst = _instance.transform;
            if (_bHead != null)
            {
                _headFwdLocal = Quaternion.Inverse(_bHead.rotation) * inst.forward;
                _headUpLocal = Quaternion.Inverse(_bHead.rotation) * inst.up;
            }
            if (_bSpine != null)
            {
                _chestFwdLocal = Quaternion.Inverse(_bSpine.rotation) * inst.forward;
                _chestUpLocal = Quaternion.Inverse(_bSpine.rotation) * inst.up;
            }

            if (_bFootL == null || _bFootR == null)
                Plugin.Log.LogWarning($"[PlayerModel] {_name}: foot/ankle bone not found; the ankle is approximated from the shin (feet still planted).");
            if (!_legIkReady)
                Plugin.Log.LogWarning($"[PlayerModel] {_name}: degenerate leg lengths; feet will not be IK-planted (body drop only).");
        }

        /// <summary>
        /// Capture one leg's bind data. If the foot bone is null, approximate the ankle as
        /// LowerLeg + (LowerLeg - UpperLeg) (shin about thigh length, same direction). Returns false if a
        /// segment length is degenerate.
        /// </summary>
        private static bool CaptureOneLeg(Transform root, Transform hip, Transform knee, Transform foot,
            out float thighLen, out float shinLen, out Vector3 footLocal, out Vector3 thighAimLocal, out Vector3 shinAimLocal,
            out Quaternion footRotRoot)
        {
            Vector3 hp = hip.position, kp = knee.position;
            Vector3 ankle = foot != null ? foot.position : kp + (kp - hp);
            // Standing sole orientation, in ROOT space so it follows the body's FACING. Note that callers keep
            // the root yaw-only (bodies are world-upright and never heel), so `root.rotation * footRotRoot`
            // levels the sole to the WORLD horizon, not to the deck. That is deliberate and self-consistent
            // with the rest of the body. If a root is ever allowed to inherit boat pitch and roll, this
            // becomes deck-relative for free. Identity when there is no foot bone, which is harmless:
            // SolveLegIk skips the write in that case.
            footRotRoot = foot != null ? Quaternion.Inverse(root.rotation) * foot.rotation : Quaternion.identity;
            thighLen = Vector3.Distance(hp, kp);
            shinLen  = Vector3.Distance(kp, ankle);
            footLocal = root.InverseTransformPoint(ankle);
            Vector3 tAim = kp - hp;
            Vector3 sAim = ankle - kp;
            thighAimLocal = tAim.sqrMagnitude > 1e-8f ? hip.InverseTransformDirection(tAim.normalized) : Vector3.up;
            shinAimLocal  = sAim.sqrMagnitude > 1e-8f ? knee.InverseTransformDirection(sAim.normalized) : Vector3.up;
            return thighLen > 1e-3f && shinLen > 1e-3f;
        }

        // ---- the pose ----------------------------------------------------------------------------------

        /// <summary>
        /// Idle breathing, a speed-scaled walk cycle, the crouch squat, the torso look-lean and the held-tool
        /// arm, in that order, skipping whichever parts a pose claim has taken. Claim writes run last, in
        /// ascending priority, so the highest-priority claimant is the one left on the bones.
        /// </summary>
        private void DriveAnimation(float dt)
        {
            if (!RigReady) return;

            // --- gait tuning (fixed; these are the shape of a walk, not a preference) ---
            const float WalkFullSpeed = 1.4f;  // m/s at which the gait reaches full amplitude
            const float StrideRadPerM = 4.2f;  // gait phase advance per meter travelled (cadence)
            const float LegAmp        = 28f;   // deg, thigh swing
            const float KneeAmp       = 40f;   // deg, knee flex (one-directional)
            const float KneePhase     = 1.1f;  // rad, knee-flex offset within the stride
            const float ArmAmp        = 22f;   // deg, arm swing
            const float ElbowAmp      = 16f;   // deg, elbow flex
            const float BreatheAmp    = 2.2f;  // deg, idle spine sway
            const float BreatheHz     = 0.22f; // breaths per second
            const float CrouchSmooth  = 12f;   // easing rate toward the target (de-jitters a quantized source)

            float CrouchDrop      = BodyTuning.CrouchDropMeters.Value;
            float CrouchTorsoLean = BodyTuning.CrouchTorsoLeanDeg.Value;
            float CrouchArmBend   = BodyTuning.CrouchArmBendDeg.Value;
            float CrouchStrideCut = BodyTuning.CrouchStrideCut.Value;
            float LookPitchScale  = BodyTuning.LookPitchScale.Value;
            float LookPitchMaxDeg = BodyTuning.LookPitchMaxDeg.Value;

            // Ease the inputs. Frame-rate independent, and the same rate for crouch and look so a player who
            // crouches while looking down does not see the two halves of the fold arrive separately.
            float ease = 1f - Mathf.Exp(-CrouchSmooth * dt);
            _crouch01 = Mathf.Lerp(_crouch01, Crouch01Target, ease);
            _lookPitch = Mathf.Lerp(_lookPitch, LookPitchDegTarget, ease);
            _animSpeed = Mathf.Lerp(_animSpeed, SpeedMps, 1f - Mathf.Exp(-8f * dt));

            // Sitting and lying ease in and out at about the pace the seated view settles.
            bool seatFresh = Time.frameCount - _seatFrame <= 1;
            bool lieFresh = Time.frameCount - _lieFrame <= 1;
            _seat01 = Mathf.Lerp(_seat01, seatFresh ? 1f : 0f, 1f - Mathf.Exp(-7f * dt));
            _lie01 = Mathf.Lerp(_lie01, lieFresh ? 1f : 0f, 1f - Mathf.Exp(-5f * dt));
            if (!seatFresh && _seat01 < 0.002f) _seat01 = 0f;
            if (!lieFresh && _lie01 < 0.002f) _lie01 = 0f;
            if (seatFresh) TrackSeatTurn(dt);
            else _seatTracking = false;
            // Knocked down: a ragdoll takes the whole body at once (it started from this very pose), and getting up takes
            // over from it where it lies.
            bool ragFresh = Time.frameCount - _ragFrame <= 1;
            bool getUpFresh = Time.frameCount - _getUpFrame <= 1;
            if (getUpFresh && !_getUpBegun) BeginGetUp();
            ResetRagdollBones();
            if (ragFresh)
            {
                if (!_ragHasDrawn || _rag01 < 0.01f || _getUpBegun)
                {
                    _ragPelvisDrawn = _ragPelvis;
                    for (int i = 0; i < RagdollParts; i++) _ragDrawn[i] = _ragRot[i];
                    _ragHasDrawn = true;
                    _getUpBegun = false;
                    _getUp01 = 0f;
                }
                else
                {
                    float follow = 1f - Mathf.Exp(-25f * dt);
                    _ragPelvisDrawn = Vector3.Lerp(_ragPelvisDrawn, _ragPelvis, follow);
                    for (int i = 0; i < RagdollParts; i++) _ragDrawn[i] = Quaternion.Slerp(_ragDrawn[i], _ragRot[i], follow);
                }
                _rag01 = 1f;
            }
            else
            {
                // Getting up starts from the last ragdoll pose; anything else (the water, a cut-short fall) eases back to standing.
                _rag01 = getUpFresh ? 0f : Mathf.Max(0f, _rag01 - dt * 3.5f);
            }
            if (getUpFresh)
            {
                // Never backward, and eased, so progress arriving a few times a second still moves smoothly.
                _getUpShown = Mathf.Max(_getUpShown, Mathf.Lerp(_getUpShown, _getUpTarget, 1f - Mathf.Exp(-12f * dt)));
                _getUp01 = 1f;
            }
            else if (_getUpBegun)
            {
                _getUp01 = Mathf.Max(0f, _getUp01 - dt * 4f);
                if (_getUp01 <= 0f) { _getUpBegun = false; _getUpShown = 0f; }
            }
            bool swimFresh = Time.frameCount - _swimFrame <= 1 && _rag01 <= 0f && _getUp01 <= 0f;
            _swim01 = Mathf.Lerp(_swim01, swimFresh ? 1f : 0f, 1f - Mathf.Exp(-4f * dt));
            if (!swimFresh && _swim01 < 0.002f) _swim01 = 0f;
            if (_swim01 > 0f) TrackSwim(dt);
            float rest = Mathf.Max(Mathf.Max(_seat01, _lie01), Mathf.Max(Mathf.Max(_rag01, _getUp01), _swim01));
            // A seat or a bed stands in for the crouch rather than adding to it.
            float crouch = _crouch01 * (1f - rest);

            // Crouch-walk: the gait continues but with a shorter stride, so a crouched body still steps. The
            // phase advances even while a claim owns the legs, so releasing a claim does not snap the stride.
            float blend = Mathf.Clamp01(_animSpeed / WalkFullSpeed) * (1f - CrouchStrideCut * crouch) * (1f - rest);
            _gaitPhase = Mathf.Repeat(_gaitPhase + _animSpeed * StrideRadPerM * dt, 2f * Mathf.PI);
            float s    = Mathf.Sin(_gaitPhase);
            float sOpp = Mathf.Sin(_gaitPhase + Mathf.PI);

            var suppressed = Poses.Suppressed;
            bool legs  = (suppressed & PoseParts.Legs) == 0;
            bool spine = (suppressed & PoseParts.Spine) == 0;
            bool armL  = (suppressed & PoseParts.LeftArm) == 0;
            bool armR  = (suppressed & PoseParts.RightArm) == 0;
            bool bodyOffset = (suppressed & PoseParts.Body) == 0;

            // Legs and arms: WALK GAIT ONLY here (the crouch is added below as symmetric world-space pitches).
            if (legs)
            {
                BodyTemplate.SetSwing(_bUpperLegL, _qUpperLegL, Vector3.up, LegAmp * blend * s);
                BodyTemplate.SetSwing(_bUpperLegR, _qUpperLegR, Vector3.up, LegAmp * blend * sOpp);
                BodyTemplate.SetSwing(_bLowerLegL, _qLowerLegL, Vector3.back, KneeAmp * blend * Mathf.Max(0f, Mathf.Sin(_gaitPhase + KneePhase)));
                BodyTemplate.SetSwing(_bLowerLegR, _qLowerLegR, Vector3.back, KneeAmp * blend * Mathf.Max(0f, Mathf.Sin(_gaitPhase + Mathf.PI + KneePhase)));
            }

            // Arms swing about local -Y, opposite the same-side leg. DAMPED while crouched: a crouched player
            // holds the arms in a ready stance, and a full walk swing looks derpy. At full crouch the gait arm
            // swing is about 15% of normal.
            float armBlend = blend * (1f - 0.85f * crouch);
            if (armL)
            {
                BodyTemplate.SetSwing(_bShoulderL, _qShoulderL, Vector3.down, ArmAmp * armBlend * sOpp);
                BodyTemplate.SetSwing(_bElbowL, _qElbowL, Vector3.down, ElbowAmp * armBlend * (0.5f + 0.5f * sOpp));
            }
            if (armR)
            {
                BodyTemplate.SetSwing(_bShoulderR, _qShoulderR, Vector3.down, ArmAmp * armBlend * s);
                BodyTemplate.SetSwing(_bElbowR, _qElbowR, Vector3.down, ElbowAmp * armBlend * (0.5f + 0.5f * s));
            }
            // Idle breathing: subtle spine sway about local +Z (matches the game's own NPCAnimations convention).
            if (spine)
                BodyTemplate.SetSwing(_bSpine, _qSpine, Vector3.forward, Mathf.Sin(Time.time * BreatheHz * 2f * Mathf.PI) * BreatheAmp);

            if (crouch > 0.001f)
            {
                float armBend      = CrouchArmBend * crouch;
                float shoulderTuck = 0.35f * armBend;   // slight upper-arm raise off the same knob (ready stance)
                // Vector3.up (the OPPOSITE of the walk's -Y swing) so the forearms come FORWARD and up into a
                // ready stance; Vector3.down bent them backwards. Left and right are bind-mirrored, so one
                // axis and sign moves both symmetrically.
                if (armL && _bElbowL != null) _bElbowL.Rotate(Vector3.up, armBend, Space.Self);
                if (armR && _bElbowR != null) _bElbowR.Rotate(Vector3.up, armBend, Space.Self);
                if (armL && _bShoulderL != null) _bShoulderL.Rotate(Vector3.up, shoulderTuck, Space.Self);
                if (armR && _bShoulderR != null) _bShoulderR.Rotate(Vector3.up, shoulderTuck, Space.Self);
            }

            // LOOK-LEAN. The crouch fold is +CrouchTorsoLean about the body's own right = a FORWARD fold, and
            // MouseLook.rotationY is positive when looking UP, so NEGATE the pitch to make looking DOWN fold
            // FORWARD (and looking UP lean BACK). LookPitchScale set NEGATIVE flips the whole direction live.
            // Clamped so the torso never over-bends.
            float lookLean = Mathf.Clamp(-_lookPitch * LookPitchScale, -LookPitchMaxDeg, LookPitchMaxDeg);
            // The look part fades as the body faces away from the view, so looking sideways does not fold the torso.
            Vector3 bodyFlat = _instance.transform.forward, viewFlat = _root.forward;
            bodyFlat.y = 0f;
            viewFlat.y = 0f;
            if (bodyFlat.sqrMagnitude > 1e-4f && viewFlat.sqrMagnitude > 1e-4f)
                lookLean *= Mathf.Clamp01(Vector3.Dot(bodyFlat.normalized, viewFlat.normalized));

            // Spine world-pitch EVERY frame, standing included: the crouch FORWARD fold (0 when standing) plus
            // the look-lean, composed into ONE world-space rotate about the body's own right AFTER the breathe
            // swing. The instance still holds last frame's placement here, which is the body's facing (at a
            // control, or on a seat), so the fold stays forward whichever way the view is turned.
            // This is the single spine pitch, so the look-lean pivots the whole upper body (Spine_01 to
            // chest/head/arms) on the hips while standing and adds to the crouch fold when crouched.
            // Seated, a slight slouch; lying, the look-lean would fold the torso off the mattress, so it fades out.
            // Swinging round on the seat, the torso leans back over the hips to lift the legs.
            // On the floor, each pose has its own lean: back on the hands, upright, forward over hugged knees.
            float floorLean = 0f;
            for (int i = 0; i < _floorW.Length; i++) floorLean += FloorLean[i] * _floorW[i];
            float down = Mathf.Max(Mathf.Max(_rag01, _getUp01), _swim01);
            float spinePitch = (CrouchTorsoLean * crouch + lookLean) * (1f - Mathf.Max(_lie01, down)) + (6f - 12f * _tuck + floorLean) * _seat01 * (1f - down);
            if (spine && _bSpine != null && Mathf.Abs(spinePitch) > 0.001f)
                _bSpine.Rotate(_instance.transform.right, spinePitch, Space.World);

            // Crouch body drop: lower the whole body so the hips, torso and head come down, relative to the
            // planted base. MUST run BEFORE the leg IK so the IK reads the DROPPED hip joints. At crouch 0 this
            // restores the exact base, so standing is untouched.
            // Interaction placement: step toward and turn to face a control the body is using. Computed from the
            // vanilla player's position (the root), never from the already-moved body, so it cannot chase itself.
            UpdateInteractionPlacement(dt, bodyOffset);
            Quaternion ixYawRot = Quaternion.Euler(0f, _ixYaw, 0f);

            if (_hasBodyBase && bodyOffset)
            {
                float dropM = CrouchDrop * crouch;
                // Solve the TARGET pose (full crouch) and scale it in, rather than re-solving at the current
                // depth every frame - see SolveHipSetback for why that flickered.
                float backM = SolveHipSetback(CrouchDrop) * crouch;
                // Root-local +Z is the body's facing, so subtracting walks the hips BACKWARD, which is what
                // turns a kneel into a squat.
                CrouchOffset = new Vector3(0f, dropM, backM);
                // The crouch setback is along the body's facing, so it turns with the interaction yaw.
                _instance.transform.localPosition = _bodyBaseLocalPos + _ixOffset - ixYawRot * CrouchOffset;
                _instance.transform.localRotation = ixYawRot;
                if (_seat01 > 0f) PlaceOnSeat();
                if (_lie01 > 0f) PlaceLying();
                if (_getUp01 > 0f) PlaceGettingUp(ixYawRot);
                if (_swim01 > 0f) PlaceSwimming(ixYawRot);
            }

            // Working a wheel, winch or pump from a seat: the chest turns toward it and then leans out over the
            // knees by as much as the hands are short of reaching. After the placement, so both axes come from
            // this frame's seated facing, and on top of the seated slouch above. The hips, legs and feet do not
            // move, so the seat pose keeps whatever it had. Weighted by the seat blend like every other seated
            // write, so a player who stands up with the control still in hand unfolds over the rise instead of
            // straightening between two frames.
            if (spine && _bSpine != null && _seat01 > 0.01f
                && (Mathf.Abs(_seatTurnDeg) > 0.01f || Mathf.Abs(_seatLeanDeg) > 0.01f))
            {
                float turnDeg = _seatTurnDeg * _seat01, leanDeg = _seatLeanDeg * _seat01;
                if (Mathf.Abs(turnDeg) > 0.01f) _bSpine.Rotate(Vector3.up, turnDeg, Space.World);
                Vector3 leanDir = Quaternion.Euler(0f, turnDeg, 0f) * _instance.transform.forward;
                leanDir.y = 0f;
                // Cross(up, forward) is the body's right, and a positive pitch about the body's right folds it
                // forward (the seated slouch above uses the same sign), so this leans toward leanDir.
                if (Mathf.Abs(leanDeg) > 0.01f && leanDir.sqrMagnitude > 1e-6f)
                    _bSpine.Rotate(Vector3.Cross(Vector3.up, leanDir.normalized), leanDeg, Space.World);
            }

            // Leg IK: after the drop moved the hips down, re-plant both ankles (knees bend FORWARD = squat).
            // CROUCH-WALK: the ankle targets STEP with the gait so the legs actually stride while crouched -
            // each foot swings forward and back (root.forward) and lifts (root.up) on its half of the cycle,
            // alternating left and right, scaled by the walk blend (0 when standing = a planted static squat).
            // Flip CrouchKneeForward to -1 if the knees ever bend backward.
            if (legs && _legIkReady && _getUp01 > 0f && _hasBodyBase && bodyOffset)
            {
                PoseGetUpLegs(ixYawRot);
            }
            else if (legs && _legIkReady && _swim01 > 0f && _hasBodyBase && bodyOffset)
            {
                PoseSwimLegs(ixYawRot);
            }
            else if (legs && _legIkReady && _seat01 > 0f && _hasBodyBase && bodyOffset)
            {
                PoseSeatedLegs(ixYawRot);
            }
            else if (legs && _legIkReady && crouch > 0.001f)
            {
                float kf = BodyTuning.CrouchKneeForward.Value;
                const float StepLen = 0.28f;   // m, foot forward/back travel at full gait
                const float StepLift = 0.12f;  // m, swing-foot lift
                Vector3 bodyFwd = _root.rotation * ixYawRot * Vector3.forward;
                Vector3 stepL = bodyFwd * (StepLen * blend * s)    + _root.up * (StepLift * blend * Mathf.Max(0f, s));
                Vector3 stepR = bodyFwd * (StepLen * blend * sOpp) + _root.up * (StepLift * blend * Mathf.Max(0f, sOpp));
                SolveLegIk(_root, _bUpperLegL, _bLowerLegL, _thighLenL, _shinLenL, _footLocalL, _thighAimLocalL, _shinAimLocalL, kf, stepL, _bFootL, _footRotRootL, _qFootL, crouch, _ixOffset, ixYawRot);
                SolveLegIk(_root, _bUpperLegR, _bLowerLegR, _thighLenR, _shinLenR, _footLocalR, _thighAimLocalR, _shinAimLocalR, kf, stepR, _bFootR, _footRotRootR, _qFootR, crouch, _ixOffset, ixYawRot);
            }
            else if (legs && _legIkReady)
            {
                // STANDING/WALKING: guarantee the ankle is back at bind. The crouch block above is gated off
                // below 0.001 crouch and nothing else here writes the foot, so without this the last crouched
                // frame's ankle rotation would be frozen into the standing and walking pose for the session.
                if (_bFootL != null) _bFootL.localRotation = _qFootL;
                if (_bFootR != null) _bFootR.localRotation = _qFootR;
            }

            // Lying propped on a pillow: the torso lifted off the mattress a little. After placement, because it
            // turns about the body's own right axis, which placement just laid down.
            if (spine && _bSpine != null && _lie01 > 0f)
                _bSpine.Rotate(_instance.transform.right, 18f * _lie01, Space.World);

            // A ragdoll poses every bone it has, after everything above, so only the arms' reach for the head is left.
            if (_rag01 > 0f) PoseFromRagdoll(_rag01);

            // Swimming flat, the body lies along the swim, so looking around is the head's job. Before the arms, so an
            // item brought to the mouth goes to the lips of the head as it is drawn. The head turns in place and is no
            // parent of the arms, so nothing else the arms read moves with it.
            if (_swim01 > 0.01f && spine) LookHead(ixYawRot, _swim01 * _swimFlat);

            // Last of our own writes, so the arm reaches from where the crouch and look-lean left the
            // shoulder. Claimants run after this, from Tick.
            DriveArms(dt, armL, armR);

            // Getting up starts exactly where the ragdoll lay and moves off it over the first moments.
            if (_getUp01 > 0f && _ragHasDrawn)
            {
                float w = (1f - Smooth01(_getUpShown / 0.18f)) * _getUp01;
                if (w > 0.001f) PoseFromRagdoll(w);
            }
        }

        private static float Smooth01(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        /// <summary>
        /// Follow the seat's facing and hips. Small changes (the boat turning under the seat) are taken at once; a
        /// big one (legs swung over, a spar turned on) sets a goal the body turns to over about half a second, with
        /// the hips sliding across from where they were. A half turn goes back the way the last one came, the same
        /// rule Seating turns the first-person view by, so pressing the key again swings the legs back over the
        /// same side and pressing it mid-swing reverses it.
        /// </summary>
        private void TrackSeatTurn(float dt)
        {
            Vector3 f = _seatForward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) return;
            float raw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            Vector3 hipsRel = _seatHips - _root.position;

            if (!_seatTracking || _seat01 < 0.02f)
            {
                _seatYaw = _seatYawGoal = _seatRawYaw = raw;
                _seatYawVel = 0f;
                _seatNextSign = 1f;
                _tuck = 0f;
                _straddle01 = _seatPose == SeatPose.Straddle ? 1f : 0f;
                int pose = FloorIndex(_seatPose);
                for (int i = 0; i < _floorW.Length; i++) _floorW[i] = i == pose ? 1f : 0f;
                _seatHipsOffset = Vector3.zero;
                _seatTracking = true;
            }
            else
            {
                float d = Mathf.DeltaAngle(_seatRawYaw, raw);
                if (Mathf.Abs(d) < 30f)
                {
                    _seatYaw += d;
                    _seatYawGoal += d;
                }
                else
                {
                    if (Mathf.Abs(d) > 150f)
                    {
                        float round = Mathf.Repeat(d, 360f);
                        d = _seatNextSign > 0f ? round : round - 360f;
                        _seatNextSign = -_seatNextSign;
                    }
                    _seatYawGoal += d;
                    _seatHipsOffset += _lastSeatHipsRel - hipsRel;
                }
                _seatRawYaw = raw;
            }
            _lastSeatHipsRel = hipsRel;

            _seatYaw = Mathf.SmoothDamp(_seatYaw, _seatYawGoal, ref _seatYawVel, 0.2f, 900f, dt);
            if (Mathf.Abs(_seatYawGoal) > 720f)
            {
                float wrap = Mathf.Sign(_seatYawGoal) * 360f;
                _seatYaw -= wrap;
                _seatYawGoal -= wrap;
            }
            float turning = Mathf.Abs(_seatYawGoal - _seatYaw);
            _tuck = Mathf.Lerp(_tuck, turning > 12f ? 1f : 0f, 1f - Mathf.Exp(-12f * dt));
            _straddle01 = Mathf.Lerp(_straddle01, _seatPose == SeatPose.Straddle ? 1f : 0f, 1f - Mathf.Exp(-8f * dt));
            _seatHipsOffset = Vector3.Lerp(_seatHipsOffset, Vector3.zero, 1f - Mathf.Exp(-8f * dt));
            int floorPose = FloorIndex(_seatPose);
            float ease = 1f - Mathf.Exp(-6f * dt);
            for (int i = 0; i < _floorW.Length; i++) _floorW[i] = Mathf.Lerp(_floorW[i], i == floorPose ? 1f : 0f, ease);
        }

        /// <summary>0 to 3 for the floor poses in <see cref="_floorW"/> order, -1 for any other way of sitting.</summary>
        private static int FloorIndex(SeatPose pose)
        {
            int i = (int)pose - (int)SeatPose.FloorLegsOut;
            return i >= 0 && i < 4 ? i : -1;
        }

        private float FloorWeight()
        {
            float w = 0f;
            for (int i = 0; i < _floorW.Length; i++) w += _floorW[i];
            return w;
        }

        /// <summary>
        /// One leg's ankle target on the floor for one floor pose, the direction its knee points, and how far the
        /// foot keeps level with the floor (1) rather than following the shin (0).
        /// </summary>
        private static void FloorLeg(int pose, bool rightLeg, Vector3 hip, Vector3 fwd, Vector3 outward, float a, float b,
            out Vector3 ankle, out Vector3 pole, out float level)
        {
            switch (pose)
            {
                case 0:   // legs out in front, a little apart
                    ankle = hip + fwd * ((a + b) * 0.92f) + outward * 0.06f;
                    pole = Vector3.up;
                    level = 0.15f;
                    break;
                case 1:   // cross-legged: each foot tucked under the other knee, one in front of the other, knees out
                    ankle = hip + fwd * (a * (rightLeg ? 0.45f : 0.62f)) - outward * 0.15f;
                    pole = (outward * 0.9f + Vector3.up * 0.2f + fwd * 0.2f).normalized;
                    level = 0f;
                    break;
                case 2:   // right knee up with the foot planted near the hips, left leg out and bent to its side
                    if (rightLeg)
                    {
                        ankle = hip + fwd * (a * 0.7f) + outward * 0.03f;
                        pole = (Vector3.up + fwd * 0.3f).normalized;
                        level = 1f;
                    }
                    else
                    {
                        ankle = hip + fwd * ((a + b) * 0.78f) + outward * 0.14f;
                        pole = (Vector3.up * 0.7f + outward * 0.7f).normalized;
                        level = 0.3f;
                    }
                    break;
                default:  // both knees drawn up to the chest, feet flat near the hips
                    ankle = hip + fwd * (a * 0.6f) + outward * 0.05f;
                    pole = (Vector3.up + fwd * 0.2f).normalized;
                    level = 1f;
                    break;
            }
        }

        /// <summary>The seated facing, as drawn (mid-turn included), as a yaw inside the root, which is yaw-only.</summary>
        private Quaternion SeatYaw()
        {
            Vector3 f = _seatTracking ? Quaternion.Euler(0f, _seatYaw, 0f) * Vector3.forward : _seatForward;
            f = _root.InverseTransformDirection(f);
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) return Quaternion.identity;
            return Quaternion.Euler(0f, Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg, 0f);
        }

        /// <summary>The seated hip joints as drawn: sliding over to a new seat point, and lifted a little while turning.</summary>
        private Vector3 SeatHipsDrawn()
        {
            return _seatHips + _seatHipsOffset + Vector3.up * (0.03f * _tuck);
        }

        /// <summary>The shoulder joints over the hip joints at the planted rest pose: the torso a seated lean pivots on.</summary>
        private float ShoulderAboveHips()
        {
            return _shoulderMidRoot.y - _hipMidRoot.y;
        }

        /// <summary>The seated shoulders as drawn: over the seat's hips by the torso's own height, and a little
        /// forward, which is where the seated slouch carries them.</summary>
        private Vector3 SeatedShoulderMid()
        {
            return SeatedShoulderMid(SeatHipsDrawn(), (_root.rotation * SeatYaw()) * Vector3.forward);
        }

        /// <summary>The same, for a seat handed in rather than the one this body is drawing.</summary>
        private Vector3 SeatedShoulderMid(Vector3 hipsWorld, Vector3 forwardWorld)
        {
            Vector3 f = forwardWorld;
            f.y = 0f;
            f = f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.zero;
            return hipsWorld + Vector3.up * ShoulderAboveHips() + f * SeatedShoulderForward;
        }

        /// <summary>
        /// How far this body would have to reach to work a wheel, winch or bilge pump from a seat with its hips at
        /// <paramref name="hipsWorld"/> facing <paramref name="forwardWorld"/>, how far it can reach sitting there,
        /// and how far round from the seat's facing the control lies. False when there is no fitted body to measure
        /// or the control is not one this mod poses, and then the caller must fall back to standing up.
        /// </summary>
        public bool TryMeasureSeatedReach(Transform control, Vector3 hipsWorld, Vector3 forwardWorld,
            out float need, out float limit, out float offFacingDeg)
        {
            need = 0f;
            limit = 0f;
            offFacingDeg = 0f;
            // _legIkReady is what says the hip joints were captured. Without them the torso height below is the
            // shoulders' height over the root, which would read every control on the boat as within reach.
            if (!RigReady || !_hasBodyBase || !_legIkReady || _root == null) return false;
            ArmIk arm = _armR.Ready ? _armR : _armL;
            if (!arm.Ready) return false;

            Vector3 flat = forwardWorld;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) return false;
            flat.Normalize();
            Vector3 shoulderMid = SeatedShoulderMid(hipsWorld, flat);
            Vector3 aim;
            if (!RotorGrip.MeasureSeated(control, shoulderMid, Vector3.Cross(Vector3.up, flat), _shoulderHalf, out need, out aim))
                return false;

            // The arms reach to the palm and no farther (the IK clamps there), and the lean adds exactly the
            // shoulder travel the seated lean provides, so the limit promises nothing the body does not do.
            limit = (arm.Length + arm.PalmReach) * InteractionTuning.SeatedReach.Value
                    + ShoulderAboveHips() * Mathf.Sin(InteractionTuning.SeatedLeanDegrees.Value * Mathf.Deg2Rad);
            Vector3 toAim = aim - shoulderMid;
            toAim.y = 0f;
            if (toAim.sqrMagnitude > 1e-6f) offFacingDeg = Vector3.Angle(flat, toAim);
            return true;
        }

        /// <summary>Move the body so its hip joints are on the seat, facing the way the seat faces, eased.</summary>
        private void PlaceOnSeat()
        {
            Quaternion yaw = SeatYaw();
            Vector3 hips = _root.InverseTransformPoint(SeatHipsDrawn());
            Vector3 pos = hips - yaw * (_hipMidRoot - _bodyBaseLocalPos);
            var t = _instance.transform;
            t.localPosition = Vector3.Lerp(t.localPosition, pos, _seat01);
            t.localRotation = Quaternion.Slerp(t.localRotation, yaw, _seat01);
        }

        /// <summary>Lay the body on its back with the head at the given point and the feet along the bed, eased.</summary>
        private void PlaceLying()
        {
            Vector3 along = _lieAlong.sqrMagnitude > 1e-6f ? _lieAlong.normalized : _root.forward;
            Vector3 up = Vector3.ProjectOnPlane(_lieUp, along);
            if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
            up.Normalize();
            // Body forward (the chest) to the sky, body up (toward the head) back along the bed.
            Quaternion rot = Quaternion.LookRotation(up, -along);
            // The body's origin is at the soles, a head's height from the head along the bed.
            Vector3 soles = _lieHead + along * _headAboveSoles;
            var t = _instance.transform;
            t.position = Vector3.Lerp(t.position, soles, _lie01);
            t.rotation = Quaternion.Slerp(t.rotation, rot, _lie01);
        }

        // ---- swimming -------------------------------------------------------------------------------------

        private void TrackSwim(float dt)
        {
            float speed = _swimVel.magnitude;
            float move = Mathf.Clamp01((speed - 0.3f) / 1.2f);
            _swimMove01 = Mathf.Lerp(_swimMove01, move, 1f - Mathf.Exp(-3f * dt));
            // One stroke every second or so, quicker the faster the swim.
            float rate = Mathf.Lerp(0.55f, 0.7f + 0.3f * Mathf.Clamp01(speed / 2.5f), _swimMove01);
            _swimPhase = Mathf.Repeat(_swimPhase + dt * rate * 2f * Mathf.PI, 2f * Mathf.PI);

            Vector3 yawFwd = _root.forward;
            float along = speed > 0.3f ? Vector3.Dot(_swimVel / speed, yawFwd) : 1f;
            _swimBack = Mathf.Lerp(_swimBack, along < -0.25f ? 1f : 0f, 1f - Mathf.Exp(-3f * dt));
            // Flat out in the water: always under the surface, and at the surface once there is way on.
            float flat = _swimUnder ? Mathf.Max(_swimMove01, 0.75f) : _swimMove01;
            _swimFlat = Mathf.Lerp(_swimFlat, flat, 1f - Mathf.Exp(-3f * dt));
        }

        /// <summary>
        /// Lay the body along the way it is going: head first, or feet first and chest to the sky when that way is
        /// backward, climbing or diving with the swim. Upright and facing the player's way when treading water at the
        /// surface. Turned about the shoulders, so the head stays where the view is, with a gentle bob.
        /// </summary>
        private void PlaceSwimming(Quaternion ixYawRot)
        {
            Quaternion yaw = _root.rotation * ixYawRot;
            Vector3 yawFwd = yaw * Vector3.forward;
            float speed = _swimVel.magnitude;
            Vector3 moveDir = speed > 0.3f ? _swimVel / speed : yawFwd;

            Quaternion rotSwim = Quaternion.Slerp(SwimAlong(moveDir, Vector3.down, yawFwd), SwimAlong(-moveDir, Vector3.up, yawFwd), _swimBack);
            Quaternion rotTread = Quaternion.LookRotation(yawFwd, Vector3.up);
            Quaternion rot = Quaternion.Slerp(rotTread, rotSwim, _swimFlat);

            Vector3 shoulderOffset = _shoulderMidRoot - _bodyBaseLocalPos;
            Vector3 pivot = _root.TransformPoint(_bodyBaseLocalPos + _ixOffset + ixYawRot * shoulderOffset)
                            + Vector3.up * (0.03f * Mathf.Sin(_swimPhase));
            Vector3 pos = pivot - rot * shoulderOffset;
            var t = _instance.transform;
            t.position = Vector3.Lerp(t.position, pos, _swim01);
            t.rotation = Quaternion.Slerp(t.rotation, rot, _swim01);
        }

        /// <summary>A body lying with its head along <paramref name="headDir"/> and its chest toward <paramref name="chestDir"/>.</summary>
        private static Quaternion SwimAlong(Vector3 headDir, Vector3 chestDir, Vector3 fallbackChest)
        {
            if (headDir.sqrMagnitude < 1e-6f) headDir = Vector3.up;
            headDir.Normalize();
            Vector3 chest = Vector3.ProjectOnPlane(chestDir, headDir);
            if (chest.sqrMagnitude < 1e-4f) chest = Vector3.ProjectOnPlane(fallbackChest, headDir);
            if (chest.sqrMagnitude < 1e-4f) chest = Vector3.ProjectOnPlane(Vector3.forward, headDir);
            if (chest.sqrMagnitude < 1e-6f) return Quaternion.LookRotation(Vector3.forward, headDir);
            return Quaternion.LookRotation(chest.normalized, headDir);
        }

        /// <summary>Where a breaststroke is in its cycle, and which way round it goes (backward, it runs in reverse).</summary>
        private float SwimStroke(float offset)
        {
            float t = Mathf.Repeat(_swimPhase / (2f * Mathf.PI) + offset, 1f);
            return _swimBack > 0.5f ? 1f - t : t;
        }

        private void PoseSwimLegs(Quaternion ixYawRot)
        {
            Transform body = _instance.transform;
            Vector3 up = body.up, fwd = body.forward, right = body.right;
            float kf = BodyTuning.CrouchKneeForward.Value;
            // The kick comes a moment after the arms, the way a breaststroke goes.
            float t = SwimStroke(-0.25f);
            SwimLeg(_bUpperLegL, _bLowerLegL, _thighLenL, _shinLenL, _footLocalL, _thighAimLocalL, _shinAimLocalL, kf, _bFootL, _footRotRootL, _qFootL, ixYawRot, up, fwd, -right, t);
            SwimLeg(_bUpperLegR, _bLowerLegR, _thighLenR, _shinLenR, _footLocalR, _thighAimLocalR, _shinAimLocalR, kf, _bFootR, _footRotRootR, _qFootR, ixYawRot, up, fwd, right, t);
        }

        /// <summary>
        /// One leg through a frog kick: trailing straight while the body glides, heels drawn up with the knees out, then
        /// swept out and back together. Treading water, the same shapes at a slower, smaller pedal.
        /// </summary>
        private void SwimLeg(Transform hip, Transform knee, float a, float b, Vector3 footLocal, Vector3 thighAim, Vector3 shinAim, float kf,
            Transform foot, Quaternion footRotRoot, Quaternion footBind, Quaternion ixYawRot, Vector3 up, Vector3 fwd, Vector3 outward, float t)
        {
            if (hip == null || knee == null) return;
            Vector3 h = hip.position;
            float leg = a + b;
            float trail, wide, forward;
            if (t < 0.45f)                       // gliding, legs together behind
            {
                float k = Smooth01(t / 0.45f);
                trail = Mathf.Lerp(0.96f, 0.94f, k); wide = Mathf.Lerp(0.07f, 0.06f, k); forward = 0f;
            }
            else if (t < 0.72f)                  // heels drawn up, knees out
            {
                float k = Smooth01((t - 0.45f) / 0.27f);
                trail = Mathf.Lerp(0.94f, 0.45f, k); wide = Mathf.Lerp(0.06f, 0.3f, k); forward = Mathf.Lerp(0f, 0.12f, k);
            }
            else                                 // swept out and back together
            {
                float k = Smooth01((t - 0.72f) / 0.28f);
                trail = Mathf.Lerp(0.45f, 0.96f, k); wide = Mathf.Lerp(0.3f, 0.07f, Mathf.Max(0f, k * 2f - 1f)) + 0.18f * Mathf.Sin(k * Mathf.PI); forward = Mathf.Lerp(0.12f, 0f, k);
            }
            // Treading water is the same kick, smaller and more upright.
            float size = Mathf.Lerp(0.55f, 1f, _swimMove01);
            Vector3 target = h - up * (leg * Mathf.Lerp(0.8f, trail, size)) + outward * (wide * size * leg * 0.55f) + fwd * (forward * size * leg * 0.5f);
            Vector3 standF = _root.TransformPoint(_ixOffset + ixYawRot * footLocal);
            Vector3 F = Vector3.Lerp(standF, target, _swim01);
            // Knees out to the sides and toward the chest, never folding back through the body.
            Vector3 pole = Vector3.Lerp(_root.rotation * ixYawRot * Vector3.forward * kf, (fwd * 0.6f + outward).normalized, _swim01);
            SolveLegIk(_root, hip, knee, a, b, _root.InverseTransformPoint(F), thighAim, shinAim, kf, Vector3.zero, foot, footRotRoot, footBind,
                0f, Vector3.zero, Quaternion.identity, pole);
        }

        /// <summary>
        /// One hand through a breaststroke: reaching out ahead of the head, sweeping wide and back, tucking in under the
        /// chin, then pushing forward again. Backward, the same path in reverse, which is what sculling backward looks
        /// like. Treading water, the same shapes, smaller and in front of the chest.
        /// </summary>
        private HandGrip SwimHand(Vector3 shoulder, Vector3 up, Vector3 fwd, Vector3 outward, float t)
        {
            float ahead, wide, under;
            if (t < 0.3f)                        // reaching, hands together out in front
            {
                float k = Smooth01(t / 0.3f);
                ahead = Mathf.Lerp(0.48f, 0.44f, k); wide = Mathf.Lerp(0.1f, 0.16f, k); under = Mathf.Lerp(0.04f, 0.08f, k);
            }
            else if (t < 0.6f)                   // sweeping out and back
            {
                float k = Smooth01((t - 0.3f) / 0.3f);
                ahead = Mathf.Lerp(0.44f, 0.12f, k); wide = Mathf.Lerp(0.16f, 0.42f, k); under = Mathf.Lerp(0.08f, 0.2f, k);
            }
            else if (t < 0.78f)                  // tucked in under the chin
            {
                float k = Smooth01((t - 0.6f) / 0.18f);
                ahead = Mathf.Lerp(0.12f, 0.16f, k); wide = Mathf.Lerp(0.42f, 0.11f, k); under = Mathf.Lerp(0.2f, 0.22f, k);
            }
            else                                 // pushed forward again
            {
                float k = Smooth01((t - 0.78f) / 0.22f);
                ahead = Mathf.Lerp(0.16f, 0.48f, k); wide = Mathf.Lerp(0.11f, 0.1f, k); under = Mathf.Lerp(0.22f, 0.04f, k);
            }
            float size = Mathf.Lerp(0.6f, 1f, _swimMove01);
            Vector3 palm = shoulder + up * (ahead * size) + outward * (wide * size) + fwd * (under * size);
            Vector3 finger = palm - shoulder;
            return new HandGrip
            {
                On = true,
                Palm = palm,
                Normal = -fwd,
                Finger = finger.sqrMagnitude > 1e-6f ? finger.normalized : up,
            };
        }

        /// <summary>
        /// Turn the head toward what the player is looking at, up to most of a turn away from where the face already
        /// points. Swimming flat, the body lies along the swim and the head is what looks around.
        /// </summary>
        private void LookHead(Quaternion ixYawRot, float weight)
        {
            if (_bHead == null || weight < 0.01f) return;
            Quaternion yaw = _root.rotation * ixYawRot;
            Vector3 view = yaw * Quaternion.Euler(-_lookPitch, 0f, 0f) * Vector3.forward;
            Vector3 face = _bHead.rotation * _headFwdLocal;
            if (face.sqrMagnitude < 1e-6f || view.sqrMagnitude < 1e-6f) return;
            Vector3 want = Vector3.RotateTowards(face.normalized, view.normalized, 70f * Mathf.Deg2Rad, 0f);
            _bHead.rotation = Quaternion.FromToRotation(face.normalized, Vector3.Slerp(face.normalized, want, weight)) * _bHead.rotation;
        }

        // ---- knocked down --------------------------------------------------------------------------------

        /// <summary>
        /// A ragdoll or a get-up writes the hips bone's place and turn and the head's turn directly, and nothing in the
        /// ordinary pose puts them back, so they return to their rest pose at the start of every frame.
        /// </summary>
        private void ResetRagdollBones()
        {
            if (_bPelvis != null)
            {
                _bPelvis.localPosition = _pelvisBindPos;
                _bPelvis.localRotation = _qPelvis;
            }
            if (_bHead != null) _bHead.localRotation = _qHead;
        }

        /// <summary>
        /// Every ragdoll bone to the drawn ragdoll pose, <paramref name="weight"/> of the way from where it is posed now.
        /// Each bone's turn is read before any is written, since turning a parent turns its children.
        /// </summary>
        private void PoseFromRagdoll(float weight)
        {
            for (int i = 0; i < RagdollParts; i++)
            {
                var b = RagdollBone((RagdollPart)i);
                _ragBefore[i] = b != null ? b.rotation : Quaternion.identity;
            }
            if (_bPelvis != null) _bPelvis.position = Vector3.Lerp(_bPelvis.position, _ragPelvisDrawn, weight);
            for (int i = 0; i < RagdollParts; i++)
            {
                var b = RagdollBone((RagdollPart)i);
                if (b != null) b.rotation = weight >= 0.999f ? _ragDrawn[i] : Quaternion.Slerp(_ragBefore[i], _ragDrawn[i], weight);
            }
        }

        /// <summary>
        /// Note how the body lies as getting up starts, from the ragdoll pose still on the bones: where the hips are,
        /// which way the head lies along the floor, and whether the chest faces down.
        /// </summary>
        private void BeginGetUp()
        {
            _getUpBegun = true;
            _getUpShown = 0f;
            if (!_ragHasDrawn || _bHead == null || _bSpine == null || !_legIkReady)
            {
                // Nothing to get up from (a crewmate first seen part way up): standing already.
                _getUpShown = 1f;
                return;
            }
            Vector3 hips = (_bUpperLegL.position + _bUpperLegR.position) * 0.5f;
            Vector3 along = _bHead.position - hips;
            along.y = 0f;
            _guAlong = along.sqrMagnitude > 1e-4f ? along.normalized : _root.forward;
            _guPelvis = hips;
            _guFaceDown = (_bSpine.rotation * _chestFwdLocal).y < 0f;
            _guFloorY = Mathf.Min(_ragFloorY, hips.y - 0.1f);
        }

        private struct GetUpKey
        {
            public Vector3 Hips, Up, Forward, FootL, FootR, Pole, HandL, HandR;
            public float FootLevel, HandW;
        }

        private static GetUpKey LerpKey(GetUpKey a, GetUpKey b, float t)
        {
            return new GetUpKey
            {
                Hips = Vector3.Lerp(a.Hips, b.Hips, t),
                Up = Vector3.Slerp(a.Up, b.Up, t),
                Forward = Vector3.Slerp(a.Forward, b.Forward, t),
                FootL = Vector3.Lerp(a.FootL, b.FootL, t),
                FootR = Vector3.Lerp(a.FootR, b.FootR, t),
                Pole = Vector3.Slerp(a.Pole, b.Pole, t),
                HandL = Vector3.Lerp(a.HandL, b.HandL, t),
                HandR = Vector3.Lerp(a.HandR, b.HandR, t),
                FootLevel = Mathf.Lerp(a.FootLevel, b.FootLevel, t),
                HandW = Mathf.Lerp(a.HandW, b.HandW, t),
            };
        }

        private static Vector3 OnFloor(Vector3 p, float y)
        {
            p.y = y;
            return p;
        }

        /// <summary>
        /// The get-up pose at the current progress: four key poses eased between. Face down: flat, onto hands and knees,
        /// back into a squat, standing. Face up: flat, sitting up with the knees drawn in and the hands behind, forward
        /// into a squat, standing. The standing pose is the ordinary one where the root is now, so getting up ends
        /// exactly on it.
        /// </summary>
        private GetUpKey GetUpPose(Quaternion ixYawRot)
        {
            float kf = BodyTuning.CrouchKneeForward.Value;
            Quaternion standRot = _root.rotation * ixYawRot;
            var stand = new GetUpKey
            {
                Hips = _root.TransformPoint(_bodyBaseLocalPos + _ixOffset + ixYawRot * (_hipMidRoot - _bodyBaseLocalPos)),
                Up = Vector3.up,
                Forward = standRot * Vector3.forward,
                FootL = _root.TransformPoint(_ixOffset + ixYawRot * _footLocalL),
                FootR = _root.TransformPoint(_ixOffset + ixYawRot * _footLocalR),
                Pole = standRot * Vector3.forward * kf,
                FootLevel = 1f,
                HandW = 0f,
            };
            stand.HandL = stand.HandR = stand.Hips;
            if (_getUpShown >= 0.999f) return stand;

            Vector3 up = Vector3.up, along = _guAlong;
            Vector3 side = Vector3.Cross(Vector3.up, along).normalized;
            Vector3 g = new Vector3(_guPelvis.x, _guFloorY, _guPelvis.z);
            float floor = _guFloorY;
            float leg = _thighLenL + _shinLenL;
            float ankle = Mathf.Max(0.06f, _footLocalL.y - FittedFeetLocalY);

            GetUpKey k0, k1, k2;
            float a, b;
            if (_guFaceDown)
            {
                // Lying face down with the head along the floor, the body's right is +side.
                Vector3 h0 = g + up * 0.14f;
                k0 = new GetUpKey
                {
                    Hips = h0, Up = along, Forward = Vector3.down,
                    FootL = OnFloor(h0 - along * (leg * 0.95f) - side * 0.1f, floor + ankle),
                    FootR = OnFloor(h0 - along * (leg * 0.95f) + side * 0.1f, floor + ankle),
                    Pole = Vector3.down,
                    HandL = OnFloor(h0 + along * 0.55f - side * 0.28f, floor + 0.03f),
                    HandR = OnFloor(h0 + along * 0.55f + side * 0.28f, floor + 0.03f),
                    FootLevel = 0f, HandW = 1f,
                };
                Vector3 h1 = g - along * 0.2f + up * 0.45f;
                Vector3 up1 = (along + up * 0.35f).normalized;
                Vector3 shoulders1 = h1 + up1 * 0.5f;
                k1 = new GetUpKey
                {
                    Hips = h1, Up = up1, Forward = Vector3.Cross(side, up1),
                    FootL = OnFloor(g - along * (0.2f + _shinLenL * 0.95f) - side * 0.12f, floor + 0.06f),
                    FootR = OnFloor(g - along * (0.2f + _shinLenL * 0.95f) + side * 0.12f, floor + 0.06f),
                    Pole = Vector3.down,
                    HandL = OnFloor(shoulders1 - side * 0.2f, floor + 0.03f),
                    HandR = OnFloor(shoulders1 + side * 0.2f, floor + 0.03f),
                    FootLevel = 0f, HandW = 1f,
                };
                Vector3 h2 = g - along * 0.35f + up * 0.55f;
                Vector3 up2 = (up * 0.75f + along * 0.65f).normalized;
                k2 = new GetUpKey
                {
                    Hips = h2, Up = up2, Forward = Vector3.Cross(side, up2),
                    FootL = OnFloor(g - along * 0.25f - side * 0.13f, floor + ankle),
                    FootR = OnFloor(g - along * 0.25f + side * 0.13f, floor + ankle),
                    Pole = along,
                    HandL = h2 + along * (_thighLenL * 0.8f) - side * 0.15f + up * 0.05f,
                    HandR = h2 + along * (_thighLenL * 0.8f) + side * 0.15f + up * 0.05f,
                    FootLevel = 1f, HandW = 0.7f,
                };
                a = 0.32f; b = 0.64f;
            }
            else
            {
                // Lying face up with the head along the floor, the body's right is -side.
                Vector3 h0 = g + up * 0.13f;
                k0 = new GetUpKey
                {
                    Hips = h0, Up = along, Forward = up,
                    FootL = OnFloor(h0 - along * (leg * 0.95f) + side * 0.1f, floor + ankle),
                    FootR = OnFloor(h0 - along * (leg * 0.95f) - side * 0.1f, floor + ankle),
                    Pole = up,
                    HandL = OnFloor(h0 + along * 0.05f + side * 0.3f, floor + 0.03f),
                    HandR = OnFloor(h0 + along * 0.05f - side * 0.3f, floor + 0.03f),
                    FootLevel = 0f, HandW = 0.6f,
                };
                Vector3 h1 = g + up * 0.12f - along * 0.08f;
                Vector3 up1 = (up + along * 0.35f).normalized;
                Vector3 feetL = OnFloor(g - along * 0.5f + side * 0.12f, floor + ankle);
                Vector3 feetR = OnFloor(g - along * 0.5f - side * 0.12f, floor + ankle);
                k1 = new GetUpKey
                {
                    Hips = h1, Up = up1, Forward = Vector3.ProjectOnPlane(-along, up1).normalized,
                    FootL = feetL, FootR = feetR,
                    Pole = up,
                    HandL = OnFloor(g + along * 0.22f + side * 0.25f, floor + 0.03f),
                    HandR = OnFloor(g + along * 0.22f - side * 0.25f, floor + 0.03f),
                    FootLevel = 1f, HandW = 1f,
                };
                Vector3 h2 = g - along * 0.38f + up * 0.5f;
                Vector3 up2 = (up - along * 0.55f).normalized;
                k2 = new GetUpKey
                {
                    Hips = h2, Up = up2, Forward = Vector3.ProjectOnPlane(-along, up2).normalized,
                    FootL = feetL, FootR = feetR,
                    Pole = -along,
                    HandL = h2 - along * (_thighLenL * 0.8f) + side * 0.15f + up * 0.05f,
                    HandR = h2 - along * (_thighLenL * 0.8f) - side * 0.15f + up * 0.05f,
                    FootLevel = 1f, HandW = 0.6f,
                };
                a = 0.34f; b = 0.68f;
            }

            float t = _getUpShown;
            if (t < a) return LerpKey(k0, k1, Smooth01(t / a));
            if (t < b) return LerpKey(k1, k2, Smooth01((t - a) / (b - a)));
            return LerpKey(k2, stand, Smooth01((t - b) / (1f - b)));
        }

        /// <summary>Place the body for this moment of getting up, and note where its feet and hands go.</summary>
        private void PlaceGettingUp(Quaternion ixYawRot)
        {
            var k = GetUpPose(ixYawRot);
            _guFootL = k.FootL;
            _guFootR = k.FootR;
            _guPole = k.Pole;
            _guHandL = k.HandL;
            _guHandR = k.HandR;
            _guFootLevel = k.FootLevel;
            _guHandW = k.HandW;
            Vector3 up = k.Up.sqrMagnitude > 1e-6f ? k.Up.normalized : Vector3.up;
            Vector3 fwd = Vector3.ProjectOnPlane(k.Forward, up);
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(_root.forward, up);
            Quaternion rot = Quaternion.LookRotation(fwd.normalized, up);
            Vector3 pos = k.Hips - rot * (_hipMidRoot - _bodyBaseLocalPos);
            var t = _instance.transform;
            t.position = Vector3.Lerp(t.position, pos, _getUp01);
            t.rotation = Quaternion.Slerp(t.rotation, rot, _getUp01);
        }

        private void PoseGetUpLegs(Quaternion ixYawRot)
        {
            float kf = BodyTuning.CrouchKneeForward.Value;
            GetUpLeg(_bUpperLegL, _bLowerLegL, _thighLenL, _shinLenL, _footLocalL, _thighAimLocalL, _shinAimLocalL, kf, _bFootL, _footRotRootL, _qFootL, ixYawRot, _guFootL);
            GetUpLeg(_bUpperLegR, _bLowerLegR, _thighLenR, _shinLenR, _footLocalR, _thighAimLocalR, _shinAimLocalR, kf, _bFootR, _footRotRootR, _qFootR, ixYawRot, _guFootR);
        }

        private void GetUpLeg(Transform hip, Transform knee, float a, float b, Vector3 footLocal, Vector3 thighAim, Vector3 shinAim, float kf,
            Transform foot, Quaternion footRotRoot, Quaternion footBind, Quaternion ixYawRot, Vector3 target)
        {
            if (hip == null || knee == null) return;
            Vector3 standF = _root.TransformPoint(_ixOffset + ixYawRot * footLocal);
            Vector3 F = Vector3.Lerp(standF, target, _getUp01);
            SolveLegIk(_root, hip, knee, a, b, _root.InverseTransformPoint(F), thighAim, shinAim, kf, Vector3.zero, foot, footRotRoot, footBind,
                _guFootLevel, Vector3.zero, Quaternion.identity, _guPole);
        }

        /// <summary>
        /// What the arms do while down, from the head and chest as the ragdoll has them: both palms over the face, both
        /// hands over the top of the head with the elbows in front, or flung out wide. Never below the floor.
        /// </summary>
        private void ReactionHands(float dt, ref HandGrip gr, ref HandGrip gl, ref Vector3 poleR, ref Vector3 poleL)
        {
            float ease = 1f - Mathf.Exp(-8f * dt);
            _coverFace01 = Mathf.Lerp(_coverFace01, _ragReaction == FallReaction.CoverFace ? 1f : 0f, ease);
            _coverHead01 = Mathf.Lerp(_coverHead01, _ragReaction == FallReaction.CoverHead ? 1f : 0f, ease);
            _flail01 = Mathf.Lerp(_flail01, _ragReaction == FallReaction.Flail ? 1f : 0f, ease);
            if (_bHead == null || _bSpine == null) return;

            Quaternion hr = _bHead.rotation;
            Vector3 head = _bHead.position;
            Vector3 hUp = hr * _headUpLocal, hFwd = hr * _headFwdLocal;
            Vector3 hRight = Vector3.Cross(hUp, hFwd).normalized;
            Quaternion cr = _bSpine.rotation;
            Vector3 cUp = cr * _chestUpLocal, cFwd = cr * _chestFwdLocal;
            Vector3 cRight = Vector3.Cross(cUp, cFwd).normalized;
            float floor = _ragFloorY + 0.03f;

            if (_coverFace01 >= _coverHead01 && _coverFace01 >= _flail01 && _coverFace01 > 0.3f)
            {
                gr = new HandGrip { On = true, Palm = head + hFwd * 0.14f + hUp * 0.06f + hRight * 0.04f, Normal = -hFwd, Finger = hUp };
                gl = new HandGrip { On = true, Palm = head + hFwd * 0.14f + hUp * 0.06f - hRight * 0.04f, Normal = -hFwd, Finger = hUp };
                poleR = -hUp + hRight * 0.6f;
                poleL = -hUp - hRight * 0.6f;
            }
            else if (_coverHead01 >= _flail01 && _coverHead01 > 0.3f)
            {
                gr = new HandGrip { On = true, Palm = head + hUp * 0.16f + hRight * 0.07f - hFwd * 0.02f, Normal = -hUp, Finger = -hFwd };
                gl = new HandGrip { On = true, Palm = head + hUp * 0.16f - hRight * 0.07f - hFwd * 0.02f, Normal = -hUp, Finger = -hFwd };
                poleR = hFwd + hRight * 0.5f;
                poleL = hFwd - hRight * 0.5f;
            }
            else if (_flail01 > 0.3f)
            {
                float wave = Mathf.Sin(Time.time * 9f);
                Vector3 sR = _bShoulderR != null ? _bShoulderR.position : head, sL = _bShoulderL != null ? _bShoulderL.position : head;
                gr = new HandGrip { On = true, Palm = sR + cRight * 0.42f + cUp * (0.3f + 0.12f * wave) + cFwd * 0.1f, Normal = cFwd, Finger = cRight };
                gl = new HandGrip { On = true, Palm = sL - cRight * 0.42f + cUp * (0.3f - 0.12f * wave) + cFwd * 0.1f, Normal = cFwd, Finger = -cRight };
                poleR = -cUp;
                poleL = -cUp;
            }
            if (gr.On) gr.Palm.y = Mathf.Max(gr.Palm.y, floor);
            if (gl.On) gl.Palm.y = Mathf.Max(gl.Palm.y, floor);
        }

        /// <summary>
        /// Seated legs: thighs forward from the hips, feet planted on the floor in front, or hanging below the knees
        /// when the floor is out of reach. Solved with the crouch's two-bone IK, blended from the standing targets
        /// so sitting down and standing up move the feet rather than snap them.
        /// </summary>
        private void PoseSeatedLegs(Quaternion ixYawRot)
        {
            Quaternion seatYaw = SeatYaw();
            Quaternion frame = Quaternion.Slerp(ixYawRot, seatYaw, _seat01);
            Vector3 fwd = _root.rotation * seatYaw * Vector3.forward;
            Vector3 right = _root.rotation * seatYaw * Vector3.right;
            float kf = BodyTuning.CrouchKneeForward.Value;
            float swing = 0.03f * Mathf.Sin(Time.time * 0.9f);
            SeatOneLeg(_bUpperLegL, _bLowerLegL, _thighLenL, _shinLenL, _footLocalL, _thighAimLocalL, _shinAimLocalL, kf, _bFootL, _footRotRootL, _qFootL, ixYawRot, frame, fwd, -right, swing, false);
            SeatOneLeg(_bUpperLegR, _bLowerLegR, _thighLenR, _shinLenR, _footLocalR, _thighAimLocalR, _shinAimLocalR, kf, _bFootR, _footRotRootR, _qFootR, ixYawRot, frame, fwd, right, -swing, true);
        }

        private void SeatOneLeg(Transform hip, Transform knee, float a, float b, Vector3 footLocal, Vector3 thighAim, Vector3 shinAim,
            float kf, Transform foot, Quaternion footRotRoot, Quaternion footBind, Quaternion ixYawRot, Quaternion frame, Vector3 fwd, Vector3 outward, float swing,
            bool rightLeg)
        {
            if (hip == null || knee == null) return;
            Vector3 standF = _root.TransformPoint(_ixOffset + ixYawRot * footLocal);
            Vector3 h = hip.position;
            float ankle = footLocal.y - FittedFeetLocalY;            // the ankle over the sole
            float drop = h.y - (_seatFloorY + ankle);                  // hip over the ankle on the floor
            Vector3 seatF;
            bool hanging = _seatPose == SeatPose.Dangle || _seatPose == SeatPose.Straddle;
            // Sitting on the floor itself: the legs go out in front along it.
            bool onFloor = !hanging && drop < a * 0.6f;
            bool dangle = !onFloor && (hanging || drop > b * 1.05f);
            if (onFloor)
            {
                seatF = h + fwd * ((a + b) * 0.93f);
                seatF.y = _seatFloorY + ankle;
            }
            else if (!dangle)
            {
                seatF = h + fwd * (a * 0.95f);
                seatF.y = _seatFloorY + ankle;
            }
            else
            {
                seatF = h + fwd * (a * 0.9f + swing) - Vector3.up * (b * 0.95f);
            }
            bool footLoose = dangle;
            float footLevel = 1f;
            Vector3 pole = Vector3.zero;
            // On the floor: the floor poses, blended by how much of each is showing.
            float floorW = FloorWeight();
            if (floorW > 0.001f)
            {
                Vector3 sumF = Vector3.zero, sumPole = Vector3.zero;
                float sumLevel = 0f;
                for (int i = 0; i < _floorW.Length; i++)
                {
                    if (_floorW[i] < 0.001f) continue;
                    Vector3 t, p; float level;
                    FloorLeg(i, rightLeg, h, fwd, outward, a, b, out t, out p, out level);
                    t.y = _seatFloorY + ankle;
                    sumF += t * _floorW[i];
                    sumPole += p * _floorW[i];
                    sumLevel += level * _floorW[i];
                }
                float k = Mathf.Clamp01(floorW);
                seatF = Vector3.Lerp(seatF, sumF / floorW, k);
                pole = Vector3.Lerp(fwd * kf, sumPole / floorW, k);
                footLevel = Mathf.Lerp(1f, sumLevel / floorW, k);
            }
            // Straddling a spar: each leg down its own side, spread clear of the spar, knees a little forward.
            if (_straddle01 > 0.001f)
            {
                Vector3 straddleF = h + outward * 0.16f + fwd * (a * 0.3f + swing * 0.5f) - Vector3.up * ((a + b) * 0.82f);
                seatF = Vector3.Lerp(seatF, straddleF, _straddle01);
            }
            // Turning on the seat: knees drawn up and feet lifted to hip height, clear of the rail being swung over.
            if (_tuck > 0.001f)
            {
                Vector3 tuckF = h + fwd * (a * 0.55f) + outward * 0.04f + Vector3.up * 0.06f;
                seatF = Vector3.Lerp(seatF, tuckF, _tuck);
            }
            Vector3 F = Vector3.Lerp(standF, seatF, _seat01);
            Vector3 local = Quaternion.Inverse(frame) * _root.InverseTransformPoint(F);
            footLoose |= _straddle01 > 0.5f || _tuck > 0.5f;
            float footBlend = (footLoose ? 0.4f : footLevel) * _seat01;
            if (_seat01 < 1f && pole != Vector3.zero) pole = Vector3.Lerp(fwd * kf, pole, _seat01);
            SolveLegIk(_root, hip, knee, a, b, local, thighAim, shinAim, kf, Vector3.zero, foot, footRotRoot, footBind,
                footBlend, Vector3.zero, frame, pole);
        }

        /// <summary>
        /// The interaction this body is showing this frame. An explicit SetInteraction wins; otherwise a fresh
        /// held item means carrying. Anything stale is None, which lowers the arms.
        /// </summary>
        private InteractionKind EffectiveInteraction()
        {
            bool enabled = InteractionTuning.Enabled != null && InteractionTuning.Enabled.Value;
            if (Time.frameCount - _ixFrame <= 1 && _ixTarget != null && _ixKind != InteractionKind.None)
                return enabled ? _ixKind : InteractionKind.None;
            if (_heldItem != null && Time.frameCount - _heldFrame <= 1)
                return enabled && HeldToolPose.Mode != null && HeldToolPose.Mode.Value != HeldPoseMode.Off ? InteractionKind.Carry : InteractionKind.None;
            return InteractionKind.None;
        }

        private static bool IsCarry(InteractionKind k)
        {
            return k == InteractionKind.Carry || k == InteractionKind.CarryBig;
        }

        /// <summary>
        /// Where the body stands and which way it faces while using a control, eased. Only fixed controls move
        /// the body: the player stands still while operating one, and often clicked it from farther away than an
        /// arm can reach, or from right up against it.
        ///
        /// A wheel or winch places the body from its HUB, never from the handles. The handles go round, and a
        /// body placed from them walks round in a circle with every turn of the crank.
        /// </summary>
        private void UpdateInteractionPlacement(float dt, bool bodyOffset)
        {
            var kind = EffectiveInteraction();
            Vector3 targetOffset = Vector3.zero;
            float targetYaw = 0f;
            // Set where a control places the body: the step and the turn are then eased in the world's orientation.
            bool holdPos = false, holdYaw = false;
            // Set standing at a tiller with the step on: the eased step is then kept clear of the swinging arm.
            bool tillerClear = false;

            bool seated = _seat01 >= 0.5f;
            bool helmOrCrank = (kind == InteractionKind.Helm || kind == InteractionKind.Crank) && _ixTarget != null && _armR.Ready;
            // A tiller is held at the end of its arm with one hand, not worked round like a wheel.
            bool tiller = helmOrCrank && kind == InteractionKind.Helm
                && _tiller.Update(_ixTarget, dt, _root.position, seated, SeatHipsDrawn(), (_root.rotation * SeatYaw()) * Vector3.right,
                    _armR.Length, _root.TransformPoint(_shoulderMidRoot).y - CrouchOffset.y, _shoulderHalf);
            if (!tiller && _tiller.Active) _tiller.Release();
            bool rotor = helmOrCrank && !tiller;
            if (rotor)
            {
                // Seated, the hands are placed from where the seat puts the shoulders, not from the root: while
                // sitting the root is well below the seat, and a crewmate's is the standing spot they sat down
                // from, which can be meters away and puts left and right on the wrong sides.
                rotor = _rotor.Update(_ixTarget, seated ? SeatedShoulderMid() : _root.position,
                    seated ? 0f : Mathf.Max(0f, _shoulderMidRoot.z), _armR.Length, dt);
            }
            else if (_rotor.Active)
            {
                _rotor.Release();
            }

            if (bodyOffset && _armR.Ready)
            {
                float cap = InteractionTuning.StepInMaxMeters.Value;
                // Seated, the seat places the body and nothing here moves it, the way the tiller branch below
                // has always done: the body never slides toward a winch or walks a circle as a crank goes round.
                if (rotor && !seated)
                {
                    Vector3 stand = _root.InverseTransformPoint(_rotor.StandPoint);
                    stand.y = 0f;
                    if (cap > 0f) targetOffset = Vector3.ClampMagnitude(stand, cap);
                    holdPos = cap > 0f;
                    Vector3 face = _root.InverseTransformPoint(_rotor.Hub) - targetOffset;
                    face.y = 0f;
                    if (InteractionTuning.TurnToFace.Value && face.sqrMagnitude > 1e-4f)
                    {
                        targetYaw = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
                        holdYaw = true;
                    }
                }
                else if (tiller && !seated)
                {
                    // Standing at a tiller: beside its end, where TillerGrip keeps the body, facing the way the tiller
                    // points. Sitting, the seat decides where the body is and nothing moves.
                    Vector3 stand = _root.InverseTransformPoint(_tiller.StandPoint);
                    stand.y = 0f;
                    if (cap > 0f)
                    {
                        Vector3 capped = Vector3.ClampMagnitude(stand, cap);
                        // The cap never leaves the swinging tiller inside the body: past it, the body still steps aside.
                        float gap = _tiller.ClearanceGap(_root.TransformPoint(capped));
                        if (gap > 0f)
                        {
                            capped += _root.InverseTransformDirection(_tiller.AwaySide * gap);
                            capped.y = 0f;
                        }
                        targetOffset = capped;
                    }
                    holdPos = cap > 0f;
                    tillerClear = holdPos;
                    Vector3 along = _root.InverseTransformDirection(_tiller.Forward);
                    along.y = 0f;
                    if (InteractionTuning.TurnToFace.Value && along.sqrMagnitude > 1e-4f)
                    {
                        targetYaw = Mathf.Atan2(along.x, along.z) * Mathf.Rad2Deg;
                        holdYaw = true;
                    }
                }
                else if (kind == InteractionKind.Push && _ixTarget != null && !seated)
                {
                    Vector3 l, r;
                    InteractionGeometry.Push(_ixTarget, _root.TransformPoint(_shoulderMidRoot), _root.right, out l, out r);
                    Vector3 m = _root.InverseTransformPoint((l + r) * 0.5f);
                    Vector3 flat = new Vector3(m.x, 0f, m.z);
                    if (InteractionTuning.TurnToFace.Value && flat.sqrMagnitude > 1e-4f)
                    {
                        targetYaw = Mathf.Atan2(m.x, m.z) * Mathf.Rad2Deg;
                        holdYaw = true;
                    }
                    float reach = _armR.Length * InteractionTuning.ReachFraction.Value;
                    float dy = m.y - _shoulderMidRoot.y;
                    float reachFlat = Mathf.Sqrt(Mathf.Max(reach * reach - dy * dy, 0.04f * reach * reach));
                    float need = flat.magnitude - reachFlat;
                    if (need > 0f && cap > 0f && flat.sqrMagnitude > 1e-4f)
                    {
                        targetOffset = flat.normalized * Mathf.Min(need, cap);
                        holdPos = true;
                    }
                }
            }

            // Leaning and turning off the seat toward a control worked from it. Nothing to lean toward otherwise,
            // so the goals are zero and the eases below take the body back upright.
            float leanGoal = 0f, turnGoal = 0f;
            if (rotor && seated && _hasBodyBase) SeatedLeanGoals(out leanGoal, out turnGoal);

            // The root turns with the view. Eased in the root's own frame, a step and turn toward a control would lag
            // behind a quick look round and swing the body off the control, so while a control holds them they are
            // eased in the world's orientation instead. Both roots are yaw-only, and both pairs are kept in step so
            // switching between the two eases never jumps.
            float ease = 1f - Mathf.Exp(-(InteractionTuning.BlendSpeed != null ? InteractionTuning.BlendSpeed.Value : 9f) * dt);
            Quaternion rr = _root.rotation;
            float ry = rr.eulerAngles.y;
            if (!_ixWorldSynced)
            {
                // A body built while already at a control starts from where it stands, not from world yaw 0.
                _ixOffsetWorld = rr * _ixOffset;
                _ixYawWorld = Mathf.Repeat(ry + _ixYaw, 360f);
                _ixWorldSynced = true;
            }
            if (holdPos)
            {
                _ixOffsetWorld = Vector3.Lerp(_ixOffsetWorld, rr * targetOffset, ease);
                _ixOffset = Quaternion.Inverse(rr) * _ixOffsetWorld;
                if (tillerClear) KeepClearOfTiller(rr);
            }
            else
            {
                _ixOffset = Vector3.Lerp(_ixOffset, targetOffset, ease);
                _ixOffsetWorld = rr * _ixOffset;
            }
            if (!tillerClear) _tillerIntrusion = float.PositiveInfinity;
            if (holdYaw)
            {
                _ixYawWorld = Mathf.Repeat(Mathf.LerpAngle(_ixYawWorld, ry + targetYaw, ease), 360f);
                _ixYaw = Mathf.DeltaAngle(ry, _ixYawWorld);
            }
            else
            {
                _ixYaw = Mathf.LerpAngle(_ixYaw, targetYaw, ease);
                _ixYawWorld = Mathf.Repeat(ry + _ixYaw, 360f);
            }
            _seatLeanDeg = Mathf.Lerp(_seatLeanDeg, leanGoal, ease);
            _seatTurnDeg = Mathf.Lerp(_seatTurnDeg, turnGoal, ease);
        }

        /// <summary>
        /// How far the chest turns and then leans toward a control being worked from a seat: round toward it by up
        /// to SeatedTurnDegrees, and over toward it by as much as the hands are short of reaching, capped at
        /// SeatedLeanDegrees. Measured from the same static seated shoulders the seat's own reach test uses, so the
        /// body only ever leans as far as the rule that kept the seat allowed for.
        /// </summary>
        private void SeatedLeanGoals(out float leanDeg, out float turnDeg)
        {
            leanDeg = 0f;
            turnDeg = 0f;
            Vector3 seatFwd = (_root.rotation * SeatYaw()) * Vector3.forward;
            seatFwd.y = 0f;
            if (seatFwd.sqrMagnitude < 1e-6f) return;
            seatFwd.Normalize();

            Vector3 shoulderMid = SeatedShoulderMid(SeatHipsDrawn(), seatFwd);
            float need;
            Vector3 aim;
            if (!RotorGrip.MeasureSeated(_ixTarget, shoulderMid, Vector3.Cross(Vector3.up, seatFwd), _shoulderHalf, out need, out aim))
                return;

            float torso = ShoulderAboveHips();
            if (torso > 0.05f)
            {
                float over = need - (_armR.Length + _armR.PalmReach) * InteractionTuning.SeatedReach.Value;
                float want = Mathf.Clamp(over, 0f, torso * Mathf.Sin(InteractionTuning.SeatedLeanDegrees.Value * Mathf.Deg2Rad));
                leanDeg = Mathf.Asin(Mathf.Clamp01(want / torso)) * Mathf.Rad2Deg;
            }
            Vector3 toAim = aim - shoulderMid;
            toAim.y = 0f;
            if (toAim.sqrMagnitude > 1e-6f)
            {
                float cap = InteractionTuning.SeatedTurnDegrees.Value;
                turnDeg = Mathf.Clamp(Vector3.SignedAngle(seatFwd, toAim.normalized, Vector3.up), -cap, cap);
            }
        }

        /// <summary>
        /// Standing at a tiller, the eased step lags a quick swing (keyboard steering turns it about 50 degrees a second),
        /// so the arm would sweep into the hips on its way to where the body is going. Push the eased body aside so the
        /// tiller never reaches farther into it than on the last frame. Only the ease takes it back out, so a body that
        /// took hold standing too close still eases clear rather than jumping.
        ///
        /// When the hand changes, the reach is measured from the other side and cannot be compared with the last frame's,
        /// so it starts over there too and the ease carries the body across. A crewmate whose position is still sliding in
        /// as they take hold can cross the tiller's centerline; pushed aside instead, the body would jump across the tiller
        /// in one frame.
        /// </summary>
        private void KeepClearOfTiller(Quaternion rr)
        {
            if (_tiller.RightHand != _tillerRightHand)
            {
                _tillerRightHand = _tiller.RightHand;
                _tillerIntrusion = float.PositiveInfinity;
            }
            float gap = _tiller.ClearanceGap(_root.TransformPoint(_ixOffset));
            if (gap > _tillerIntrusion)
            {
                _ixOffsetWorld += _tiller.AwaySide * (gap - _tillerIntrusion);
                _ixOffset = Quaternion.Inverse(rr) * _ixOffsetWorld;
                gap = _tillerIntrusion;
            }
            _tillerIntrusion = Mathf.Max(gap, 0f);
        }

        /// <summary>Chest, head, mouth and facing of this body right now, for posing an item against it.</summary>
        private BodyFrame BuildFrame(Vector3 chest)
        {
            Vector3 fwd = _instance.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) { fwd = _root.forward; fwd.y = 0f; }
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Quaternion yaw = Quaternion.LookRotation(fwd, Vector3.up);
            Vector3 head = _bHead != null ? _bHead.position : chest + Vector3.up * 0.25f;
            return new BodyFrame
            {
                Chest = chest,
                Head = head,
                Mouth = _bHead != null ? HeadMouth() : head + fwd * ItemPoseTuning.MouthForward.Value + Vector3.up * ItemPoseTuning.MouthUp.Value,
                Eye = head + fwd * 0.08f + Vector3.up * 0.09f,
                Feet = _root.TransformPoint(new Vector3(_ixOffset.x, FittedFeetLocalY, _ixOffset.z)),
                Right = yaw * Vector3.right,
                Forward = fwd,
                Yaw = yaw,
                LookPitch = _lookPitch,
                HasPointer = _heldView != null,
                PointerRot = _heldView != null ? _heldView.rotation : Quaternion.identity,
            };
        }

        /// <summary>
        /// Both arms, once per frame, after the gait, crouch and look-lean have posed them. Each hand has a grip:
        /// a palm position and the direction its fingers and palm face. Each arm blends from the walk pose toward
        /// its grip by its own eased weight, so hands rise onto a control or item and drop off it smoothly, and a
        /// claim on an arm simply skips it.
        /// </summary>
        private void DriveArms(float dt, bool armL, bool armR)
        {
            var kind = EffectiveInteraction();
            CurrentInteraction = kind;
            _itemPoseValid = false;
            if (!_armR.Ready && !_armL.Ready) return;

            Transform body = _instance.transform;
            Vector3 bodyRight = body.right, bodyUp = body.up, bodyFwd = body.forward;
            // A ragdoll's chest is wherever the fall put it, not where the body's own transform stands.
            if (_rag01 > 0.3f && _bSpine != null)
            {
                bodyUp = _bSpine.rotation * _chestUpLocal;
                bodyFwd = _bSpine.rotation * _chestFwdLocal;
                bodyRight = Vector3.Cross(bodyUp, bodyFwd).normalized;
            }
            // The chest turned on the seat toward a control took the shoulders round with it, so the elbows hang
            // off a body that is up to SeatedTurnDegrees from the way the seat faces. Turn the axes the elbow
            // hints are built from by the same amount, and the elbows keep pointing down and out from the chest.
            else if (_rotor.Active && _seat01 > 0.01f && Mathf.Abs(_seatTurnDeg) > 0.01f)
            {
                Quaternion seatTurn = Quaternion.AngleAxis(_seatTurnDeg * _seat01, Vector3.up);
                bodyRight = seatTurn * bodyRight;
                bodyFwd = seatTurn * bodyFwd;
            }
            Vector3 chest = (_bShoulderL != null && _bShoulderR != null)
                ? (_bShoulderL.position + _bShoulderR.position) * 0.5f
                : body.position + bodyUp * (MeasuredHeight * 0.8f);

            HandGrip gr = default(HandGrip), gl = default(HandGrip);
            bool carry = IsCarry(kind);

            switch (kind)
            {
                case InteractionKind.Carry:
                case InteractionKind.CarryBig:
                    if (HeldToolPose.Mode.Value == HeldPoseMode.HandReachesItem)
                    {
                        gr = new HandGrip { On = true, Palm = _heldPos, Normal = -bodyRight, Finger = bodyFwd };
                    }
                    else
                    {
                        var profile = ItemPoses.Profile(_heldItem);
                        if (profile != null)
                        {
                            ItemPoses.Solve(profile, _heldPos, _heldRot, BuildFrame(chest), dt, out _itemPose);
                            _itemPoseValid = true;
                            gr = _itemPose.R;
                            gl = _itemPose.L;
                        }
                    }
                    break;

                case InteractionKind.Rope:
                {
                    Vector3 l, r;
                    InteractionGeometry.Rope(_ixTarget.position, chest, bodyFwd, bodyRight, out l, out r);
                    Vector3 along = (r - l).sqrMagnitude > 1e-6f ? (r - l).normalized : bodyFwd;
                    gr = new HandGrip { On = true, Palm = r, Normal = -bodyRight, Finger = bodyFwd, Axis = along };
                    gl = new HandGrip { On = true, Palm = l, Normal = bodyRight, Finger = bodyFwd, Axis = along };
                    // The rope end or the length adjuster is drawn in the leading hand, not a meter out in front.
                    _itemPose = new ItemPoseResult { Repose = true, Pos = r, Rot = _ixTarget.rotation, ScaleMul = Vector3.one, R = gr, L = gl };
                    _itemPoseValid = true;
                    break;
                }

                case InteractionKind.Helm:
                case InteractionKind.Crank:
                    if (_rotor.Active)
                    {
                        gr = new HandGrip { On = true, Palm = _rotor.PalmR, Normal = _rotor.NormalR, Finger = _rotor.FingerR, Axis = _rotor.AxisR };
                        gl = new HandGrip { On = true, Palm = _rotor.PalmL, Normal = _rotor.NormalL, Finger = _rotor.FingerL, Axis = _rotor.AxisL };
                    }
                    else if (kind == InteractionKind.Helm && _tiller.Active)
                    {
                        // One hand round the tiller near its end; which hand was decided when it was taken.
                        var hold = new HandGrip
                        {
                            On = true, Palm = _tiller.Grip, Normal = Vector3.down,
                            Finger = Vector3.ProjectOnPlane(_tiller.FingerHint, _tiller.Along), Axis = _tiller.Along
                        };
                        if (_tiller.RightHand) gr = hold;
                        else gl = hold;
                    }
                    break;

                case InteractionKind.Push:
                {
                    Vector3 l, r;
                    InteractionGeometry.Push(_ixTarget, chest, bodyRight, out l, out r);
                    gr = new HandGrip { On = true, Palm = r, Normal = bodyFwd, Finger = bodyUp };
                    gl = new HandGrip { On = true, Palm = l, Normal = bodyFwd, Finger = bodyUp };
                    break;
                }
            }

            // Seated with nothing in a hand: that hand rests on its thigh, near the knee. Straddling a spar, both
            // hands hold it in front; turning on the seat, they push down on it beside the hips.
            if (_seat01 > 0.5f && _legIkReady && _rag01 <= 0.3f && _getUp01 <= 0.3f)
            {
                Quaternion seatRot = _root.rotation * SeatYaw();
                Vector3 seatFwd = seatRot * Vector3.forward, seatRight = seatRot * Vector3.right;
                Vector3 hips = SeatHipsDrawn();
                if (!gr.On && _bUpperLegR != null && _bLowerLegR != null)
                    gr = SeatedHand(true, hips, seatFwd, seatRight);
                if (!gl.On && _bUpperLegL != null && _bLowerLegL != null)
                    gl = SeatedHand(false, hips, seatFwd, -seatRight);
            }

            float blendSpeed = carry ? HeldToolPose.BlendSpeed.Value : InteractionTuning.BlendSpeed.Value;
            float down = carry ? HeldToolPose.ElbowDown.Value : InteractionTuning.ElbowDown.Value;
            float outward = carry ? HeldToolPose.ElbowOut.Value : InteractionTuning.ElbowOut.Value;
            Vector3 poleR = -bodyUp * down + bodyRight * outward, poleL = -bodyUp * down - bodyRight * outward;

            // Down: the arms go over the face or head, or fling out, on top of the ragdoll's own arms.
            if (_rag01 > 0.3f)
            {
                gr = gl = default(HandGrip);
                ReactionHands(dt, ref gr, ref gl, ref poleR, ref poleL);
                blendSpeed = 10f;
            }
            // Getting up: hands on the floor to push up, then on the knees, then letting go.
            else if (_getUp01 > 0.3f)
            {
                gr = gl = default(HandGrip);
                if (_guHandW > 0.35f)
                {
                    gr = new HandGrip { On = true, Palm = _guHandR, Normal = Vector3.down, Finger = (_guHandR - chest).normalized };
                    gl = new HandGrip { On = true, Palm = _guHandL, Normal = Vector3.down, Finger = (_guHandL - chest).normalized };
                }
                poleR = -bodyUp + bodyRight * 0.6f;
                poleL = -bodyUp - bodyRight * 0.6f;
                blendSpeed = 10f;
            }
            // Swimming with nothing in the hands: sculling, or the crawl.
            else if (_swim01 > 0.3f && !carry && _bShoulderR != null && _bShoulderL != null)
            {
                float stroke = SwimStroke(0f);
                gr = SwimHand(_bShoulderR.position, bodyUp, bodyFwd, bodyRight, stroke);
                gl = SwimHand(_bShoulderL.position, bodyUp, bodyFwd, -bodyRight, stroke);
                // Elbows out to the sides and a little toward the feet, never through the chest.
                poleR = (bodyRight - bodyUp * 0.4f).normalized;
                poleL = (-bodyRight - bodyUp * 0.4f).normalized;
                blendSpeed = 12f;
            }
            float wrist = ItemPoseTuning.MaxWristBend.Value;

            float itemTarget = (carry || kind == InteractionKind.Rope) && _itemPoseValid && _itemPose.Repose ? 1f : 0f;
            _itemBlend = Mathf.Lerp(_itemBlend, itemTarget, 1f - Mathf.Exp(-blendSpeed * dt));

            // An arm that lets go lowers along the path it came up on.
            if (gr.On) _lastGripR = gr;
            if (gl.On) _lastGripL = gl;

            // Each hand starts every frame hanging relaxed, palm to the thigh; a grip blends it from there.
            if (armR && _armR.Ready)
            {
                _armR.RestHand(-bodyRight, bodyFwd);
                _armR.SolveGrip(_lastGripR.Palm, _lastGripR.Finger, _lastGripR.Normal, _lastGripR.Axis,
                    poleR, gr.On ? 1f : 0f, blendSpeed, dt, wrist);
            }
            if (armL && _armL.Ready)
            {
                _armL.RestHand(bodyRight, bodyFwd);
                _armL.SolveGrip(_lastGripL.Palm, _lastGripL.Finger, _lastGripL.Normal, _lastGripL.Axis,
                    poleL, gl.On ? 1f : 0f, blendSpeed, dt, wrist);
            }

            // A crewmate's item is written here; the local body draws its own at render time instead.
            if (carry && _itemPoseValid && _itemPose.Repose && !HeldItemRenderOnly && _heldItem != null && PlacesHeldItemInHand)
            {
                Vector3 p = Vector3.Lerp(_heldPos, _itemPose.Pos, _itemBlend);
                Quaternion q = Quaternion.Slerp(_heldRot, _itemPose.Rot, _itemBlend);
                _heldItem.SetPositionAndRotation(p, q);
                if (_heldFollower != null) _heldFollower.SetPositionAndRotation(p, q);
            }
        }

        /// <summary>
        /// Where an empty hand rests while seated: on the thigh near the knee, on a straddled spar in front of the
        /// hips, pressed on the seat beside the hips while turning, or as the floor pose has it. The seat surface is
        /// a hip-joint height below the hips, which is how Seating places them. Called after the legs are posed, so
        /// the knees are where they are drawn.
        /// </summary>
        private HandGrip SeatedHand(bool right, Vector3 hips, Vector3 fwd, Vector3 outward)
        {
            Transform upper = right ? _bUpperLegR : _bUpperLegL, lower = right ? _bLowerLegR : _bLowerLegL;
            Vector3 palm = Vector3.Lerp(upper.position, lower.position, 0.72f) + Vector3.up * 0.08f;
            Vector3 normal = Vector3.down, finger = fwd;
            if (_straddle01 > 0.001f)
                palm = Vector3.Lerp(palm, hips + fwd * 0.2f + outward * 0.07f - Vector3.up * 0.06f, _straddle01);

            float floorW = FloorWeight();
            if (floorW > 0.001f)
            {
                Vector3 sumPalm = Vector3.zero, sumNormal = Vector3.zero, sumFinger = Vector3.zero;
                for (int i = 0; i < _floorW.Length; i++)
                {
                    if (_floorW[i] < 0.001f) continue;
                    Vector3 p, n, f;
                    FloorHand(i, right, hips, fwd, outward, lower.position, out p, out n, out f);
                    sumPalm += p * _floorW[i];
                    sumNormal += n * _floorW[i];
                    sumFinger += f * _floorW[i];
                }
                float k = Mathf.Clamp01(floorW);
                palm = Vector3.Lerp(palm, sumPalm / floorW, k);
                if (sumNormal.sqrMagnitude > 1e-6f) normal = Vector3.Slerp(normal, sumNormal.normalized, k);
                if (sumFinger.sqrMagnitude > 1e-6f) finger = Vector3.Slerp(finger, sumFinger.normalized, k);
            }

            if (_tuck > 0.001f)
            {
                palm = Vector3.Lerp(palm, hips + outward * 0.24f - fwd * 0.02f - Vector3.up * 0.08f, _tuck);
                normal = Vector3.Slerp(normal, Vector3.down, _tuck);
                finger = Vector3.Slerp(finger, fwd, _tuck);
            }
            return new HandGrip { On = true, Palm = palm, Normal = normal, Finger = finger };
        }

        /// <summary>One hand for one floor pose: the palm, the way the palm faces, and the way the fingers point.</summary>
        private void FloorHand(int pose, bool right, Vector3 hips, Vector3 fwd, Vector3 outward, Vector3 knee,
            out Vector3 palm, out Vector3 normal, out Vector3 finger)
        {
            // Both knees, for a hug round the pair of them.
            Vector3 knees = _bLowerLegL != null && _bLowerLegR != null ? (_bLowerLegL.position + _bLowerLegR.position) * 0.5f : knee;
            // Flat on the floor behind and beside the hips, fingers out and back, taking the weight of a lean.
            Vector3 behind = hips - fwd * 0.16f + outward * 0.2f;
            behind.y = _seatFloorY + 0.03f;
            switch (pose)
            {
                case 0:   // legs out: leaning back on both hands
                    palm = behind; normal = Vector3.down; finger = (outward - fwd * 0.5f).normalized;
                    break;
                case 1:   // cross-legged: hands resting on the knees
                    palm = knee + Vector3.up * 0.07f - fwd * 0.02f; normal = Vector3.down; finger = fwd;
                    break;
                case 2:   // one knee up: that arm hung over the knee, the other hand behind on the floor
                    if (right) { palm = knee + fwd * 0.08f - Vector3.up * 0.07f; normal = -fwd; finger = (fwd * 0.5f - Vector3.up).normalized; }
                    else { palm = behind; normal = Vector3.down; finger = (outward - fwd * 0.5f).normalized; }
                    break;
                default:  // knees hugged: both hands clasped in front of the shins
                    palm = knees + fwd * 0.08f - Vector3.up * 0.12f + outward * 0.04f; normal = -fwd; finger = -outward;
                    break;
            }
        }

        /// <summary>
        /// How far back the hips must travel, at a given drop, to lift the thigh to CrouchThighLiftDeg above
        /// the hip-to-ankle line.
        ///
        /// WHY A SOLVE AND NOT A CONSTANT. The old crouch dropped the hips straight down over planted feet,
        /// which is kneeling ("seiza"), not squatting. It cannot be fixed by rotating anything: with the hip
        /// directly above the foot the knee is confined to a HORIZONTAL circle, so the IK pole chooses only
        /// which way the knee points, never how high it sits, and while the thigh is no shorter than the shin
        /// the knee can never reach hip height at all. Only moving the hip backward opens the angle.
        ///
        /// The distance for a given angle depends on the rig's actual bone lengths - across plausible
        /// proportions the same visual angle needs anywhere from about 0.09m to 0.21m - so the config knob is
        /// the ANGLE and the meters are derived here from the lengths captured at bind time. Because the
        /// result closes a triangle the leg can reach, the leg cannot be over-extended by construction, and an
        /// unreachable request returns 0 (unchanged pose) rather than a snapped or straightened leg.
        ///
        /// PASS THE FULL CROUCH DEPTH, NOT THE CURRENT ONE, and scale the result by the crouch amount. This
        /// solve is NOT continuous in dropMeters: the triangle only closes once the hips are low enough that
        /// the shin can still reach the ankle (roughly the last fifth of the descent), and at the exact moment
        /// it becomes solvable the sqrt is still about 0, so s appears at its maximum a*cos(E) and then
        /// DECREASES as the player settles. Feeding it the live depth therefore pinned the hips at zero setback
        /// for most of the transition, snapped them about 0.35m backward at a threshold, then crept them
        /// forward again - a visible flicker, and one that oscillates if the crouch amount dithers around that
        /// threshold. Evaluating the target pose once and easing into it is monotonic and smooth by construction.
        /// </summary>
        private float SolveHipSetback(float dropMeters)
        {
            if (!_legIkReady) return 0f;
            float maxBack = BodyTuning.CrouchHipSetbackMaxMeters.Value;
            if (maxBack <= 0.0001f) return 0f;      // 0 = opt out, back to the straight-down crouch
            float a = _thighLenL, b = _shinLenL;
            if (a < 1e-3f || b < 1e-3f) return 0f;

            float yh = _hipAboveFoot - dropMeters;  // hip height over the ankle after the drop
            if (yh <= 0.01f) return 0f;             // hips at or below the ankle: nothing sane to solve

            float e = BodyTuning.CrouchThighLiftDeg.Value * Mathf.Deg2Rad;
            // The knee sits a*(cos e forward, sin e up) from the hip; the shin must still reach the ankle.
            float dy = yh + a * Mathf.Sin(e);
            float inner = b * b - dy * dy;
            if (inner <= 0f) return 0f;             // shin too short for that lift: leave the pose alone
            float sBack = a * Mathf.Cos(e) - Mathf.Sqrt(inner);
            return Mathf.Clamp(sBack, 0f, maxBack);
        }

        /// <summary>
        /// Two-bone leg IK for one leg, run each crouched frame AFTER the body drop. The hip (UpperLeg) has
        /// already been lowered by the drop; this rotates the thigh and shin so the ANKLE returns to its
        /// standing world target F, keeping the foot planted. Aiming is axis-agnostic: rotate each bone so its
        /// captured bone-to-child LOCAL aim axis points at its solved target, so nothing relies on the rig's
        /// local axis signs.
        /// </summary>
        private static void SolveLegIk(Transform root, Transform hip, Transform knee,
            float thighLen, float shinLen, Vector3 footLocal, Vector3 thighAimLocal, Vector3 shinAimLocal, float kneeForwardSign,
            Vector3 stepOffset, Transform foot, Quaternion footRotRoot, Quaternion footLocalBind, float crouch,
            Vector3 frameOffsetLocal, Quaternion frameYaw, Vector3 poleWorld = default(Vector3))
        {
            if (hip == null || knee == null) return;
            float a = thighLen, b = shinLen;
            if (a < 1e-4f || b < 1e-4f) return;

            Vector3 H = hip.position;                    // dropped hip
            // Standing ankle target (moves with the body and boat, not with the drop) PLUS a per-frame step
            // offset so the feet actually stride while crouch-walking (0 when standing = a planted crouch).
            // The standing spot turns and steps with the body when it is using a control (identity otherwise).
            Vector3 F = root.TransformPoint(frameOffsetLocal + frameYaw * footLocal) + stepOffset;
            Vector3 hf = F - H;
            float d = Mathf.Clamp(hf.magnitude, Mathf.Abs(a - b) + 1e-3f, a + b - 1e-3f);
            Vector3 dir = hf.sqrMagnitude > 1e-8f ? hf.normalized : -root.up; // hip to foot (normally downward)

            // Law of cosines: the angle at the hip between the hip-to-foot line and the thigh.
            float cosH = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float hipAngle = Mathf.Acos(cosH);

            // Pole = body forward, projected perpendicular to dir, so the knee points forward = a squat. A caller
            // can point the knee elsewhere: up for a knee drawn to the chest, out to the side for cross-legged.
            Vector3 fwd = poleWorld.sqrMagnitude > 1e-6f ? poleWorld : (root.rotation * frameYaw * Vector3.forward) * kneeForwardSign;
            Vector3 pole = fwd - Vector3.Dot(fwd, dir) * dir;
            if (pole.sqrMagnitude < 1e-6f) pole = root.up - Vector3.Dot(root.up, dir) * dir; // degenerate guard
            if (pole.sqrMagnitude < 1e-6f) { pole = Vector3.up; }
            pole.Normalize();

            // Solved knee position, then aim the thigh at it.
            Vector3 K = H + a * (Mathf.Cos(hipAngle) * dir + Mathf.Sin(hipAngle) * pole);
            Vector3 wantThigh = K - H;
            if (wantThigh.sqrMagnitude < 1e-10f) return;
            Vector3 worldAim = hip.TransformDirection(thighAimLocal);
            hip.rotation = Quaternion.FromToRotation(worldAim, wantThigh.normalized) * hip.rotation;

            // Aim the shin from the (now-moved) knee toward the foot target. Read the knee fresh after the thigh.
            Vector3 Kp = knee.position;
            Vector3 wantShin = F - Kp;
            if (wantShin.sqrMagnitude < 1e-10f) return;
            Vector3 worldAim2 = knee.TransformDirection(shinAimLocal);
            knee.rotation = Quaternion.FromToRotation(worldAim2, wantShin.normalized) * knee.rotation;

            // ANKLE ORIENTATION. Everything above solves the ankle's POSITION; nothing solved its ROTATION,
            // and the rig chain is UpperLeg -> LowerLeg -> Foot, so the foot rigidly inherited the shin's world
            // rotation. At the default 0.6m crouch drop the shin sits roughly 70-78 deg off vertical, which
            // pitched the soles that far toes-down: the reported "tippy toes".
            //
            // Restoring the captured standing orientation is the whole fix - a real ankle keeps the sole flat
            // to the deck while the shin swings over it. Written AFTER both bone aims, because each of those
            // rotates a parent and would otherwise carry the foot with it. Blended by crouch amount so the
            // correction fades in with the squat rather than popping at the 0.001 threshold.
            //
            // THE localRotation RESET IS LOAD-BEARING - DO NOT REMOVE IT. Slerping from the foot's CURRENT
            // rotation would read this method's own previous output, making it a recursive filter: it would
            // converge to `level` at any sustained crouch (not the crouch-proportional blend intended), its
            // rate would be frame-rate dependent, and - worst - the block is gated off below 0.001 crouch, so
            // whatever partial rotation the last frame wrote would be FROZEN into the standing and walking
            // pose until the next crouch. Restoring the bind local first makes the source the
            // walk-cycle-inherited pose every frame, so the write is deterministic and carries nothing over.
            if (foot != null)
            {
                foot.localRotation = footLocalBind;
                Quaternion level = root.rotation * frameYaw * footRotRoot;
                foot.rotation = Quaternion.Slerp(foot.rotation, level, Mathf.Clamp01(crouch));
            }
        }
    }
}

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
        private Transform _bFootL, _bFootR, _bHandR, _bHead;
        private Quaternion _qSpine, _qUpperLegL, _qUpperLegR, _qLowerLegL, _qLowerLegR, _qShoulderL, _qShoulderR, _qElbowL, _qElbowR;
        // Foot bind LOCAL rotations. The crouch ankle write is an ABSOLUTE world write, so it must reset to
        // these first every frame - otherwise it reads its own previous output and becomes a self-feeding
        // filter that freezes a rotated ankle into the standing pose. See SolveLegIk.
        private Quaternion _qFootL = Quaternion.identity, _qFootR = Quaternion.identity;

        public Transform Spine { get { return _bSpine; } }
        public Transform Head { get { return _bHead; } }
        public Transform HandR { get { return _bHandR; } }

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
        private Transform _heldItem, _heldFollower;
        private Vector3 _heldPos, _armTarget;
        private Quaternion _heldRot = Quaternion.identity;
        private bool _heldBig;
        private int _heldFrame = -10;

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
            _bHead = BodyTemplate.FindDeep(r, "Head");

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
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null) _renderers[i].enabled = on;
        }

        /// <summary>
        /// Restyle this body in place after an appearance change. Deliberately NOT a destroy-and-rebuild: a
        /// rebuild is throttled, it is gated on scaled Time.time (frozen while the pause menu holds timeScale
        /// at 0, which is exactly where a character screen lives), and it would re-run the leg-IK bind
        /// capture, which is unsafe to repeat - see CaptureLegIkBind.
        /// </summary>
        public void RefreshAppearance(PlayerAppearance appearance)
        {
            if (_instance == null) return;
            var c = _instance.GetComponentInChildren<PsychoticLab.CharacterCustomizer>(true);
            if (c != null) appearance.ApplyLive(c);
        }

        public void Destroy()
        {
            Poses.Clear();
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
        }

        // ---- held tool ---------------------------------------------------------------------------------

        /// <summary>
        /// Hand in this frame's pose for the item this body holds. <paramref name="item"/> is the transform to
        /// draw in the hand when <see cref="PlacesHeldItemInHand"/> is true; <paramref name="follower"/>
        /// (optional) is moved with it, for an item whose physics object is a separate transform. Big items
        /// (crates, barrels) are carried out in front, so they never go in the hand; the arm reaches toward
        /// them instead.
        /// </summary>
        public void SetHeldItemPose(Transform item, Transform follower, Vector3 worldPos, Quaternion worldRot, bool big)
        {
            _heldItem = item;
            _heldFollower = follower;
            _heldPos = worldPos;
            _heldRot = worldRot;
            _heldBig = big;
            _heldFrame = Time.frameCount;
        }

        /// <summary>
        /// True when this body draws the held item itself (in its hand), so the caller must NOT write the
        /// item's transform. Only meaningful after this frame's SetHeldItemPose.
        ///
        /// This MUST mirror every gate on the path to the arm solve. If it said yes while the solve never ran,
        /// nobody would write the item and it would freeze in mid-air - which is exactly what happens if the
        /// rig or the arm capture failed, or if something has claimed the right arm.
        /// </summary>
        public bool PlacesHeldItemInHand
        {
            get
            {
                return _instance != null
                       && RigReady
                       && _armR.Ready
                       && !_heldBig
                       && !Poses.IsSuppressed(PoseParts.RightArm)
                       && HeldToolPose.Mode != null
                       && HeldToolPose.Mode.Value == HeldPoseMode.ItemInHand;
            }
        }

        // ---- per-frame ---------------------------------------------------------------------------------

        /// <summary>
        /// Advance this body one frame: the one-time fit, then the pose. Call from LateUpdate, after the root
        /// has been placed. Never throws on a missing rig - it simply does nothing.
        /// </summary>
        public void Tick(float dt)
        {
            if (_instance == null || _root == null) return;
            if (_dressCheckFrame > 0 && Time.frameCount >= _dressCheckFrame) { _dressCheckFrame = 0; VerifyDressed(); }
            if (_needsFit) Fit();
            else if (_hasBodyBase) ReplantIfNudged();
            DriveAnimation(Mathf.Max(dt, 1e-4f));
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
            Bounds wb = _renderers[0].bounds;
            for (int i = 1; i < _renderers.Length; i++) wb.Encapsulate(_renderers[i].bounds);
            if (wb.size.y < 0.5f) return; // bounds not ready yet (degenerate) - retry next frame

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
                $"legIk={_legIkReady}, armIk={_armR.Ready}");
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
            _instance.transform.localPosition = _bodyBaseLocalPos;
            Plugin.Log.LogInfo($"[PlayerModel] {_name} re-planted: sole offset {nudge:F3} (feetLocalY now {FittedFeetLocalY:F3})");
        }

        private void CaptureArmIkBind()
        {
            if (!_armR.Capture(_bShoulderR, _bElbowR, _bHandR))
                Plugin.Log.LogWarning($"[PlayerModel] {_name}: right arm bones not found (shoulder={_bShoulderR != null}, " +
                    $"elbow={_bElbowR != null}, hand={_bHandR != null}); held items will float as before.");
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
            float crouch = _crouch01;

            // Crouch-walk: the gait continues but with a shorter stride, so a crouched body still steps. The
            // phase advances even while a claim owns the legs, so releasing a claim does not snap the stride.
            float blend = Mathf.Clamp01(_animSpeed / WalkFullSpeed) * (1f - CrouchStrideCut * crouch);
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

            // LOOK-LEAN. The crouch fold is +CrouchTorsoLean about root.right = a FORWARD fold, and
            // MouseLook.rotationY is positive when looking UP, so NEGATE the pitch to make looking DOWN fold
            // FORWARD (and looking UP lean BACK). LookPitchScale set NEGATIVE flips the whole direction live.
            // Clamped so the torso never over-bends.
            float lookLean = Mathf.Clamp(-_lookPitch * LookPitchScale, -LookPitchMaxDeg, LookPitchMaxDeg);

            // Spine world-pitch EVERY frame, standing included: the crouch FORWARD fold (0 when standing) plus
            // the look-lean, composed into ONE world-space rotate about root.right AFTER the breathe swing.
            // This is the single spine pitch, so the look-lean pivots the whole upper body (Spine_01 to
            // chest/head/arms) on the hips while standing and adds to the crouch fold when crouched.
            float spinePitch = CrouchTorsoLean * crouch + lookLean;
            if (spine && _bSpine != null && Mathf.Abs(spinePitch) > 0.001f)
                _bSpine.Rotate(_root.right, spinePitch, Space.World);

            // Crouch body drop: lower the whole body so the hips, torso and head come down, relative to the
            // planted base. MUST run BEFORE the leg IK so the IK reads the DROPPED hip joints. At crouch 0 this
            // restores the exact base, so standing is untouched.
            if (_hasBodyBase && bodyOffset)
            {
                float dropM = CrouchDrop * crouch;
                // Solve the TARGET pose (full crouch) and scale it in, rather than re-solving at the current
                // depth every frame - see SolveHipSetback for why that flickered.
                float backM = SolveHipSetback(CrouchDrop) * crouch;
                // Root-local +Z is the body's facing, so subtracting walks the hips BACKWARD, which is what
                // turns a kneel into a squat.
                CrouchOffset = new Vector3(0f, dropM, backM);
                _instance.transform.localPosition = _bodyBaseLocalPos - CrouchOffset;
            }

            // Leg IK: after the drop moved the hips down, re-plant both ankles (knees bend FORWARD = squat).
            // CROUCH-WALK: the ankle targets STEP with the gait so the legs actually stride while crouched -
            // each foot swings forward and back (root.forward) and lifts (root.up) on its half of the cycle,
            // alternating left and right, scaled by the walk blend (0 when standing = a planted static squat).
            // Flip CrouchKneeForward to -1 if the knees ever bend backward.
            if (legs && _legIkReady && crouch > 0.001f)
            {
                float kf = BodyTuning.CrouchKneeForward.Value;
                const float StepLen = 0.28f;   // m, foot forward/back travel at full gait
                const float StepLift = 0.12f;  // m, swing-foot lift
                Vector3 stepL = _root.forward * (StepLen * blend * s)    + _root.up * (StepLift * blend * Mathf.Max(0f, s));
                Vector3 stepR = _root.forward * (StepLen * blend * sOpp) + _root.up * (StepLift * blend * Mathf.Max(0f, sOpp));
                SolveLegIk(_root, _bUpperLegL, _bLowerLegL, _thighLenL, _shinLenL, _footLocalL, _thighAimLocalL, _shinAimLocalL, kf, stepL, _bFootL, _footRotRootL, _qFootL, crouch);
                SolveLegIk(_root, _bUpperLegR, _bLowerLegR, _thighLenR, _shinLenR, _footLocalR, _thighAimLocalR, _shinAimLocalR, kf, stepR, _bFootR, _footRotRootR, _qFootR, crouch);
            }
            else if (legs && _legIkReady)
            {
                // STANDING/WALKING: guarantee the ankle is back at bind. The crouch block above is gated off
                // below 0.001 crouch and nothing else here writes the foot, so without this the last crouched
                // frame's ankle rotation would be frozen into the standing and walking pose for the session.
                if (_bFootL != null) _bFootL.localRotation = _qFootL;
                if (_bFootR != null) _bFootR.localRotation = _qFootR;
            }

            // Last of our own writes, so the arm reaches from where the crouch and look-lean left the
            // shoulder. Claimants run after this, from Tick.
            if (armR) DriveHeldItemPose(dt);
        }

        private void DriveHeldItemPose(float dt)
        {
            if (!_armR.Ready || HeldToolPose.Mode == null) return;
            var mode = HeldToolPose.Mode.Value;
            bool holding = mode != HeldPoseMode.Off && _heldItem != null && Time.frameCount - _heldFrame <= 1;

            if (holding)
            {
                if (mode == HeldPoseMode.ItemInHand && !_heldBig)
                {
                    // Aim the hand the way the holder aims the item: from the head toward where the item really
                    // is, stopped at a comfortable holding distance, then nudged down and to the side.
                    Vector3 head = _bHead != null ? _bHead.position : _root.position + _root.up * 0.75f;
                    Vector3 toItem = _heldPos - head;
                    Vector3 dir = toItem.sqrMagnitude > 1e-6f ? toItem.normalized : _root.forward;
                    _armTarget = head + dir * HeldToolPose.HoldDistance.Value
                                 + _root.up * HeldToolPose.HoldDrop.Value
                                 + _root.right * HeldToolPose.HoldSide.Value;
                }
                else
                {
                    _armTarget = _heldPos;
                }
            }

            Vector3 pole = -_root.up * HeldToolPose.ElbowDown.Value + _root.right * HeldToolPose.ElbowOut.Value;
            _armR.Solve(_armTarget, pole, holding ? 1f : 0f, HeldToolPose.BlendSpeed.Value, dt);

            if (holding && PlacesHeldItemInHand && _armR.Hand != null && _heldItem != null)
            {
                Quaternion rot = _heldRot * Quaternion.Euler(HeldToolPose.GripPitch.Value, HeldToolPose.GripYaw.Value, HeldToolPose.GripRoll.Value);
                Vector3 pos = _armR.Hand.position + rot * new Vector3(HeldToolPose.GripX.Value, HeldToolPose.GripY.Value, HeldToolPose.GripZ.Value);
                _heldItem.position = pos;
                _heldItem.rotation = rot;
                if (_heldFollower != null)
                {
                    _heldFollower.position = pos;
                    _heldFollower.rotation = rot;
                }
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
            Vector3 stepOffset, Transform foot, Quaternion footRotRoot, Quaternion footLocalBind, float crouch)
        {
            if (hip == null || knee == null) return;
            float a = thighLen, b = shinLen;
            if (a < 1e-4f || b < 1e-4f) return;

            Vector3 H = hip.position;                    // dropped hip
            // Standing ankle target (moves with the body and boat, not with the drop) PLUS a per-frame step
            // offset so the feet actually stride while crouch-walking (0 when standing = a planted crouch).
            Vector3 F = root.TransformPoint(footLocal) + stepOffset;
            Vector3 hf = F - H;
            float d = Mathf.Clamp(hf.magnitude, Mathf.Abs(a - b) + 1e-3f, a + b - 1e-3f);
            Vector3 dir = hf.sqrMagnitude > 1e-8f ? hf.normalized : -root.up; // hip to foot (normally downward)

            // Law of cosines: the angle at the hip between the hip-to-foot line and the thigh.
            float cosH = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float hipAngle = Mathf.Acos(cosH);

            // Pole = body forward, projected perpendicular to dir, so the knee points forward = a squat.
            Vector3 fwd = root.forward * kneeForwardSign;
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
                Quaternion level = root.rotation * footRotRoot;
                foot.rotation = Quaternion.Slerp(foot.rotation, level, Mathf.Clamp01(crouch));
            }
        }
    }
}

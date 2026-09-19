using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// YOUR OWN humanoid body, in third person. Sailwind's local player is bodyless: look down and there is
    /// nothing there, and the ship-orbit camera shows an empty deck. This puts a body at your feet, animated
    /// from your own movement, and hides it in first person so it can never clip the camera.
    ///
    /// Shown only while the ship-orbit camera (BoatCamera.on) is up, which is the only zoom-out view Sailwind
    /// has aboard a boat, and never in the shipyard, where that same camera is used to inspect the ship being
    /// edited. A camera-distance heuristic was tried instead and mis-fires during falls and fast moves, popping
    /// the body into view.
    ///
    /// Local-only and non-networked. With the co-op mod installed your crewmates see you through their own
    /// copy of your body, built from the same <see cref="SyntyBody"/>.
    /// </summary>
    public class LocalBody : MonoBehaviour
    {
        public static LocalBody Instance { get; private set; }

        private GameObject _root;        // re-positioned each frame at the player's feet + yaw
        private SyntyBody _body;
        private GameObject _tagObject;
        private TextMesh _tag;
        private Vector3 _tagBaseLocalPos;
        private bool _tagPlaced;
        private float _nextRetry;
        private bool _visible = true;

        // Gait driven by deck-relative speed (the controller's local-position delta).
        private Vector3 _prevLocalPos;
        private bool _havePrev;
        private Transform _prevParent;   // detect a reparent (board/disembark/teleport) and rebaseline

        /// <summary>The body, or null before one could be built. See <see cref="BodyTemplate"/>.</summary>
        public SyntyBody Body { get { return _body; } }

        /// <summary>Overrides the first-person hide, so something else can show the body on its own terms.</summary>
        public bool ForcedVisible { get; set; }

        // A carried item drawn in the hand at render time, then put back. See OnBeforeRender.
        private Transform _renderHeld;
        private Transform _restoreT;
        private Vector3 _restorePos;
        private Quaternion _restoreRot;
        private Vector3 _restoreScale;
        private bool _restorePending;

        // A mooring rope drawn in the hand keeps its line attached: the line's end is bent to the hand for the
        // frame and put back with the rope.
        private LineRenderer _restoreLine;
        private Vector3[] _lineSaved = new Vector3[0];
        private Vector3[] _lineWork = new Vector3[0];
        private Transform[] _restoreBones;
        private Vector3[] _bonesSaved = new Vector3[0];
        private static readonly AccessTools.FieldRef<RopeEffect, ClothRope> ClothRopeRef =
            AccessTools.FieldRefAccess<RopeEffect, ClothRope>("clothRope");

        // Skinned meshes with per-render skinning on, for the item drawn in the hands. See SyncRenderSkins.
        private readonly System.Collections.Generic.List<SkinnedMeshRenderer> _forcedSkins = new System.Collections.Generic.List<SkinnedMeshRenderer>();
        private Transform _forcedFor;

        // The same for the cloth rope drawn from a held rope or rod line. See SyncClothSkin.
        private Transform _clothFor;
        private RopeEffect _forcedRope;
        private SkinnedMeshRenderer _forcedCloth;

        // World-space particle emitters on the item drawn in the hands (a lit pipe's smoke). See SyncEmitters.
        private sealed class HeldEmitter
        {
            public Transform T;
            public ParticleSystem PS;
            public Vector3 LocalPos, DrawnPos, LeftPos;
            public Quaternion LocalRot, DrawnRot, LeftRot;
            public bool Left;      // on the drawn spot now
            public bool SeenLeft;  // on the drawn spot for the last particle update
        }
        private readonly System.Collections.Generic.List<HeldEmitter> _emitters = new System.Collections.Generic.List<HeldEmitter>();
        private readonly System.Collections.Generic.List<ParticleSystem> _emitterScan = new System.Collections.Generic.List<ParticleSystem>();
        private Transform _emittersFor;
        private bool _emittersDrawn;     // read where the drawn item holds them, to be left there once the item is put back
        private bool _emittersMoved;     // off their stock local poses
        private bool _emittersFailed;

        // Particle systems with inherited velocity turned off for the next particle update. See HoldInherit.
        private readonly System.Collections.Generic.List<ParticleSystem> _inheritHeld = new System.Collections.Generic.List<ParticleSystem>(4);
        private bool _inheritFailed;

        // The game's pipe exhale, and its stock local pose. See PlaceExhale.
        private Transform _exhale;
        private Vector3 _exhaleLocalPos;
        private Quaternion _exhaleLocalRot;
        private bool _exhaleMoved;
        private bool _exhaleFailed;

        private void Awake() { Instance = this; }
        private void OnDestroy() { if (Instance == this) Instance = null; }
        private void OnEnable() { Application.onBeforeRender += OnBeforeRender; }
        private void OnDisable() { Application.onBeforeRender -= OnBeforeRender; RestoreHeld(); ReleaseRenderSkins(); ReleaseEmitters(); RestoreInherit(); RestoreExhale(); }

        private void LateUpdate()
        {
            NoteParticleUpdate();
            try
            {
                if (!GameState.playing) { Teardown(); return; }
                var cc = Refs.charController;
                if (cc == null) return;

                if (_body == null)
                {
                    if (Time.time < _nextRetry) return;
                    _nextRetry = Time.time + 1.5f;
                    TryBuild();
                    if (_body == null) return;
                }

                PlaceRoot(cc);
                _body.SpeedMps = SampleDeckSpeed(cc);
                _body.Crouch01Target = VanillaPlayer.Crouch01();
                // The look lock freezes where the body looks as well as which way it faces, so the pitch holds
                // the value it had when the lock went on and the camera orbits without tilting the body.
                if (!PlayerOrbitCamera.LookLocked) _body.LookPitchDegTarget = VanillaPlayer.HeadLookPitchDeg();
                FeedRest();
                if (BodyTuning.SwimAnimation.Value && PlayerSwimming.swimming && !Downed.HoldsView)
                    _body.SetSwimming(!PlayerSwimming.swimmingOnSurface, _deckVelocity);
                bool visible = ForcedVisible || (BoatCamera.on && GameState.currentShipyard == null);
                // Sitting in first person: your own body from the chest down, fading in below the shoulders. Not while
                // something else has moved the view off the seat (a Three Sheets blackout fall), which would look back at it.
                bool chest = !visible && Seating.IsSeated && GameState.currentShipyard == null
                    && SeatingTuning.ShowBodyWhenSeated.Value && BodyShaders.SeatedFade != null && !Seating.EyeDisplaced;
                FeedInteraction(visible, chest);
                // Before Tick, so a throw there cannot leave an item moved at render time without its skinning. With
                // Interactions off the body never moves the item, so nothing needs it.
                Transform drawnItem = InteractionTuning.Enabled != null && InteractionTuning.Enabled.Value ? _renderHeld : null;
                SyncRenderSkins(drawnItem);
                SyncEmitters(drawnItem);
                _body.Tick(Time.deltaTime);
                // After Tick, from where the head is posed this frame.
                PlaceExhale(visible && BoatCamera.on);

                // After the pose, so the fade starts from where the shoulders are this frame.
                if (chest) _body.SetChestFade(BodyShaders.SeatedFade, SeatingTuning.BodyFadeBelowShoulders.Value, SeatingTuning.BodyFadeAboveHips.Value);
                else _body.EndChestFade();
                SetVisible(visible || chest, visible);
                _scorchFromView = !visible && (Seating.Smoke01 > 0f || Seating.Fire01 > 0f || Seating.Steam01 > 0f);

                if (_tagObject != null)
                {
                    // The tag is a SIBLING of the body, so it does not inherit the crouch drop and would hang
                    // at standing height over a crouched player. Keep it in step by hand.
                    _tagObject.transform.localPosition = _tagBaseLocalPos + _body.BodyOffset;
                    if (_visible && Camera.main != null)
                        _tagObject.transform.rotation = Camera.main.transform.rotation;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("[LocalBody] " + e);
                // The exhale is only placed after a pose that completed; left at an old spot it would stay there.
                RestoreExhale();
            }
        }

        /// <summary>
        /// Follow the player, facing the controller's yaw (NOT the camera's pitch or orbit).
        ///
        /// Aboard a boat the CharacterController lives in PHYSICS space (around Y200 underway) while the orbit
        /// camera and the deck render in VISUAL space, so positioning the body from the controller would put it
        /// far off-screen in the orbit cam. Source from Refs.observerMirror (the visual-frame controller
        /// mirror) when aboard - the same visual frame the orbit camera renders. On land the two frames
        /// coincide, so keep the controller there.
        /// </summary>
        private void PlaceRoot(CharacterController cc)
        {
            var observer = Refs.observerMirror != null ? Refs.observerMirror.transform : null;
            bool onBoatVisual = cc.transform.parent != null && cc.transform.parent.name != "_shifting world"
                                && GameState.currentBoat != null && observer != null;
            // Seated, in bed or knocked down the controller stays frozen where the player stood and only the observer
            // is at the seat, on land as well as aboard.
            bool pinned = observer != null && (Seating.IsSeated || GameState.inBed != null || Downed.HoldsView);
            var src = onBoatVisual || pinned ? observer : cc.transform;
            _root.transform.position = src.position;
            _root.transform.rotation = Quaternion.Euler(0f, src.eulerAngles.y, 0f);
            // Getting up, the view follows the body's head, so the body must not follow the view: it stands up onto
            // the controller's spot, found in the drawn frame (the two frames share local coordinates).
            if (Downed.IsRising && observer != null && observer.parent != null)
                _root.transform.position = observer.parent.TransformPoint(cc.transform.localPosition);
        }

        /// <summary>
        /// Deck-relative gait speed: the delta of the controller's LOCAL position, which excludes the boat's
        /// own motion while parented to one, so a body standing on a sailing deck does not phantom-walk.
        ///
        /// localPosition is relative to the CURRENT parent, so a reparent (boarding, disembarking, a join or
        /// sleep teleport, a floating-origin shift) remaps the same world spot to a different localPosition and
        /// would inject a spurious gait blip. Rebaseline on a parent change instead, and reject implausibly
        /// large single-frame deltas outright.
        /// </summary>
        private float SampleDeckSpeed(CharacterController cc)
        {
            var par = cc.transform.parent;
            if (par != _prevParent) { _havePrev = false; _prevParent = par; }
            Vector3 lp = cc.transform.localPosition;
            float speed = 0f;
            if (_havePrev)
            {
                float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                speed = (lp - _prevLocalPos).magnitude / dt;
                if (speed > 15f) speed = 0f; // floating-origin shift or teleport, not walking
            }
            // The same delta as a direction, for the swim to lie along.
            _deckVelocity = speed > 0f && par != null ? par.TransformVector((lp - _prevLocalPos) / Mathf.Max(Time.deltaTime, 1e-4f)) : Vector3.zero;
            _prevLocalPos = lp; _havePrev = true;
            return speed;
        }

        private readonly Quaternion[] _ragdoll = new Quaternion[SyntyBody.RagdollParts];
        private Vector3 _deckVelocity;

        /// <summary>Knocked down (see <see cref="Downed"/>), sitting (see <see cref="Seating"/>) or lying in one of the game's beds, and a scorched seat.</summary>
        private void FeedRest()
        {
            if (Seating.Smoke01 > 0f || Seating.Fire01 > 0f || Seating.Steam01 > 0f) _body.SetScorch(Seating.Smoke01, Seating.Fire01, Seating.Steam01);
            Vector3 pelvis; float downFloor; FallReaction reaction;
            if (Downed.TryGetRagdoll(out pelvis, _ragdoll, out downFloor, out reaction))
            {
                _body.SetRagdoll(pelvis, _ragdoll, downFloor, reaction);
                return;
            }
            float gettingUp;
            if (Downed.TryGetGettingUp(out gettingUp))
            {
                _body.SetGettingUp(gettingUp);
                return;
            }
            Vector3 a, b; SeatPose legs; float floorY;
            if (Seating.TryGetSeat(out a, out b, out legs, out floorY))
            {
                _body.SetSeat(a, b, legs, floorY);
                return;
            }
            Vector3 c;
            if (Seating.TryGetLying(out a, out b, out c)) _body.SetLying(a, b, c);
        }

        /// <summary>
        /// Tell the body what you are doing with your hands, read from the game's own pointer. Fed every frame,
        /// first person included: the body is hidden there, but keeping the pose running means switching to the
        /// orbit camera shows it already in place rather than easing in from nothing.
        /// </summary>
        private void FeedInteraction(bool visible, bool chestView)
        {
            _renderHeld = null;
            _body.HeldItemRenderOnly = true;

            Transform target;
            PickupableItem held;
            var kind = LocalInteraction.Sample(out target, out held);
            switch (kind)
            {
                case InteractionKind.Carry:
                case InteractionKind.CarryBig:
                    // Seen from your own eyes the game keeps the item framed in front of the view, so hands reaching
                    // for it lower down would hold nothing: they rest instead.
                    if (chestView) break;
                    var pointer = LocalInteraction.Pointer;
                    Quaternion heldRot = held.transform.rotation;
                    // This runs before GoPointer places the item, so the item still has last frame's hold, on last
                    // frame's pointer (and moved with the boat since). Put that hold on this frame's pointer, the view
                    // the pose compares it against.
                    if (pointer != null && PointerPlaced.Frame == Time.frameCount - 1 && PointerPlaced.Item == held)
                        heldRot = pointer.transform.rotation * PointerPlaced.Hold;
                    _body.SetHeldItem(held.transform, null, held.transform.position, heldRot, held.big,
                        pointer != null ? pointer.transform : null);
                    // Only draw it in the hands while the body can be seen. In first person the item stays where
                    // the game frames it for you.
                    if (visible) _renderHeld = held.transform;
                    break;
                case InteractionKind.None:
                    break;
                case InteractionKind.Rope:
                    _body.SetInteraction(kind, target);
                    if (visible) _renderHeld = target;
                    break;
                default:
                    _body.SetInteraction(kind, target);
                    break;
            }
        }

        /// <summary>
        /// Draw the held item in the hands for this frame's rendering only. The game repositions your held item
        /// every LateUpdate and its physics reads that transform, so it is moved here, after every LateUpdate,
        /// and put back at the end of the frame. Nothing but rendering ever sees the moved pose or scale.
        /// </summary>
        private bool _scorchFromView;

        private void OnBeforeRender()
        {
            // Smoking or burning pants seen from your own eyes: placed from the view's own body after every
            // LateUpdate, not from the model's hip bone, which follows the physics controller a frame out of step with
            // the camera and made the flames jitter against the view.
            if (_scorchFromView && _body != null && Refs.observerMirror != null)
            {
                Transform observer = Refs.observerMirror.transform;
                Vector3 back = observer.forward;
                back.y = 0f;
                back = back.sqrMagnitude > 1e-4f ? -back.normalized : Vector3.zero;
                _body.MoveScorch(observer.position + Vector3.down * 0.05f + back * 0.08f);
            }

            if (_restoreT != null) return;
            // Emitters left where last frame's drawn item held them go back on their stock local poses first, so this
            // frame's are read from the item as drawn now, and an item not drawn this frame emits from its own place.
            StockEmitters();
            try
            {
                if (_renderHeld == null || _body == null) return;
                Vector3 pos; Quaternion rot; Vector3 scaleMul;
                if (!_body.TryGetItemRenderPose(_renderHeld.position, _renderHeld.rotation, out pos, out rot, out scaleMul)) return;
                _restoreT = _renderHeld;
                _restorePos = _renderHeld.position;
                _restoreRot = _renderHeld.rotation;
                _restoreScale = _renderHeld.localScale;
                // A fishing rod's line hangs from its tip, which moves with the rod rather than with the item's origin.
                Transform rodTip, rodLine;
                bool rod = ItemPoses.RodLine(_renderHeld, out rodTip, out rodLine);
                Vector3 tipBefore = rod ? rodTip.position : Vector3.zero;
                _renderHeld.SetPositionAndRotation(pos, rot);
                if (scaleMul != Vector3.one) _renderHeld.localScale = Vector3.Scale(_restoreScale, scaleMul);
                ReadDrawnEmitters();
                if (rod) BendRopeEnd(rodLine, tipBefore, rodTip.position);
                else BendRopeEnd(_renderHeld, _restorePos, pos);
                if (!_restorePending)
                {
                    _restorePending = true;
                    StartCoroutine(RestoreAtEndOfFrame());
                }
            }
            finally
            {
                // Not drawn this frame, the emitters stay on their stock poses for the next particle update. Drawn, the
                // end of the frame leaves them and checks there.
                if (!_emittersDrawn) HoldInheritOnJumps();
            }
        }

        /// <summary>
        /// The game draws a rope's line from the rope item's position, worked out before this render-time move,
        /// so moving the item alone would leave a gap between the hand and the line. Shift the line's points
        /// toward the new end, fully at the item and not at all at the far attachment, which keeps the sag.
        /// Covers both the plain line and the cloth rope setting.
        /// </summary>
        private void BendRopeEnd(Transform item, Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            if (delta.sqrMagnitude < 1e-6f) return;

            var lr = item.GetComponent<LineRenderer>();
            if (lr != null && lr.enabled && lr.useWorldSpace && lr.positionCount >= 2)
            {
                int n = lr.positionCount;
                if (_lineSaved.Length != n) { _lineSaved = new Vector3[n]; _lineWork = new Vector3[n]; }
                lr.GetPositions(_lineSaved);
                bool itemFirst = (_lineSaved[0] - from).sqrMagnitude <= (_lineSaved[n - 1] - from).sqrMagnitude;
                for (int i = 0; i < n; i++) _lineWork[i] = _lineSaved[i] + delta * EndWeight(i, n, itemFirst);
                lr.SetPositions(_lineWork);
                _restoreLine = lr;
            }

            var effect = item.GetComponent<RopeEffect>();
            var cloth = effect != null ? ClothRopeRef(effect) : null;
            if (cloth != null && cloth.gameObject.activeInHierarchy && cloth.bones != null && cloth.bones.Length >= 2)
            {
                var bones = cloth.bones;
                int n = bones.Length;
                if (_bonesSaved.Length != n) _bonesSaved = new Vector3[n];
                for (int i = 0; i < n; i++) _bonesSaved[i] = bones[i] != null ? bones[i].position : Vector3.zero;
                bool itemFirst = (_bonesSaved[0] - from).sqrMagnitude <= (_bonesSaved[n - 1] - from).sqrMagnitude;
                // In chain order, each set to an absolute position, so a parent's move cannot drag its children.
                for (int i = 0; i < n; i++)
                    if (bones[i] != null) bones[i].position = _bonesSaved[i] + delta * EndWeight(i, n, itemFirst);
                _restoreBones = bones;
            }
        }

        private static float EndWeight(int i, int n, bool itemFirst)
        {
            float t = (float)i / (n - 1);
            if (itemFirst) t = 1f - t;
            return t * t * (3f - 2f * t);
        }

        private IEnumerator RestoreAtEndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            RestoreHeld();
        }

        private void RestoreHeld()
        {
            if (_restoreT != null)
            {
                _restoreT.SetPositionAndRotation(_restorePos, _restoreRot);
                _restoreT.localScale = _restoreScale;
            }
            if (_emittersDrawn) LeaveEmittersDrawn();
            if (_restoreLine != null && _restoreLine.positionCount == _lineSaved.Length) _restoreLine.SetPositions(_lineSaved);
            _restoreLine = null;
            if (_restoreBones != null)
            {
                for (int i = 0; i < _restoreBones.Length && i < _bonesSaved.Length; i++)
                    if (_restoreBones[i] != null) _restoreBones[i].position = _bonesSaved[i];
                _restoreBones = null;
            }
            _restoreT = null;
            _restorePending = false;
        }

        /// <summary>
        /// A skinned mesh takes its bones' places before OnBeforeRender, so a skinned mesh moved there, such as the
        /// fishing rod's, only shows moved with per-render skinning; without it the hands closed on a rod that was still
        /// drawn where the game floats it. That is an extra skinning pass per camera render, so it is kept on only for
        /// the item this body draws in the hands, and set here in LateUpdate, before this frame's renders.
        /// </summary>
        private void SyncRenderSkins(Transform item)
        {
            if ((object)item != (object)_forcedFor)
            {
                ReleaseRenderSkins();
                _forcedFor = item;
                if (item != null)
                {
                    item.GetComponentsInChildren(true, _forcedSkins);
                    for (int i = 0; i < _forcedSkins.Count; i++) _forcedSkins[i].forceMatrixRecalculationPerRender = true;
                }
            }
            SyncClothSkin(item);
        }

        private void ReleaseRenderSkins()
        {
            for (int i = 0; i < _forcedSkins.Count; i++)
                if (_forcedSkins[i] != null) _forcedSkins[i].forceMatrixRecalculationPerRender = false;
            _forcedSkins.Clear();
            _forcedFor = null;
            ReleaseClothSkin();
        }

        /// <summary>
        /// With the game's Cloth Ropes setting on, a rope is drawn by a ClothRope, whose bones BendRopeEnd moves at render
        /// time too. The cloth is not under the rope item (ClothRope parents itself to the RopeEffect's parent), and a
        /// fishing rod's cloth follows its line's RopeEffect rather than the rod, so neither is reliably among the item's
        /// own skinned meshes. RopeEffect destroys its cloth when the setting goes off and makes a new one when it comes
        /// back on, so the cloth is checked every frame.
        /// </summary>
        private void SyncClothSkin(Transform item)
        {
            if ((object)item != (object)_clothFor)
            {
                // Recorded before the lookup, so a lookup that throws is not retried every frame.
                _clothFor = item;
                _forcedRope = null;
                Transform tip, line;
                if (item != null) _forcedRope = (ItemPoses.RodLine(item, out tip, out line) ? line : item).GetComponent<RopeEffect>();
            }
            var cloth = _forcedRope != null ? ClothRopeRef(_forcedRope) : null;
            var skin = cloth != null ? cloth.skinned : null;
            if (skin != _forcedCloth)
            {
                if (_forcedCloth != null) _forcedCloth.forceMatrixRecalculationPerRender = false;
                if (skin != null) skin.forceMatrixRecalculationPerRender = true;
                _forcedCloth = skin;
            }
        }

        private void ReleaseClothSkin()
        {
            if (_forcedCloth != null) _forcedCloth.forceMatrixRecalculationPerRender = false;
            _forcedCloth = null;
            _forcedRope = null;
            _clothFor = null;
        }

        /// <summary>
        /// A particle system simulating in world space leaves each new particle where its emitter was at the particle
        /// update, which never sees the render-time move in OnBeforeRender. A lit pipe drawn in the hands therefore smoked
        /// from where the game floats it, level with the top of the head. So each such emitter
        /// on the drawn item is left, once the item is put back, where the drawn item holds it (see RestoreHeld), and the
        /// next particle update emits from the drawn bowl, a frame behind. Found again only when the drawn item changes,
        /// and set back on its stock local pose then, in any frame the item is not drawn, and on teardown.
        ///
        /// Only emitters that are not the item itself, carry no collider and have nothing under them: moving one moves
        /// only the particles' source, never anything the game's physics reads.
        ///
        /// The particle update that sees an emitter go onto the drawn spot or back off it sees a jump of about half a
        /// meter in one frame, which the pipe's smoke would inherit as velocity. See HoldInherit.
        /// </summary>
        private void SyncEmitters(Transform item)
        {
            if (_emittersFailed || (object)item == (object)_emittersFor) return;
            try
            {
                ReleaseEmitters();
                _emittersFor = item;
                if (item == null) return;
                item.GetComponentsInChildren(true, _emitterScan);
                for (int i = 0; i < _emitterScan.Count; i++)
                {
                    var ps = _emitterScan[i];
                    Transform t = ps.transform;
                    if (ps.main.simulationSpace != ParticleSystemSimulationSpace.World) continue;
                    if (t == item || t.childCount > 0 || t.GetComponent<Collider>() != null) continue;
                    _emitters.Add(new HeldEmitter { T = t, PS = ps, LocalPos = t.localPosition, LocalRot = t.localRotation });
                }
                _emitterScan.Clear();
            }
            catch (System.Exception e) { FailEmitters(e); }
        }

        /// <summary>
        /// Emitters back on their stock local poses, if they were left anywhere else. One whose local pose changed since it
        /// was left takes that as its stock pose instead, so nothing else that places it is undone.
        /// </summary>
        private void StockEmitters()
        {
            _emittersDrawn = false;
            if (!_emittersMoved) return;
            _emittersMoved = false;
            for (int i = 0; i < _emitters.Count; i++)
            {
                var em = _emitters[i];
                em.Left = false;
                try
                {
                    if (em.T == null) continue;
                    if (em.T.localPosition != em.LeftPos || em.T.localRotation != em.LeftRot)
                    {
                        em.LocalPos = em.T.localPosition;
                        em.LocalRot = em.T.localRotation;
                    }
                    em.T.localPosition = em.LocalPos;
                    em.T.localRotation = em.LocalRot;
                }
                catch { }
            }
        }

        /// <summary>Where the item, just moved to its drawn pose, holds each emitter. Runs with the emitters on their stock local poses.</summary>
        private void ReadDrawnEmitters()
        {
            if (_emittersFailed || _emitters.Count == 0 || (object)_emittersFor != (object)_renderHeld) return;
            try
            {
                for (int i = 0; i < _emitters.Count; i++)
                {
                    var em = _emitters[i];
                    if (em.T == null) continue;
                    em.DrawnPos = em.T.position;
                    em.DrawnRot = em.T.rotation;
                }
                _emittersDrawn = true;
            }
            catch (System.Exception e) { FailEmitters(e); }
        }

        /// <summary>With the item back where the game holds it, the emitters stay where the drawn item held them.</summary>
        private void LeaveEmittersDrawn()
        {
            _emittersDrawn = false;
            try
            {
                _emittersMoved = true;
                for (int i = 0; i < _emitters.Count; i++)
                {
                    var em = _emitters[i];
                    em.Left = false;
                    if (em.T == null) continue;
                    // Under an item shrunk to nothing (put in a crate or the inventory) no local pose reaches the drawn
                    // spot, so it stays on its stock pose.
                    Vector3 scale = em.T.parent != null ? em.T.parent.lossyScale : Vector3.one;
                    if (Mathf.Abs(scale.x * scale.y * scale.z) < 1e-9f) continue;
                    em.T.SetPositionAndRotation(em.DrawnPos, em.DrawnRot);
                    em.LeftPos = em.T.localPosition;
                    em.LeftRot = em.T.localRotation;
                    em.Left = true;
                }
                // The last move before the next particle update.
                HoldInheritOnJumps();
            }
            catch (System.Exception e) { FailEmitters(e); }
        }

        private void ReleaseEmitters()
        {
            // Held before the move back, so no particle update sees that move with inheritance on.
            for (int i = 0; i < _emitters.Count; i++)
                if (_emitters[i].SeenLeft) HoldInherit(_emitters[i].PS);
            StockEmitters();
            _emitters.Clear();
            _emitterScan.Clear();
            _emittersFor = null;
        }

        private void FailEmitters(System.Exception e)
        {
            _emittersFailed = true;
            ReleaseEmitters();
            RestoreInherit();
            Plugin.Log.LogWarning("[LocalBody] Smoke from an item in the hands is left where the game has it for this session: " + e.Message);
        }

        /// <summary>
        /// Runs after this frame's particle update. Each system held for that update gets its own setting back, and each
        /// emitter's place is noted as the one that update saw. A frame with no time step may run no particle update, so
        /// both wait for a frame that has one.
        /// </summary>
        private void NoteParticleUpdate()
        {
            if (Time.deltaTime <= 0f) return;
            RestoreInherit();
            for (int i = 0; i < _emitters.Count; i++) _emitters[i].SeenLeft = _emitters[i].Left;
        }

        /// <summary>Holds each emitter whose place for the next particle update differs from the place the last one saw.</summary>
        private void HoldInheritOnJumps()
        {
            for (int i = 0; i < _emitters.Count; i++)
                if (_emitters[i].Left != _emitters[i].SeenLeft) HoldInherit(_emitters[i].PS);
        }

        /// <summary>
        /// Turns off inherited velocity on a particle system for the one particle update that sees its emitter jump
        /// between where the game holds the item and where the body draws it. The pipe's smoke inherits its emitter's
        /// velocity at birth and nothing slows it, so a particle born on that update would fly off at tens of meters a
        /// second. Only a system whose own setting is on is changed, and NoteParticleUpdate turns it back on after that
        /// update. A system already held reads as off, so it is never recorded twice.
        /// </summary>
        private void HoldInherit(ParticleSystem ps)
        {
            if (_inheritFailed || ps == null) return;
            try
            {
                var inherit = ps.inheritVelocity;
                if (!inherit.enabled) return;
                inherit.enabled = false;
                _inheritHeld.Add(ps);
            }
            catch (System.Exception e) { FailInherit(e); }
        }

        private void RestoreInherit()
        {
            if (_inheritHeld.Count == 0) return;
            for (int i = 0; i < _inheritHeld.Count; i++)
            {
                var ps = _inheritHeld[i];
                if (ps == null) continue; // destroyed with its item
                try
                {
                    var inherit = ps.inheritVelocity;
                    inherit.enabled = true;
                }
                catch (System.Exception e) { FailInherit(e); }
            }
            _inheritHeld.Clear();
        }

        private void FailInherit(System.Exception e)
        {
            if (_inheritFailed) return;
            _inheritFailed = true;
            Plugin.Log.LogWarning("[LocalBody] Smoke from an item in the hands can streak off when the view changes, for this session: " + e.Message);
        }

        /// <summary>
        /// The game breathes pipe smoke out of one object on the view body, 1.08 m up and tipped 30 degrees up, which with
        /// this body drawn is inside the brow. While the body is seen from the game's camera the breath leaves the drawn
        /// lips instead, tipped up the same way from the face's heading. In first person it keeps the game's own place. The
        /// object is a child of the view body, so it rides any move of that before the next particle update.
        /// </summary>
        private void PlaceExhale(bool drawn)
        {
            if (_exhaleFailed) return;
            try
            {
                var effect = PipeExhaleEffect.instance;
                Transform t = effect != null ? effect.transform : null;
                if ((object)t != (object)_exhale)
                {
                    RestoreExhale();
                    _exhale = t;
                    if (t != null) { _exhaleLocalPos = t.localPosition; _exhaleLocalRot = t.localRotation; }
                }
                Vector3 mouth, facing;
                if (!drawn || _exhale == null || _body == null || !_body.TryGetMouth(out mouth, out facing))
                {
                    RestoreExhale();
                    return;
                }
                Vector3 heading = facing;
                heading.y = 0f;
                if (heading.sqrMagnitude < 1e-4f) heading = _root != null ? _root.transform.forward : Vector3.forward;
                _exhale.SetPositionAndRotation(mouth + facing * 0.02f, Quaternion.LookRotation(heading.normalized, Vector3.up) * _exhaleLocalRot);
                _exhaleMoved = true;
            }
            catch (System.Exception e)
            {
                _exhaleFailed = true;
                RestoreExhale();
                Plugin.Log.LogWarning("[LocalBody] The pipe exhale is left where the game has it for this session: " + e.Message);
            }
        }

        private void RestoreExhale()
        {
            if (!_exhaleMoved) return;
            _exhaleMoved = false;
            try
            {
                if (_exhale == null) return;
                _exhale.localPosition = _exhaleLocalPos;
                _exhale.localRotation = _exhaleLocalRot;
            }
            catch { }
        }

        private void TryBuild()
        {
            if (!BodyTemplate.Available) return; // no shopkeeper loaded yet (e.g. out at sea)

            _root = new GameObject("LocalPlayerBodyRoot");
            DontDestroyOnLoad(_root);

            // The root sits at the vanilla CharacterController origin, so the drop to the ground is
            // height/2 - center.y, which the game can be asked for directly rather than guessed at. A
            // hardcoded 0.9 used to stand in for it and planted the body wrong by however much that missed by.
            // Reading it live also survives the runtime height rescale PlayerEmbarkerNew applies on boarding.
            _body = SyntyBody.TryBuild(_root.transform, "your body", PlayerModel.LocalAppearance, 0, SoleDrop);
            if (_body == null) { Destroy(_root); _root = null; return; }

            _havePrev = false;
            _visible = true;
            _tagPlaced = false;
            BuildTag();
            Plugin.Log.LogInfo("[LocalBody] Built your third-person body");
        }

        private static float SoleDrop()
        {
            float gap = VanillaPlayer.ControllerFeetGap();
            return -(gap <= 0.01f ? 0.9f : gap); // controller not resolvable yet: keep the old constant
        }

        private void BuildTag()
        {
            string playerName = PlayerModel.LocalDisplayName;
            if (string.IsNullOrEmpty(playerName)) return; // solo: nobody needs a label over their own head
            if (_root == null) return;

            _tagObject = new GameObject("LocalNameTag");
            _tagObject.transform.SetParent(_root.transform);
            _tag = _tagObject.AddComponent<TextMesh>();
            _tag.text = playerName;
            _tag.fontSize = 32;
            _tag.characterSize = 0.04f;
            _tag.anchor = TextAnchor.MiddleCenter;
            _tag.alignment = TextAlignment.Center;
            _tag.color = Color.yellow;
            // Parked until the body's fit reports a real height; PlaceTag moves it then.
            _tagBaseLocalPos = new Vector3(0f, 1.1f, 0f);
            _tagObject.transform.localPosition = _tagBaseLocalPos;
            _tagPlaced = false;
        }

        private void PlaceTag()
        {
            if (_tagObject == null || _body == null || !_body.Fitted || _tagPlaced) return;
            _tagBaseLocalPos = new Vector3(0f, _body.FittedFeetLocalY + _body.MeasuredHeight + 0.25f, 0f);
            _tagObject.transform.localPosition = _tagBaseLocalPos;
            _tagPlaced = true;
        }

        private void Teardown()
        {
            _renderHeld = null;
            ReleaseRenderSkins();
            ReleaseEmitters();
            RestoreInherit();
            RestoreExhale();
            if (_body != null) _body.Destroy();
            _body = null;
            if (_root != null) Destroy(_root);
            _root = null;
            _tagObject = null;
            _tag = null;
            _havePrev = false;
            _tagPlaced = false;
        }

        private void SetVisible(bool on, bool tag)
        {
            PlaceTag();
            if (_tagObject != null && _tagObject.activeSelf != tag) _tagObject.SetActive(tag);
            if (on == _visible) return;
            _visible = on;
            if (_body != null) _body.SetRenderersEnabled(on);
        }

        /// <summary>Update the name shown over your own body (co-op sets this; solo leaves it empty).</summary>
        public void SetDisplayName(string playerName)
        {
            if (string.IsNullOrEmpty(playerName))
            {
                if (_tagObject != null) { Destroy(_tagObject); _tagObject = null; _tag = null; }
                return;
            }
            if (_tag != null) { _tag.text = playerName; return; }
            BuildTag();
        }
    }
}

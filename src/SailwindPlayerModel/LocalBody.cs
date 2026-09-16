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

        private void Awake() { Instance = this; }
        private void OnDestroy() { if (Instance == this) Instance = null; }
        private void OnEnable() { Application.onBeforeRender += OnBeforeRender; }
        private void OnDisable() { Application.onBeforeRender -= OnBeforeRender; RestoreHeld(); }

        private void LateUpdate()
        {
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
                _body.LookPitchDegTarget = VanillaPlayer.HeadLookPitchDeg();
                FeedRest();
                if (BodyTuning.SwimAnimation.Value && PlayerSwimming.swimming && !Downed.HoldsView)
                    _body.SetSwimming(!PlayerSwimming.swimmingOnSurface, _deckVelocity);
                bool visible = ForcedVisible || (BoatCamera.on && GameState.currentShipyard == null);
                // Sitting in first person: your own body from the chest down, fading in below the shoulders.
                bool chest = !visible && Seating.IsSeated && GameState.currentShipyard == null
                    && SeatingTuning.ShowBodyWhenSeated.Value && BodyShaders.SeatedFade != null;
                FeedInteraction(visible, chest);
                _body.Tick(Time.deltaTime);

                // After the pose, so the fade starts from where the shoulders are this frame.
                if (chest) _body.SetChestFade(BodyShaders.SeatedFade, SeatingTuning.BodyFadeBelowShoulders.Value, SeatingTuning.BodyFadeAboveHips.Value);
                else if (_body.ChestFadeOn) _body.ClearChestFade();
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
            catch (System.Exception e) { Plugin.Log.LogError("[LocalBody] " + e); }
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
                    _body.SetHeldItem(held.transform, null, held.transform.position, held.transform.rotation, held.big,
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

            if (_renderHeld == null || _body == null || _restoreT != null) return;
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
            if (rod) BendRopeEnd(rodLine, tipBefore, rodTip.position);
            else BendRopeEnd(_renderHeld, _restorePos, pos);
            if (!_restorePending)
            {
                _restorePending = true;
                StartCoroutine(RestoreAtEndOfFrame());
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

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

        private void Awake() { Instance = this; }
        private void OnDestroy() { if (Instance == this) Instance = null; }

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
                _body.Tick(Time.deltaTime);

                SetVisible(ForcedVisible || (BoatCamera.on && GameState.currentShipyard == null));

                if (_tagObject != null)
                {
                    // The tag is a SIBLING of the body, so it does not inherit the crouch drop and would hang
                    // at standing height over a crouched player. Keep it in step by hand.
                    _tagObject.transform.localPosition = _tagBaseLocalPos - _body.CrouchOffset;
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
            var src = onBoatVisual ? observer : cc.transform;
            _root.transform.position = src.position;
            _root.transform.rotation = Quaternion.Euler(0f, src.eulerAngles.y, 0f);
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
            _prevLocalPos = lp; _havePrev = true;
            return speed;
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

        private void SetVisible(bool on)
        {
            PlaceTag();
            if (on == _visible) return;
            _visible = on;
            if (_body != null) _body.SetRenderersEnabled(on);
            if (_tagObject != null) _tagObject.SetActive(on);
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

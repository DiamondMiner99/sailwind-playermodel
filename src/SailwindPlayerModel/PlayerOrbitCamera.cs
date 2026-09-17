using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>Live tuning for the third person camera that follows the player. Applies immediately from the F1 menu.</summary>
    public static class CameraTuning
    {
        private const string Section = "8. Camera";

        public static ConfigEntry<bool> FollowCamera { get; private set; }
        public static ConfigEntry<float> FollowDistance { get; private set; }

        public static void Bind(ConfigFile cfg)
        {
            FollowCamera = cfg.Bind(Section, "FollowCamera", true,
                "The camera key also gives a third person view that follows you, on land as well as aboard. Aboard it goes first person, following you, then the game's view of the whole ship, then back to first person.");
            FollowDistance = cfg.Bind(Section, "FollowDistance", 4f,
                new ConfigDescription("How far behind you the following camera starts, in meters. The scroll wheel moves it closer or farther.", new AcceptableValueRange<float>(1.2f, 12f)));
        }
    }

    /// <summary>
    /// A third person camera that follows the player, built on the game's own ship camera.
    ///
    /// BoatCamera (the camera key) orbits the boat, 8 to 40 m out, and switches itself off the moment the player is
    /// not aboard one. This takes over its Update: the camera key cycles first person, following the player, and
    /// (aboard) the game's ship view. Following the player reuses everything BoatCamera.SwitchOn sets up (the eye
    /// camera on its orbit rig, mouse look moved to the rig, the pointer moved to the body, the body shown), and only
    /// the rig's position and the eye's distance differ. The ship view is left entirely to the game.
    ///
    /// The game stops turning the player with the mouse while its camera is up, which is fine for watching a ship
    /// and useless for walking, so while following, moving turns the player to face the way the camera looks.
    /// </summary>
    [HarmonyPatch(typeof(BoatCamera), "Update")]
    internal static class PlayerOrbitCamera
    {
        private enum Mode { FirstPerson, Following, Ship }

        private static Mode _mode = Mode.FirstPerson;
        private static float _distance = -1f;
        private static bool _renderHooked;

        private const float PivotAboveBody = 0.65f;     // the observer is at the controller's middle; this is about the head
        private const float EyeAbovePivot = 0.25f;
        private const float MinDistance = 1.2f, MaxDistance = 12f;
        private const float TurnDegreesPerSecond = 720f;
        private const int UiLayer = 5, InvisLayer = 16;

        private static readonly AccessTools.FieldRef<BoatCamera, Transform> CenterEyeRef =
            AccessTools.FieldRefAccess<BoatCamera, Transform>("centerEye");
        private static readonly AccessTools.FieldRef<MouseLook, float> PitchRef =
            AccessTools.FieldRefAccess<MouseLook, float>("rotationY");

        /// <summary>True while the camera is following the local player in third person.</summary>
        public static bool Following { get { return BoatCamera.on && _mode == Mode.Following; } }

        private static bool Prefix(BoatCamera __instance)
        {
            try
            {
                // The shipyard drives this camera itself, and switched off this is the game's camera as it was.
                if (CameraTuning.FollowCamera == null || !CameraTuning.FollowCamera.Value || GameState.currentShipyard != null)
                {
                    _mode = BoatCamera.on ? Mode.Ship : Mode.FirstPerson;
                    return true;
                }
                if (!_renderHooked)
                {
                    Application.onBeforeRender += PlaceBeforeRender;
                    _renderHooked = true;
                }

                bool aboard = GameState.currentBoat != null;
                bool pressed = GameInput.GetKeyDown(InputName.CameraMode);
                if (pressed) Cycle(__instance, aboard);
                else if (BoatCamera.on && _mode == Mode.FirstPerson) _mode = aboard ? Mode.Ship : Mode.Following;   // switched on by something else
                else if (BoatCamera.on && _mode == Mode.Ship && !aboard) _mode = Mode.Following;                 // stepped ashore in the ship view

                if (!BoatCamera.on) { _mode = Mode.FirstPerson; return false; }
                // The game places its ship view. Not on the frame the key was pressed: it would read the key too.
                if (_mode == Mode.Ship) return !pressed;

                if (_distance < 0f) _distance = CameraTuning.FollowDistance.Value;
                _distance = Mathf.Clamp(_distance - GameInput.GetScrollAxis() * 5f, MinDistance, MaxDistance);
                TurnPlayerToCamera(__instance);
                Place(__instance);
                return false;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("[Camera] " + e);
                return true;
            }
        }

        private static void Cycle(BoatCamera cam, bool aboard)
        {
            if (!BoatCamera.on)
            {
                cam.SwitchOn();
                _mode = Mode.Following;
                _distance = CameraTuning.FollowDistance.Value;
                StartBehindPlayer(cam);
            }
            else if (_mode == Mode.Following && aboard)
            {
                _mode = Mode.Ship;
                if (UISoundPlayer.instance != null) UISoundPlayer.instance.PlayUISound(UISounds.buttonClick, 1f, 1.3f);
            }
            else
            {
                cam.SwitchOff();
                _mode = Mode.FirstPerson;
            }
        }

        /// <summary>Turn the camera rig to look over the player's shoulder, a little down, as following starts.</summary>
        private static void StartBehindPlayer(BoatCamera cam)
        {
            if (Refs.observerMirror == null) return;
            Vector3 facing = Refs.observerMirror.transform.forward;
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-4f) return;
            Transform rig = cam.transform;
            Vector3 local = rig.parent != null ? rig.parent.InverseTransformDirection(facing) : facing;
            const float lookDown = 12f;
            // MouseLook (MouseXAndY) keeps the yaw on the transform and the pitch in its own field.
            var look = cam.GetComponent<MouseLook>();
            if (look != null) PitchRef(look) = -lookDown;
            rig.localEulerAngles = new Vector3(lookDown, Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, 0f);
        }

        /// <summary>While a movement key is held, turn the player to face where the camera looks, so walking goes that way.</summary>
        private static void TurnPlayerToCamera(BoatCamera cam)
        {
            if (Refs.observerMirror == null || Refs.charController == null || !Refs.charController.enabled) return;
            if (!(GameInput.GetKey(InputName.MoveUp) || GameInput.GetKey(InputName.MoveDown) ||
                  GameInput.GetKey(InputName.MoveLeft) || GameInput.GetKey(InputName.MoveRight))) return;
            Vector3 look = cam.transform.forward;
            look.y = 0f;
            if (look.sqrMagnitude < 1e-4f) return;
            // The observer's turn is copied onto the physics controller by PlayerControllerMirror, and the controller
            // walks the way it faces.
            Transform observer = Refs.observerMirror.transform;
            Vector3 local = observer.parent != null ? observer.parent.InverseTransformDirection(look) : look;
            float target = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            Vector3 e = observer.localEulerAngles;
            observer.localEulerAngles = new Vector3(e.x, Mathf.MoveTowardsAngle(e.y, target, TurnDegreesPerSecond * Time.deltaTime), e.z);
        }

        private static void PlaceBeforeRender()
        {
            try
            {
                if (Following && BoatCamera.instance != null && GameState.currentShipyard == null) Place(BoatCamera.instance);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("[Camera] " + e);
                Application.onBeforeRender -= PlaceBeforeRender;
                _renderHooked = false;
            }
        }

        /// <summary>
        /// The rig just above the player's head, the eye behind it along the look, pulled in so nothing solid comes
        /// between them, and kept out of the water unless the player is in it. Run in Update, and again just before
        /// rendering so the camera is where the player ended up after this frame's movement.
        /// </summary>
        private static void Place(BoatCamera cam)
        {
            if (Refs.observerMirror == null) return;
            Transform eye = CenterEyeRef(cam);
            if (eye == null || cam.transform.childCount == 0) return;
            Transform rig = cam.transform, pitch = rig.GetChild(0);

            Vector3 pivot = Refs.observerMirror.transform.position + Vector3.up * PivotAboveBody;
            rig.position = pivot;
            Vector3 from = pitch.TransformPoint(new Vector3(0f, EyeAbovePivot, 0f));
            Vector3 want = pitch.TransformPoint(new Vector3(0f, EyeAbovePivot, -Mathf.Max(_distance, MinDistance)));
            want = from + (want - from).normalized * ClearDistance(from, want, rig);
            if (!PlayerSwimming.swimming)
            {
                float water = PlayerSwimming.cameraWaterHeight + 0.3f;
                if (want.y < water && pivot.y > water) want.y = water;
            }
            eye.position = want;
        }

        private static readonly RaycastHit[] _hits = new RaycastHit[24];

        /// <summary>How far the eye can go from <paramref name="from"/> toward <paramref name="to"/> before something solid, in both copies of a boat.</summary>
        private static float ClearDistance(Vector3 from, Vector3 to, Transform rig)
        {
            Vector3 d = to - from;
            float length = d.magnitude;
            if (length < 1e-3f) return 0f;
            Vector3 dir = d / length;
            float clear = Cast(from, dir, length, false, rig);
            Transform visual = Refs.observerMirror.transform.parent;
            Transform walk = Refs.charController != null ? Refs.charController.transform.parent : null;
            if (visual != null && walk != null && visual != walk && GameState.currentBoat != null)
            {
                Vector3 wFrom = walk.TransformPoint(visual.InverseTransformPoint(from));
                Vector3 wDir = walk.TransformDirection(visual.InverseTransformDirection(dir));
                clear = Mathf.Min(clear, Cast(wFrom, wDir, length, true, rig));
            }
            return Mathf.Max(0.3f, clear);
        }

        private static float Cast(Vector3 from, Vector3 dir, float length, bool walkCopy, Transform rig)
        {
            const float radius = 0.2f, margin = 0.08f;
            // Docks and some other scenery sit on Ignore Raycast (see Seating.SeatLayers). The UI layer and the layer the game
            // puts stowed inventory items on are left out: the inventory and needs panels hang off the camera, and counting
            // them pulled the camera in, which moved the panels in with it, over and over, into a zoom that never stopped.
            int n = Physics.SphereCastNonAlloc(from, radius, dir, _hits, length, Seating.SeatLayers & ~((1 << UiLayer) | (1 << InvisLayer)), QueryTriggerInteraction.Ignore);
            float best = length;
            Transform walkRoot = walkCopy && Refs.charController != null ? Refs.charController.transform.parent : null;
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i].collider;
                if (c == null || _hits[i].distance <= 0f) continue;
                if (c.gameObject.layer == 2 && !Seating.IsStaticScenery(c)) continue;
                if (Refs.charController != null && c.transform.IsChildOf(Refs.charController.transform)) continue;
                if (c.transform.IsChildOf(Refs.observerMirror.transform)) continue;
                if (rig != null && c.transform.IsChildOf(rig)) continue;   // anything riding along with the camera
                if (walkCopy && (walkRoot == null || !c.transform.IsChildOf(walkRoot))) continue;
                best = Mathf.Min(best, _hits[i].distance - margin);
            }
            return best;
        }
    }
}

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
        public static ConfigEntry<KeyboardShortcut> FollowKey { get; private set; }
        public static ConfigEntry<KeyCode> FollowButton { get; private set; }
        public static ConfigEntry<bool> CameraKeyFollows { get; private set; }
        public static ConfigEntry<bool> LookFollowsCamera { get; private set; }
        public static ConfigEntry<KeyboardShortcut> LookLockKey { get; private set; }
        public static ConfigEntry<KeyCode> LookLockButton { get; private set; }
        public static ConfigEntry<bool> ScrollZoom { get; private set; }
        public static ConfigEntry<KeyboardShortcut> ZoomInKey { get; private set; }
        public static ConfigEntry<KeyboardShortcut> ZoomOutKey { get; private set; }

        public static void Bind(ConfigFile cfg)
        {
            FollowCamera = cfg.Bind(Section, "FollowCamera", true,
                "A third person view that follows you, on land as well as aboard. FollowKey switches between it and first person, and CameraKeyFollows makes the camera key go through it too. While this is on, bottles, mugs, food and the pipe are eaten, drunk and smoked in this view and in the game's ship view, and up and down in both views follow the game's Invert Mouse setting. An item you tip to pour instead, such as a soup pot or a bucket, is left to first person: these views judge the tip against the way you were looking when the view opened. Off, FollowKey and FollowButton do nothing and the camera key works as in the game.");
            FollowDistance = cfg.Bind(Section, "FollowDistance", 4f,
                new ConfigDescription("How far behind you the following camera starts, in meters. Changing it while the camera follows you moves it there. The scroll wheel moves it closer or farther while your hands are empty and nothing else has taken the mouse (see ScrollZoom), and ZoomInKey and ZoomOutKey move it at any time.", new AcceptableValueRange<float>(1.2f, 12f)));
            FollowDistance.SettingChanged += (s, e) => PlayerOrbitCamera.ApplyFollowDistance();
            FollowKey = cfg.Bind(Section, "FollowKey", new KeyboardShortcut(KeyCode.V),
                "Switches between first person and the third person view that follows you, on land as well as aboard. From the game's ship view it switches to following you. It works while you hold other keys, such as the movement keys. Set to the game's camera key, that key goes through the follow view as CameraKeyFollows describes. Does nothing while FollowCamera is off or in the shipyard.");
            FollowButton = cfg.Bind(Section, "FollowButton", KeyCode.None,
                "A gamepad button that does what FollowKey does, read while the game's controller setting is on. JoystickButton8, the left stick click on most gamepads, is not bound in the game's default controls. While this is None, the gamepad's camera button goes through the follow view: first person, following you, then aboard the game's ship view. Once it is set, the gamepad's camera button works like the camera key.");
            CameraKeyFollows = cfg.Bind(Section, "CameraKeyFollows", false,
                "The game's camera key also goes through the follow view: first person, following you, then aboard the game's ship view, then back to first person. Stepping ashore in the ship view switches to following you. Off, the camera key works as in the game: aboard it switches between first person and the ship view, and stepping ashore in the ship view returns to first person. From the follow view it goes to the ship view, and ashore it does nothing.");
            LookFollowsCamera = cfg.Bind(Section, "LookFollowsCamera", true,
                "While the camera follows you, the camera orbits around you when you stand still, and your body turns to where it looks when you move, throw or drop something, so you walk, strafe and back up from the camera. Off, your body holds the way it faces even while you walk. LookLockKey switches it while the view is up, and the view starts from this setting every time, so a change here takes effect the next time you open the view.");
            LookLockKey = cfg.Bind(Section, "LookLockKey", KeyboardShortcut.Empty,
                "Freezes which way your body faces and where it looks while the view follows you, so the movement keys work from the frozen facing. A click says which way it went: low for frozen, high for following the camera. It works while you hold other keys, such as the movement keys. Unbound by default. Does nothing in first person, in the game's ship view, in the shipyard or while FollowCamera is off.");
            LookLockButton = cfg.Bind(Section, "LookLockButton", KeyCode.None,
                "A gamepad button that does what LookLockKey does, read while the game's controller setting is on. While this is None, the look lock is switched from the keyboard only.");
            ScrollZoom = cfg.Bind(Section, "ScrollZoom", true,
                "The scroll wheel moves the following camera closer or farther while your hands are empty. With the game's controller setting on, the triggers do the same. Off, the wheel and the triggers leave the camera where it is, and ZoomInKey and ZoomOutKey are the only way to move it. Held items keep the wheel either way, and the game's ship view zooms as in the game.");
            ZoomInKey = cfg.Bind(Section, "ZoomInKey", KeyboardShortcut.Empty,
                "Held down, moves the following camera closer. Works with your hands full, unlike the scroll wheel. Unbound by default.");
            ZoomOutKey = cfg.Bind(Section, "ZoomOutKey", KeyboardShortcut.Empty,
                "Held down, moves the following camera farther away. Works with your hands full, unlike the scroll wheel. Unbound by default.");
        }
    }

    /// <summary>
    /// A third person camera that follows the player, built on the game's own ship camera.
    ///
    /// BoatCamera (the camera key) orbits the boat, 8 to 40 m out, and switches itself off the moment the player is
    /// not aboard one. This takes over its Update: the follow key switches between first person and following the
    /// player, and the camera key keeps the game's ship view (or, with CameraKeyFollows, cycles first person, following
    /// the player, and aboard the ship view). Following the player reuses everything BoatCamera.SwitchOn sets up (the
    /// eye camera on its orbit rig, mouse look moved to the rig, the pointer moved to the body, the body shown), and only
    /// the rig's position and the eye's distance differ. The game places its own ship view; the two views share one
    /// rig, so in both the player's own look stays off and up and down follow the game's Invert Mouse. In both, the
    /// game's mouth trigger rides the pointer (see <see cref="MouthOnPointer"/>).
    ///
    /// The game stops turning the player with the mouse while its camera is up, which is fine for watching a ship
    /// and useless for walking, so while following, moving turns the player to face the way the camera looks. The turn
    /// key switches that off for the view that is up, and the player then walks, strafes and backs up along the way the
    /// body faces, which is how a shot is lined up.
    /// </summary>
    [HarmonyPatch(typeof(BoatCamera), "Update")]
    internal static class PlayerOrbitCamera
    {
        private enum Mode { FirstPerson, Following, Ship }

        // What a key did this frame: the follow key or button, the camera key going through the follow view, or the
        // camera key doing what it does in the game.
        private enum Press { None, Follow, Cycle, Stock }

        private static Mode _mode = Mode.FirstPerson;
        private static float _distance = -1f;
        private static bool _renderHooked;
        // A fault is logged once rather than once a frame, and a render time fault is not hooked again.
        private static bool _prefixLogged, _renderLogged, _renderFailed;

        // Whether moving stops turning the player to the camera, and whether that answer was taken for the view now up.
        private static bool _lookLocked;
        private static bool _bodyFollowing;
        private static bool _lookLockSet;

        private const float PivotAboveBody = 0.65f;     // the observer is at the controller's middle; this is about the head
        private const float CapsuleTopAboveBody = 0.35f; // center of the top of the controller's capsule (1.1 m tall, 0.2 m radius)
        private const float EyeAbovePivot = 0.25f;
        private const float MinDistance = 1.2f, MaxDistance = 12f;
        private const float TurnDegreesPerSecond = 720f;
        private const float KeyZoomMetersPerSecond = 4f;
        private const int UiLayer = 5, InvisLayer = 16;

        private static readonly AccessTools.FieldRef<BoatCamera, Transform> CenterEyeRef =
            AccessTools.FieldRefAccess<BoatCamera, Transform>("centerEye");
        private static readonly AccessTools.FieldRef<MouseLook, float> PitchRef =
            AccessTools.FieldRefAccess<MouseLook, float>("rotationY");
        private static readonly AccessTools.FieldRef<BoatCamera, MouseLook[]> PlayerLooksRef =
            AccessTools.FieldRefAccess<BoatCamera, MouseLook[]>("playerLooks");

        // The rig's own look (up and down in the follow and ship views), its scene up and down speed, and whether it is
        // currently turned around to match the game's Invert Mouse. The head's look is the one that setting writes.
        private static MouseLook _orbitLook, _headLook;
        private static float _orbitSensY;
        private static bool _orbitFlipped;

        /// <summary>True while the camera is following the local player in third person.</summary>
        public static bool Following { get { return BoatCamera.on && _mode == Mode.Following; } }

        /// <summary>
        /// The body holds the way it faces and where it looks while the camera orbits: standing still, or with the look
        /// lock on.
        /// </summary>
        internal static bool BodyHolds { get { return Following && !_bodyFollowing; } }

        /// <summary>FollowDistance was changed (F1 or a config reload): the camera takes the new distance.</summary>
        internal static void ApplyFollowDistance()
        {
            if (CameraTuning.FollowDistance != null) _distance = CameraTuning.FollowDistance.Value;
        }

        private static bool Prefix(BoatCamera __instance)
        {
            try
            {
                // Before the early returns, so switched off and in the shipyard the mouth trigger is back at the eye.
                MouthOnPointer.Sync(MouthRidesPointer());
                // Before any settings click can write it, so this is the scene's speed.
                if (_orbitLook == null)
                {
                    _orbitLook = __instance.GetComponent<MouseLook>();
                    _orbitFlipped = false;
                    if (_orbitLook != null) _orbitSensY = Mathf.Abs(_orbitLook.sensitivityY);
                }
                // The turn lock belongs to the view that is up, read here from where the last frame left it. However
                // that view ended, a key, a switch from elsewhere, the shipyard or FollowCamera going off, the next
                // one starts from the LookFollowsCamera setting again.
                if (!Following) _lookLockSet = false;
                // The shipyard drives this camera itself, and switched off this is the game's camera as it was.
                if (CameraTuning.FollowCamera == null || !CameraTuning.FollowCamera.Value || GameState.currentShipyard != null)
                {
                    _mode = BoatCamera.on ? Mode.Ship : Mode.FirstPerson;
                    RestoreOrbitLook();
                    ResetEyeTurn(__instance);
                    return true;
                }
                // A fault in the render time placement takes the hook off for the session instead of being put back
                // on, and logged again, every frame. Update still places the camera; only the catch up after this
                // frame's movement is lost.
                if (!_renderHooked && !_renderFailed)
                {
                    Application.onBeforeRender += PlaceBeforeRender;
                    _renderHooked = true;
                }

                bool aboard = GameState.currentBoat != null;
                bool keyDown = GameInput.GetKeyDown(InputName.CameraMode);
                // Not on the title screen, in the intros, on the "press F" screen after loading (justStarted is already
                // set there) or in a menu other than the inventory: the view would leave where those screens are placed.
                bool inPlay = GameState.playing && !GameState.currentlyLoading && !GameState.justStarted && !MenuBlocksCameraKey();
                switch (inPlay ? ReadPress(keyDown) : Press.None)
                {
                    case Press.Follow:
                        ToggleFollow(__instance);
                        break;
                    case Press.Cycle:
                        Cycle(__instance, aboard);
                        break;
                    case Press.Stock:
                        if (BoatCamera.on) StockCameraKey(__instance, aboard);
                        else if (aboard)
                        {
                            // The game's own Update switches its ship view on and places it this frame, as without the
                            // mod. The mouth trigger goes out with the eye there, and PlaceBeforeRender puts it back on
                            // the pointer before the next physics step.
                            _mode = Mode.Ship;
                            MatchGameInvert();
                            return true;
                        }
                        // Ashore the game's key switches the ship view on and straight back off; here it does nothing.
                        break;
                }
                if (BoatCamera.on && _mode == Mode.FirstPerson) _mode = aboard ? Mode.Ship : Mode.Following;   // switched on by something else
                else if (BoatCamera.on && _mode == Mode.Ship && !aboard)
                {
                    // Stepped ashore in the ship view. The game drops to first person; a camera key that goes through
                    // the follow view follows instead.
                    if (CameraKeyCycles()) _mode = Mode.Following;
                    else
                    {
                        __instance.SwitchOff();
                        _mode = Mode.FirstPerson;
                    }
                }
                // After any switch and before Place moves the eye: SwitchOn sends the mouth trigger out with the eye, and
                // on the pointer again in the same frame it never leaves what the pointer holds.
                MouthOnPointer.Sync(MouthRidesPointer());

                if (!BoatCamera.on) { _mode = Mode.FirstPerson; return false; }
                // Following and the game's ship view share this rig and its look. In both, the player's own look stays
                // off and up and down follows the game's Invert Mouse.
                KeepPlayerLooksOff(__instance);
                MatchGameInvert();
                // The game places its ship view. Not on a frame the key went down, even one ignored above: the game
                // would read the key too and switch the view off.
                if (_mode == Mode.Ship) { ResetEyeTurn(__instance); return !keyDown; }

                if (_distance < 0f) _distance = CameraTuning.FollowDistance.Value;
                if (CameraTuning.ScrollZoom.Value && ZoomInputFree()) _distance = Mathf.Clamp(_distance - GameInput.GetScrollAxis() * 5f, MinDistance, MaxDistance);
                if (inPlay) _distance = Mathf.Clamp(_distance + ZoomKeysAxis() * KeyZoomMetersPerSecond * Time.unscaledDeltaTime, MinDistance, MaxDistance);
                // Not on the frame the view starts, so a turn key also bound to a key that opens the view does not
                // switch the lock as it opens.
                if (!_lookLockSet)
                {
                    _lookLockSet = true;
                    _lookLocked = !CameraTuning.LookFollowsCamera.Value;
                }
                else if (inPlay && LookLockPressed()) ToggleLookLock();
                _bodyFollowing = !_lookLocked && WantsToFaceCamera();
                if (_bodyFollowing) TurnPlayerToCamera(__instance);
                Place(__instance);
                return false;
            }
            catch (System.Exception e)
            {
                if (!_prefixLogged)
                {
                    _prefixLogged = true;
                    Plugin.Log.LogError("[Camera] " + e);
                }
                return true;
            }
        }

        // The F1 settings window, where the press may be setting a key, over the game or over a menu. It frees the cursor
        // without telling the game and can leave it free after it closes, so the window itself is asked, not the cursor.
        // The Tab inventory and cargo view hang off the eye and keep working in every view, so the key still switches
        // under them, as in the stock game. The pause closes them as it opens. wasInSettingsMenu (set from the pause until
        // the frame it closes) covers the pause that PauseMenuPatches shows again without running the game's, which
        // leaves an inventory opened during the pause up.
        private static bool MenuBlocksCameraKey()
        {
            if (ConfigWindowOpen()) return true;
            if (!GameState.inCursorMenu) return false;
            var needs = PlayerNeedsUI.instance;
            return needs == null || !needs.IsActive() || GameState.wasInSettingsMenu;
        }

        private const string ConfigManagerGuid = "com.bepis.bepinex.configurationmanager";
        private static bool _configManagerLooked;
        private static System.Func<bool> _configWindowShown;

        /// <summary>
        /// Whether ConfigurationManager's F1 window is up, read from its public DisplayingWindow property. Looked up once,
        /// in play, when every plugin is loaded. False without ConfigurationManager, and from the first failed read on.
        /// </summary>
        private static bool ConfigWindowOpen()
        {
            if (!_configManagerLooked)
            {
                _configManagerLooked = true;
                try
                {
                    BepInEx.PluginInfo info;
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(ConfigManagerGuid, out info) && info != null && info.Instance != null)
                    {
                        var property = info.Instance.GetType().GetProperty("DisplayingWindow",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        var getter = property != null && property.PropertyType == typeof(bool) ? property.GetGetMethod() : null;
                        if (getter != null)
                            _configWindowShown = (System.Func<bool>)System.Delegate.CreateDelegate(typeof(System.Func<bool>), info.Instance, getter);
                        else
                            Plugin.Log.LogWarning("[Camera] ConfigurationManager has no DisplayingWindow; the camera keys are not held while F1 is open");
                    }
                }
                catch (System.Exception e)
                {
                    _configWindowShown = null;
                    Plugin.Log.LogWarning("[Camera] ConfigurationManager lookup failed; the camera keys are not held while F1 is open: " + e.Message);
                }
            }
            if (_configWindowShown == null) return false;
            try
            {
                return _configWindowShown();
            }
            catch (System.Exception e)
            {
                _configWindowShown = null;   // stop asking rather than failing every frame
                Plugin.Log.LogWarning("[Camera] reading ConfigurationManager's window failed; the camera keys are not held while F1 is open: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Which key went down this frame and what it does. The game reads its camera key from the keyboard, the second
        /// key and the gamepad at once, so the source is told apart here from each binding.
        /// </summary>
        private static Press ReadPress(bool cameraKeyDown)
        {
            KeyboardShortcut shortcut = CameraTuning.FollowKey.Value;
            KeyCode main = shortcut.MainKey, button = CameraTuning.FollowButton.Value;
            bool pad = GameInput.controllerEnabled;
            bool followKeyDown = main != KeyCode.None && Input.GetKeyDown(main);
            bool followButtonDown = pad && button != KeyCode.None && Input.GetKeyDown(button);
            if (!followKeyDown && !followButtonDown && !cameraKeyDown) return Press.None;

            KeyCode padCamera = pad ? GameInput.GetControllerKeyCode(InputName.CameraMode) : KeyCode.None;
            bool keyIsCameraKey = FollowKeyIsCameraKey(main);
            // A follow key or button that is also a camera binding is left to the camera key, so one press is handled once.
            if (followKeyDown && !keyIsCameraKey && main != padCamera && ModifiersHeld(shortcut)) return Press.Follow;
            bool buttonSet = button != KeyCode.None && button != padCamera;
            if (followButtonDown && buttonSet) return Press.Follow;
            if (!cameraKeyDown) return Press.None;

            if (Input.GetKeyDown(GameInput.GetKeyCode(InputName.CameraMode, false, false)) ||
                Input.GetKeyDown(GameInput.GetKeyCode(InputName.CameraMode, true, false)))
                return CameraTuning.CameraKeyFollows.Value || keyIsCameraKey ? Press.Cycle : Press.Stock;
            // The gamepad's camera button, the only other source. It keeps going through the follow view until
            // FollowButton gives that view a button of its own.
            return buttonSet && !CameraTuning.CameraKeyFollows.Value ? Press.Stock : Press.Cycle;
        }

        // KeyboardShortcut.IsDown refuses a press while any key outside the shortcut is held, which walking always does.
        // Only the shortcut's own modifiers are checked here.
        private static bool ModifiersHeld(KeyboardShortcut shortcut)
        {
            foreach (KeyCode k in shortcut.Modifiers)
                if (!Input.GetKey(k)) return false;
            return true;
        }

        private static bool FollowKeyIsCameraKey(KeyCode main)
        {
            return main != KeyCode.None && (main == GameInput.GetKeyCode(InputName.CameraMode, false, false) ||
                                            main == GameInput.GetKeyCode(InputName.CameraMode, true, false));
        }

        /// <summary>Whether the keyboard's camera key goes through the follow view: CameraKeyFollows, or the follow key set to it.</summary>
        private static bool CameraKeyCycles()
        {
            return CameraTuning.CameraKeyFollows.Value || FollowKeyIsCameraKey(CameraTuning.FollowKey.Value.MainKey);
        }

        // The mouth trigger rides the pointer in both of this camera's views while FollowCamera is on. Switched off and in
        // the shipyard it stays at the eye, as in the game.
        private static bool MouthRidesPointer()
        {
            return BoatCamera.on && CameraTuning.FollowCamera != null && CameraTuning.FollowCamera.Value && GameState.currentShipyard == null;
        }

        // The wheel belongs to whatever else is using it: menus (the pause menu and the character screen set
        // inCursorMenu, the F1 window is asked for itself), the game's own holds on the look (a wheel, a winch, the
        // chart table, sleep) and a held item, which the game turns, reels or zooms with it. The cursor itself is not
        // asked: the F1 window frees it without telling the game and can leave it free after it closes, which left the
        // zoom dead until a game menu was opened and closed again.
        private static bool ZoomInputFree()
        {
            if (GameState.inCursorMenu || ConfigWindowOpen() || !MouseLook.MouseLookIsEnabled()) return false;
            var p = LocalInteraction.Pointer;
            return p == null || p.GetHeldItem() == null;
        }

        // The zoom keys: -1 closer, +1 farther, 0 neither or both. Not while a menu or the F1 window is up, where the keys
        // may be typing. A held item does not stop them; they are there for when the wheel is taken. Unscaled time, so the
        // speed holds during the sleep warp.
        private static float ZoomKeysAxis()
        {
            if (GameState.inCursorMenu || ConfigWindowOpen()) return 0f;
            float axis = 0f;
            if (ZoomKeyHeld(CameraTuning.ZoomInKey.Value)) axis -= 1f;
            if (ZoomKeyHeld(CameraTuning.ZoomOutKey.Value)) axis += 1f;
            return axis;
        }

        // Held with its own modifiers, whatever else is held: zooming while walking is the point.
        private static bool ZoomKeyHeld(KeyboardShortcut shortcut)
        {
            return shortcut.MainKey != KeyCode.None && Input.GetKey(shortcut.MainKey) && ModifiersHeld(shortcut);
        }

        // SwitchOn turns the player's own look off and SwitchOff turns it back on, but getting out of bed turns it on in
        // between, and the body then turned with every orbit of the camera.
        private static void KeepPlayerLooksOff(BoatCamera cam)
        {
            var looks = PlayerLooksRef(cam);
            if (looks == null) return;
            for (int i = 0; i < looks.Length; i++)
                if (looks[i] != null && looks[i].enabled) looks[i].enabled = false;
        }

        // The game's Invert Mouse checkbox sets the sign of the head's up and down look, at load and on every click, and
        // never touches the rig's. Match the rig to it while the view is up.
        private static bool GameInvertsMouse()
        {
            if (_headLook == null && Refs.ovrCameraRig != null) _headLook = Refs.ovrCameraRig.GetComponent<MouseLook>();
            return _headLook != null && _headLook.sensitivityY < 0f;
        }

        private static void MatchGameInvert()
        {
            if (_orbitLook == null || _orbitSensY <= 0f) return;
            bool inverted = GameInvertsMouse();
            float want = inverted ? -_orbitSensY : _orbitSensY;
            if (_orbitLook.sensitivityY != want) _orbitLook.sensitivityY = want;
            _orbitFlipped = inverted;
        }

        // FollowCamera off and the shipyard get the game's own rig look back.
        private static void RestoreOrbitLook()
        {
            if (!_orbitFlipped) return;
            if (_orbitLook != null) _orbitLook.sensitivityY = _orbitSensY;
            _orbitFlipped = false;
        }

        /// <summary>The camera key with CameraKeyFollows: first person, following the player, aboard the ship view, and back.</summary>
        private static void Cycle(BoatCamera cam, bool aboard)
        {
            if (!BoatCamera.on) StartFollowing(cam);
            else if (_mode == Mode.Following && aboard) ToShipView();
            else
            {
                cam.SwitchOff();
                _mode = Mode.FirstPerson;
            }
        }

        /// <summary>The follow key: following the player from first person or the ship view, first person from following.</summary>
        private static void ToggleFollow(BoatCamera cam)
        {
            if (BoatCamera.on && _mode == Mode.Following)
            {
                cam.SwitchOff();
                _mode = Mode.FirstPerson;
            }
            else StartFollowing(cam);
        }

        /// <summary>
        /// The camera key as in the game, with the view up: first person from the ship view, and the ship view from
        /// following aboard. Switching the ship view on from first person is left to the game's own Update.
        /// </summary>
        private static void StockCameraKey(BoatCamera cam, bool aboard)
        {
            if (_mode == Mode.Following)
            {
                if (aboard) ToShipView();
                // Ashore this key does nothing, as CameraKeyFollows describes, unless nothing else can leave the view:
                // with the follow key cleared and no gamepad button for it, this is the only way back to first person.
                else if (!FollowBindingExists())
                {
                    cam.SwitchOff();
                    _mode = Mode.FirstPerson;
                }
            }
            else
            {
                cam.SwitchOff();
                _mode = Mode.FirstPerson;
            }
        }

        /// <summary>Whether a key or a gamepad button that switches the follow view is bound and read at all.</summary>
        private static bool FollowBindingExists()
        {
            if (CameraTuning.FollowKey.Value.MainKey != KeyCode.None) return true;
            return GameInput.controllerEnabled && CameraTuning.FollowButton.Value != KeyCode.None;
        }

        private static void StartFollowing(BoatCamera cam)
        {
            if (!BoatCamera.on) cam.SwitchOn();
            else if (UISoundPlayer.instance != null) UISoundPlayer.instance.PlayUISound(UISounds.buttonClick, 1f, 1.2f);   // SwitchOn's click
            _mode = Mode.Following;
            _distance = CameraTuning.FollowDistance.Value;
            StartBehindPlayer(cam);
        }

        private static void ToShipView()
        {
            _mode = Mode.Ship;
            if (UISoundPlayer.instance != null) UISoundPlayer.instance.PlayUISound(UISounds.buttonClick, 1f, 1.3f);
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

        /// <summary>
        /// Whether the turn key or its gamepad button went down this frame. Read from the keyboard the way the follow
        /// key is, since this is a key pressed with the movement keys held, which is what KeyboardShortcut.IsDown
        /// refuses.
        /// </summary>
        private static bool LookLockPressed()
        {
            KeyboardShortcut shortcut = CameraTuning.LookLockKey.Value;
            KeyCode main = shortcut.MainKey;
            if (main != KeyCode.None && Input.GetKeyDown(main) && ModifiersHeld(shortcut)) return true;
            KeyCode button = CameraTuning.LookLockButton.Value;
            return GameInput.controllerEnabled && button != KeyCode.None && Input.GetKeyDown(button);
        }

        /// <summary>
        /// Switch the turn on or off for the view that is up. Nothing is drawn on screen; the game's own click says
        /// which way it went, low for off and high for on, well below the pitches the view clicks use (1.2 to 1.4).
        /// </summary>
        private static void ToggleLookLock()
        {
            _lookLocked = !_lookLocked;
            if (UISoundPlayer.instance != null)
                UISoundPlayer.instance.PlayUISound(UISounds.buttonClick, 1f, _lookLocked ? 0.85f : 1f);
            Plugin.Log.LogInfo("[Camera] look lock " + (_lookLocked ? "on" : "off"));
        }

        /// <summary>
        /// Whether the player is doing something that goes the way the body faces: walking (the same keys, D-pad and stick
        /// OVRPlayerController reads), or throwing or dropping a held item, which leaves from the front of the body.
        /// Standing still with none of these, the camera orbits around the body.
        /// </summary>
        private static bool WantsToFaceCamera()
        {
            if (GameInput.GetKey(InputName.MoveUp) || GameInput.GetKey(InputName.MoveDown)
                || GameInput.GetKey(InputName.MoveLeft) || GameInput.GetKey(InputName.MoveRight)) return true;
            if (Mathf.Abs(GameInput.GetPrimaryHorizontal()) > StickDeadZone || Mathf.Abs(GameInput.GetPrimaryVertical()) > StickDeadZone) return true;
            if (GameInput.controllerEnabled && (OVRInput.Get(OVRInput.Button.DpadUp) || OVRInput.Get(OVRInput.Button.DpadDown))) return true;
            if (!GameInput.GetKey(InputName.Throw) && !GameInput.GetKey(InputName.PickUp)) return false;
            var pointer = LocalInteraction.Pointer;
            return pointer != null && pointer.GetHeldItem() != null;
        }

        private const float StickDeadZone = 0.15f;

        /// <summary>Turn the player to face where the camera looks, so walking, strafing and backing up all read from the camera.</summary>
        private static void TurnPlayerToCamera(BoatCamera cam)
        {
            if (Refs.observerMirror == null || Refs.charController == null || !Refs.charController.enabled) return;
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
                // Also after the game's own Update, which switches its ship view on after the prefix (the camera key aboard)
                // and takes the mouth trigger out with the eye. The next physics step reads it from the pointer again.
                MouthOnPointer.Sync(MouthRidesPointer());
                if (Following && BoatCamera.instance != null && GameState.currentShipyard == null) Place(BoatCamera.instance);
            }
            catch (System.Exception e)
            {
                if (!_renderLogged)
                {
                    _renderLogged = true;
                    Plugin.Log.LogError("[Camera] before render: " + e);
                }
                Application.onBeforeRender -= PlaceBeforeRender;
                _renderHooked = false;
                _renderFailed = true;
            }
        }

        /// <summary>
        /// The rig just above the player's head, the eye behind it along the look, pulled in so nothing solid comes
        /// between them, and kept above the waves unless the player has dived under. Run in Update, and again just before
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
            // Start the sweep somewhere clear. A sphere that starts inside a wall does not see it, and the eye went out
            // the far side. Under a low deckhead or cabin roof a thinner sphere still fits at the eye's own height.
            Vector3 start = from;
            // Thin aboard: a boat is full of stays and rope, and a wider sphere would catch every one that passes behind
            // the head and jump the eye in and out.
            float radius = GameState.currentBoat != null ? 0.1f : 0.2f;
            bool lowered = false;
            if (!FromReachable(pivot, from, radius, rig))
            {
                radius = 0.1f;
                if (!FromReachable(pivot, from, radius, rig))
                {
                    start = pivot;
                    lowered = true;
                    LogLowered();
                    // Crouched under a low deckhead the head can be in it too. The top of the player's own capsule (the
                    // controller is 1.1 m tall with a 0.2 m radius, centered on the observer) is somewhere the game keeps
                    // out of walls.
                    if (Overlaps(pivot, radius, rig)) start = Refs.observerMirror.transform.position + Vector3.up * CapsuleTopAboveBody;
                }
            }
            Vector3 dir = want - start;
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : -pitch.forward;
            float clear = ClearDistance(start, want, rig, radius);
            // Squeezed. With under 0.3 m of room the eye goes onto the line the view turns about, so turning in a
            // tight spot turns the view in place. It used to be held 0.3 m back, where it swung on a 0.3 m circle
            // and read as first person with the camera off center (reported after walking into things). Eased in
            // up to 0.6 m so nothing jumps. This also covers what that floor was for: something beside the path
            // (a mast, a chair back) pulling the sphere in close no longer leaves the eye part way in.
            if (clear < 0.6f) clear = Mathf.InverseLerp(0.3f, 0.6f, clear) * 0.6f;
            want = start + dir * clear;

            // Keep the eye out of the waves while the player's head is out of the water. A swimmer diving under is
            // followed down.
            if (!PlayerSwimming.swimming || PlayerSwimming.swimmingOnSurface)
            {
                float sea;
                if (SeaAroundEye(want, eye.forward, out sea))
                {
                    float water = sea + 0.3f;
                    // Ashore and aboard, a swell higher than the head does not lift the eye above it. Wading or swimming
                    // at the surface the controller sits 0.5 to 0.9 m under the sea, so that test would never pass there.
                    if (want.y < water && (PlayerSwimming.inShallowWater || pivot.y > water)) want.y = water;
                }
            }
            eye.position = want;
            // Started low, the eye sits under the line it looks along, and looking along the pitch from there frames the
            // hips, not the head. Turn it up to the head instead. Close in, where that direction swings with every small
            // move, it eases back to the pitch. Eased in and out over about a quarter second, so walking under the edge
            // of a beam does not snap the view. Place runs twice a frame; the ease advances once.
            if (_lowTurnFrame != Time.frameCount)
            {
                // The view was closed in between, so what it was doing under a deck back then no longer counts.
                if (Time.frameCount - _lowTurnFrame > 2) _lowTurn01 = 0f;
                _lowTurnFrame = Time.frameCount;
                _lowTurn01 = Mathf.MoveTowards(_lowTurn01, lowered ? 1f : 0f, Time.unscaledDeltaTime * 4f);
            }
            Quaternion turn = Quaternion.identity;
            if (_lowTurn01 > 0.001f)
            {
                Vector3 toHead = pivot - want;
                float d = toHead.magnitude;
                if (d > 0.15f)
                {
                    Quaternion atHead = Quaternion.LookRotation(toHead / d, pitch.up);
                    Quaternion local = Quaternion.Inverse(pitch.rotation) * atHead;
                    turn = Quaternion.Slerp(Quaternion.identity, local, Mathf.InverseLerp(0.15f, 0.6f, d) * Mathf.SmoothStep(0f, 1f, _lowTurn01));
                }
            }
            if (eye.localRotation != turn) eye.localRotation = turn;
        }

        private static float _lowTurn01;
        private static int _lowTurnFrame = -1;
        private static int _loweredLogs;
        private static float _loweredLogAt = -100f;
        private static string _blockedBy;

        // What put the eye's start under a low deck, the first few times, so a report comes with its cause.
        private static void LogLowered()
        {
            if (_loweredLogs >= 8 || Time.unscaledTime - _loweredLogAt < 5f) return;
            _loweredLogs++;
            _loweredLogAt = Time.unscaledTime;
            Plugin.Log.LogInfo($"[Camera] low deck over the head: '{_blockedBy}'");
        }

        /// <summary>
        /// Whether the sweep can start at <paramref name="from"/>, above the head: nothing there, and nothing between the
        /// head and it. Under a thin deck the point above the head can be clear on the deck's far side, and an eye that
        /// started there swept along the deck above with the player hidden under it.
        /// </summary>
        private static bool FromReachable(Vector3 pivot, Vector3 from, float radius, Transform rig)
        {
            if (Overlaps(from, radius, rig)) return false;
            float rise = (from - pivot).magnitude;
            return rise < 1e-3f || ClearDistance(pivot, from, rig, radius) >= rise - 0.09f;
        }

        /// <summary>
        /// The eye back square on the rig, as the game's ship view and SwitchOff expect, after Place turned it up to the
        /// head under a low deckhead. Only while the eye hangs off the rig.
        /// </summary>
        private static void ResetEyeTurn(BoatCamera cam)
        {
            if (!BoatCamera.on || cam.transform.childCount == 0) return;
            Transform eye = CenterEyeRef(cam);
            if (eye != null && eye.parent == cam.transform.GetChild(0) && eye.localRotation != Quaternion.identity)
                eye.localRotation = Quaternion.identity;
        }

        private static readonly Crest.SampleHeightHelper _seaAtEye = new Crest.SampleHeightHelper();
        private static readonly Crest.SampleHeightHelper _seaAhead = new Crest.SampleHeightHelper();
        private static int _seaFrame = -1;
        private static float _seaHeight;

        // The game calls the eye underwater against the sea 1 m in front of it (Crest's UnderwaterEffect sits there,
        // and SwimEffects compares the eye with what it samples), so both points count. Place runs twice a frame
        // (Update and before render); ask once.
        private static bool SeaAroundEye(Vector3 eyePos, Vector3 forward, out float height)
        {
            if (_seaFrame != Time.frameCount)
            {
                float atEye, ahead;
                if (!SeaHeight.At(_seaAtEye, eyePos, out atEye)) { height = 0f; return false; }   // no ocean: no clamp
                if (!SeaHeight.At(_seaAhead, eyePos + forward, out ahead)) ahead = atEye;
                _seaHeight = Mathf.Max(atEye, ahead);
                _seaFrame = Time.frameCount;
            }
            height = _seaHeight;
            return true;
        }

        private static readonly RaycastHit[] _hits = new RaycastHit[24];
        private static readonly Collider[] _overlaps = new Collider[24];

        // Docks and some other scenery sit on Ignore Raycast (see Seating.SeatLayers). The UI layer is left out: the
        // game's inventory and needs panels hang off the camera, and counting them pulled the camera in, which moved the
        // panels in with it, over and over, so the view flicked between near and far. Layer 16 ("invis", which the
        // camera never draws) is left out too: the game moves hidden inventory items there, next to that panel.
        private static int CastMask { get { return Seating.SeatLayers & ~((1 << UiLayer) | (1 << InvisLayer)); } }

        /// <summary>Whether a collider the camera's casts found is something the eye has to stay out of.</summary>
        private static bool Counts(Collider c, bool walkCopy, Transform walkRoot, Transform rig)
        {
            if (c == null) return false;
            if (c.gameObject.layer == 2 && !Seating.IsStaticScenery(c)) return false;
            // A shape right on a boat's rigidbody is a rough envelope round the whole ship (a capsule about 3 m across on the
            // small dhow), not its deck or rails. The player's head is inside it on deck, which read as being under a
            // low deck everywhere aboard.
            if (c.attachedRigidbody != null && c.attachedRigidbody.transform == c.transform)
            {
                var mesh = c as MeshCollider;
                if (mesh == null || mesh.convex) return false;
            }
            if (Refs.charController != null && c.transform.IsChildOf(Refs.charController.transform)) return false;
            if (c.transform.IsChildOf(Refs.observerMirror.transform)) return false;
            if (rig != null && c.transform.IsChildOf(rig)) return false;   // anything riding along with the camera
            if (walkCopy && (walkRoot == null || !c.transform.IsChildOf(walkRoot))) return false;
            return true;
        }

        /// <summary>Whether a sphere at <paramref name="at"/> touches something solid, in both copies of a boat.</summary>
        private static bool Overlaps(Vector3 at, float radius, Transform rig)
        {
            if (OverlapsIn(at, radius, false, rig)) return true;
            Transform visual = Refs.observerMirror.transform.parent;
            Transform walk = Refs.charController != null ? Refs.charController.transform.parent : null;
            return visual != null && walk != null && visual != walk && GameState.currentBoat != null
                && OverlapsIn(walk.TransformPoint(visual.InverseTransformPoint(at)), radius, true, rig);
        }

        private static bool OverlapsIn(Vector3 at, float radius, bool walkCopy, Transform rig)
        {
            int n = Physics.OverlapSphereNonAlloc(at, radius, _overlaps, CastMask, QueryTriggerInteraction.Ignore);
            Transform walkRoot = walkCopy && Refs.charController != null ? Refs.charController.transform.parent : null;
            for (int i = 0; i < n; i++)
                if (Counts(_overlaps[i], walkCopy, walkRoot, rig))
                {
                    _blockedBy = _overlaps[i].name + " (layer " + _overlaps[i].gameObject.layer + ", " + (walkCopy ? "walking copy" : "visible") + ")";
                    return true;
                }
            return false;
        }

        /// <summary>
        /// How far a sphere of <paramref name="radius"/> (0 for a thin ray) can go from <paramref name="from"/> toward
        /// <paramref name="to"/> before something solid, in both copies of a boat.
        /// </summary>
        private static float ClearDistance(Vector3 from, Vector3 to, Transform rig, float radius)
        {
            Vector3 d = to - from;
            float length = d.magnitude;
            if (length < 1e-3f) return 0f;
            Vector3 dir = d / length;
            float clear = Cast(from, dir, length, false, rig, radius);
            Transform visual = Refs.observerMirror.transform.parent;
            Transform walk = Refs.charController != null ? Refs.charController.transform.parent : null;
            if (visual != null && walk != null && visual != walk && GameState.currentBoat != null)
            {
                Vector3 wFrom = walk.TransformPoint(visual.InverseTransformPoint(from));
                Vector3 wDir = walk.TransformDirection(visual.InverseTransformDirection(dir));
                clear = Mathf.Min(clear, Cast(wFrom, wDir, length, true, rig, radius));
            }
            return Mathf.Max(0f, clear);
        }

        private static float Cast(Vector3 from, Vector3 dir, float length, bool walkCopy, Transform rig, float radius)
        {
            const float margin = 0.08f;
            int n = radius > 0f
                ? Physics.SphereCastNonAlloc(from, radius, dir, _hits, length, CastMask, QueryTriggerInteraction.Ignore)
                : Physics.RaycastNonAlloc(from, dir, _hits, length, CastMask, QueryTriggerInteraction.Ignore);
            float best = length;
            Transform walkRoot = walkCopy && Refs.charController != null ? Refs.charController.transform.parent : null;
            for (int i = 0; i < n; i++)
            {
                if (!Counts(_hits[i].collider, walkCopy, walkRoot, rig)) continue;
                _blockedBy = _hits[i].collider.name + " (layer " + _hits[i].collider.gameObject.layer + ", " + (walkCopy ? "walking copy" : "visible") + ")";
                // The start was checked clear, so something touching it stops the eye right there.
                best = Mathf.Min(best, _hits[i].distance <= 0f ? 0f : _hits[i].distance - margin);
            }
            return best;
        }
    }

    /// <summary>The sea's height under a world point, for the follow camera and the swimming it feeds.</summary>
    internal static class SeaHeight
    {
        /// <summary>
        /// False with no ocean to ask. Crest answers a query a frame or more later, keyed by the sampler, so each kind of
        /// point keeps its own sampler; until an answer arrives the calm sea level stands in.
        /// </summary>
        internal static bool At(Crest.SampleHeightHelper sampler, Vector3 world, out float height)
        {
            bool live;
            return At(sampler, world, out height, out live);
        }

        /// <summary>As above, and <paramref name="live"/> is false while the calm sea level is standing in for Crest's answer.</summary>
        internal static bool At(Crest.SampleHeightHelper sampler, Vector3 world, out float height, out bool live)
        {
            height = 0f;
            live = false;
            try
            {
                var ocean = Crest.OceanRenderer.Instance;
                if (ocean == null) return false;
                sampler.Init(world, 0f, true);
                live = sampler.Sample(out height);
                if (!live) height = ocean.SeaLevel;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Swimming while the camera follows the player. PlayerSwimming dives by the angle between the body and Camera.main,
    /// and floats the swimmer at the sea height Crest samples in front of that camera. In this view the camera is up to
    /// 12 m behind the player and starts out looking down, so holding forward sank the swimmer, who also rose and fell
    /// with waves far behind. Give it the sea at the player and no dive from the view; Crouch and Jump still dive and
    /// rise. Registered by GuardedPatches on PlayerSwimming.LateUpdate.
    /// </summary>
    internal static class FollowCameraSwimming
    {
        private static readonly Crest.SampleHeightHelper _seaAtPlayer = new Crest.SampleHeightHelper();
        private static float _stockDive;
        private static bool _stockDiveKnown, _logged;

        internal static void Prefix(PlayerSwimming __instance, out float __state)
        {
            __state = float.NaN;
            try
            {
                if (!_stockDiveKnown)
                {
                    _stockDive = __instance.diveSpeedMult;
                    _stockDiveKnown = true;
                }
                bool follow = PlayerOrbitCamera.Following && Refs.observerMirror != null;
                __instance.diveSpeedMult = follow ? 0f : _stockDive;   // the dive is the angle to Camera.main times this
                if (!follow) return;
                float sea;
                bool live;
                // The query at the player starts cold each time the view does (Crest drops one left unasked for 10
                // frames). Until it answers, keep the camera's own sample, which still lags from near the first person
                // eye, rather than the calm sea level, which would pull a swimmer in a swell down or up for a moment.
                if (!SeaHeight.At(_seaAtPlayer, Refs.observerMirror.transform.position, out sea, out live) || !live) return;
                // Read once by this LateUpdate and put back in the postfix, so the underwater effects still see the
                // camera's own sample.
                __state = Crest.UnderwaterEffect.cameraWaterHeight;
                Crest.UnderwaterEffect.cameraWaterHeight = sea;
            }
            catch (System.Exception e)
            {
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Log.LogError("[Camera] swim: " + e);
                }
            }
        }

        internal static void Postfix(float __state)
        {
            if (!float.IsNaN(__state)) Crest.UnderwaterEffect.cameraWaterHeight = __state;
        }
    }

    /// <summary>
    /// The game's Invert Mouse checkbox writes the MouseLook two parents above Camera.main. In first person that is the
    /// head's up and down look; while the camera key's view is up it is the rig's, so the click turned the rig's look
    /// around at less than half its speed and first person kept the old direction until a restart. Put a write that
    /// misses the head back, and give it to the head. Registered by GuardedPatches on GPButtonSettingsCheckbo.UpdateButton,
    /// and stands aside while FollowCamera is off, where the game's camera and this setting are left as they are.
    /// </summary>
    internal static class InvertMouseCheckbox
    {
        internal sealed class Stray
        {
            internal MouseLook Look;
            internal float Sens;
        }

        internal static void Prefix(GPButtonSettingsCheckbo __instance, out Stray __state)
        {
            __state = null;
            if (__instance.setting != "invertMouse") return;
            if (CameraTuning.FollowCamera == null || !CameraTuning.FollowCamera.Value) return;
            try
            {
                var cam = Camera.main;
                Transform above = cam != null && cam.transform.parent != null ? cam.transform.parent.parent : null;
                var target = above != null ? above.GetComponent<MouseLook>() : null;
                var head = Refs.ovrCameraRig != null ? Refs.ovrCameraRig.GetComponent<MouseLook>() : null;
                if (target != null && head != null && target != head) __state = new Stray { Look = target, Sens = target.sensitivityY };
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("[Camera] invert mouse: " + e);
            }
        }

        internal static void Postfix(GPButtonSettingsCheckbo __instance, Stray __state)
        {
            if (__state == null) return;
            try
            {
                __state.Look.sensitivityY = __state.Sens;
                var head = Refs.ovrCameraRig.GetComponent<MouseLook>();
                head.sensitivityY = (__instance.on ? -1f : 1f) * Mathf.Abs(head.sensitivityY);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("[Camera] invert mouse: " + e);
            }
        }
    }
}

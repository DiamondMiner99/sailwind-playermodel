using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// A custom in-game PAUSE menu that replaces "Esc = the full settings screen". It is a clone of the title
    /// screen's parchment scroll (mesh 'scroll_open'), parented under the StartMenu root so it inherits
    /// MoveMenuToPlayer's camera-facing orientation, with a clean button column.
    ///
    /// It ships five buttons of its own - Resume, Character, Settings, Recover Boat, Quit Game - and OTHER MODS ADD THEIR
    /// OWN with <see cref="Register"/>. That is the whole reason this lives here rather than in the co-op mod
    /// where it started: two mods each cloning their own parchment would fight over Escape and draw two
    /// scrolls on top of each other. One menu, many contributors.
    ///
    /// Lifecycle (driven by <see cref="PauseMenuPatches"/>):
    ///  - GameToSettings postfix -> OnPauseOpened(): the game has already done its pause bookkeeping
    ///    (timescale 0, MoveMenuToPlayer, the unpausedTimescale field) and opened the vanilla settings panel;
    ///    we hide that and show ours.
    ///  - SettingsToGame postfix -> Hide(): any unpause hides our panel. The StartMenu root never deactivates,
    ///    so we have to hide ourselves.
    ///  - LateUpdate / ButtonClick prefixes -> Esc-resume and settings-Back-to-pause.
    /// </summary>
    public static class ModPauseMenu
    {
        public const string Resume    = "modpause_resume";
        public const string Character = "modpause_character";
        public const string Settings  = "modpause_settings";
        public const string Recover   = "modpause_recover";
        public const string Quit      = "modpause_quit";

        /// <summary>Order values for the built-in buttons, so a registering mod can slot in around them.</summary>
        public static class Order
        {
            public const int Resume = 0;
            public const int Character = 300;
            public const int Settings = 400;
            public const int Recover = 500;
            public const int Quit = 900;
        }

        // ---- layout ------------------------------------------------------------------------------------
        // Tuned by screenshot against the real parchment. Far fewer knobs than per-button positions.
        const float ColX = 0f;
        const float ColZ = 0.037f;
        // Measured off a screenshot rather than guessed: button centers ran y100 (Resume) to y443 (Quit), so
        // 6 steps over 343px = 57px per step. A step is ColSpan/6 = 0.204 local units, which puts one local
        // unit at about 280px.
        const float ColTopY = 1.05f;
        const float ColStep = 0.245f;
        // Vertical extent the column may occupy, in the same local units as ColStep. Derived from the
        // original six-button layout so that case stays pixel-identical; LayoutButtons shrinks the step to
        // stay inside this when there are more buttons.
        //
        // Stays at FIVE steps even though there can be seven or more buttons. The parchment is stretched
        // 1.2x below, so local units already cover 1.2x more screen than they used to: widening this as well
        // multiplied the spacing twice over and pushed the bottom button out through the scroll's lower roll.
        const float ColSpan = ColStep * 5f;
        /// <summary>The column's MIDPOINT, which is what LayoutButtons anchors to. The button count varies
        /// with which mods are installed and what state they are in, and only a centered column looks right
        /// at every count.</summary>
        const float ColCenterY = ColTopY - ColSpan * 0.5f;
        /// <summary>
        /// The parchment is STRETCHED vertically to make room for extra buttons, instead of shrinking the
        /// buttons to fit the old height. Shrinking worked, but a smaller button plate on an unchanged scroll
        /// leaves a strip of bare parchment between every pair, which reads as a gap someone forgot to close
        /// rather than as spacing.
        ///
        /// Children inherit the stretch, so everything hung on this panel is counter-scaled on Y by the same
        /// factor and comes out uniform again: panel 1.2 * child (1/1.2) = 1. Only the scroll mesh itself is
        /// left stretched, which is what we wanted.
        /// </summary>
        public const float PanelStretchY = 1.2f;

        // ---- state -------------------------------------------------------------------------------------

        static GameObject _panel;
        static MonoBehaviour _startMenu;
        static Transform _startUI;
        static Transform _buttonTemplate;
        static readonly List<Entry> _entries = new List<Entry>();
        static bool _builtIns;

        /// <summary>One button on the menu. Label and Visible are polled every refresh, so a button can
        /// change its text or vanish as state changes without anyone re-registering it.</summary>
        public sealed class Entry
        {
            public string Id;
            public int Order;
            /// <summary>Text to show. Called every refresh; return null to leave the current label alone.</summary>
            public Func<string> Label;
            /// <summary>Whether to show it at all. Null means always. Hidden buttons close the gap in the column.</summary>
            public Func<bool> Visible;
            /// <summary>What the click does. Return value is ignored; the click is always consumed.</summary>
            public Action OnClick;
        }

        /// <summary>True while a sub-page (settings / recovery / quit-confirm) was opened FROM this menu, so
        /// its Back and Esc return to our panel instead of the vanilla settings screen.</summary>
        public static bool SubPageFromPause { get; set; }

        public static bool IsOpen { get { return _panel != null && _panel.activeSelf; } }

        /// <summary>The panel transform, for a mod hanging something extra on it. Null before it is built.</summary>
        public static Transform Panel { get { return _panel != null ? _panel.transform : null; } }

        // ---- extension points --------------------------------------------------------------------------

        /// <summary>
        /// Return true to keep the world simulating while the menu is open. Co-op sets this: in a lobby the
        /// menu must NOT freeze the world, because a host pausing would stop simulating the shared boat and a
        /// time stop desyncs both sides. Solo, leave it alone and the game pauses normally.
        /// </summary>
        public static Func<bool> KeepWorldRunning;

        /// <summary>
        /// Return true to keep re-pinning the world-space menu to the camera even when the panel itself is
        /// hidden. A mod that hides the panel to show its own screen over the top needs this, or the
        /// parchment and logo sail away behind it while the boat keeps moving.
        /// </summary>
        public static Func<bool> KeepPinned;

        /// <summary>Raised once, after the panel and its buttons exist. Hang extra scenery on it here.</summary>
        public static event Action PanelBuilt;

        /// <summary>Raised every refresh while the menu is open, before the column is laid out.</summary>
        public static event Action Refreshing;

        /// <summary>Raised when the panel is hidden, so a contributor can tear down what it added.</summary>
        public static event Action Hiding;

        /// <summary>Last chance to consume a menu click this menu did not recognize. Return true to swallow it.</summary>
        public static Func<string, bool> ClickFallback;

        // ---- registration ------------------------------------------------------------------------------

        /// <summary>
        /// Add a button. Safe to call at any time, including before the panel exists: the column is rebuilt
        /// from the registry whenever the menu is installed or opened. Registering the same id twice replaces
        /// the earlier entry rather than stacking two buttons.
        /// </summary>
        public static void Register(Entry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Id)) return;
            RegisterBuiltIns(); // so a mod registering from its Awake sorts against the built-ins from the start
            _entries.RemoveAll(e => e.Id == entry.Id);
            _entries.Add(entry);
            _entries.Sort((a, b) => a.Order.CompareTo(b.Order));
            Plugin.Log.LogInfo($"[PauseMenu] Registered button '{entry.Id}' at order {entry.Order} ({_entries.Count} total)");
            // A panel that already exists needs the new button built into it now, not at the next install.
            if (_panel != null && _buttonTemplate != null)
            {
                EnsureButtons();
                LayoutButtons();
            }
        }

        public static void Unregister(string id)
        {
            int removed = _entries.RemoveAll(e => e.Id == id);
            if (removed == 0) return;
            var t = _panel != null ? MenuUtil.FindChild(_panel.transform, id) : null;
            if (t != null) UnityEngine.Object.Destroy(t.gameObject);
            Plugin.Log.LogInfo($"[PauseMenu] Unregistered button '{id}'");
        }

        /// <summary>
        /// Replace the visibility rule of a registered button, built-ins included. Null means always shown.
        /// Co-op uses this to hide Recover Boat from guests, who must not recover the shared boat locally.
        /// Safe to call from a plugin's Awake: the built-ins are registered on demand.
        /// </summary>
        public static void SetVisible(string id, Func<bool> visible)
        {
            RegisterBuiltIns();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Id != id) continue;
                _entries[i].Visible = visible;
                Plugin.Log.LogInfo($"[PauseMenu] Visibility rule set on '{id}'");
                return;
            }
            Plugin.Log.LogWarning($"[PauseMenu] SetVisible: no button '{id}'");
        }

        static void RegisterBuiltIns()
        {
            if (_builtIns) return;
            _builtIns = true;

            Register(new Entry
            {
                Id = Resume,
                Order = Order.Resume,
                Label = () => "Resume",
                OnClick = () => InvokeStartMenu("SettingsToGame"), // unpause; its postfix hides our panel
            });

            Register(new Entry
            {
                Id = Character,
                Order = Order.Character,
                Label = () => "Character",
                // Deliberately NOT hidden when there is no body yet. The screen is built to SAY that it has
                // no part counts on this machine, which is a better answer than a button that vanishes at
                // sea and leaves a player unable to tell the feature exists.
                // Opening HIDES this parchment: a screen-space IMGUI panel drawn over live world-space
                // buttons would let a click fall through to whatever sits beneath it, and one of those is
                // Quit Game. CharacterScreen.Open does the hiding itself so the two cannot desync.
                OnClick = () => CharacterScreen.Open(),
            });

            Register(new Entry
            {
                Id = Settings,
                Order = Order.Settings,
                Label = () => "Settings",
                OnClick = () =>
                {
                    Hide();
                    SubPageFromPause = true;
                    InvokeStartMenu("EnableSettingsMenu");
                    HideInSettingsRecoverQuit(); // we have dedicated Quit; hide the in-settings duplicates
                },
            });

            Register(new Entry
            {
                Id = Recover,
                Order = Order.Recover,
                Label = () => "Recover Boat",
                OnClick = () =>
                {
                    Hide();
                    SubPageFromPause = true; // Back or Esc out of the recovery screen returns to our panel
                    InvokeStartMenu("EnableRecoveryMenu");
                },
            });

            Register(new Entry
            {
                Id = Quit,
                Order = Order.Quit,
                Label = () => "Quit Game",
                OnClick = () =>
                {
                    Hide();
                    SubPageFromPause = true; // so cancelling the confirm returns to our panel
                    InvokeButtonClick(StartMenuButtonType.QuitMenu);
                },
            });
        }

        // ---- build -------------------------------------------------------------------------------------

        /// <summary>Build the custom pause panel once (from a StartMenu.Start postfix). Kept inactive.</summary>
        public static void Install(MonoBehaviour startMenu)
        {
            try
            {
                _startMenu = startMenu;
                RegisterBuiltIns();
                if (_panel != null) return;

                var startUI = MenuUtil.FindChild(startMenu.transform, "start UI");
                if (startUI == null) { Plugin.Log.LogWarning("[PauseMenu] 'start UI' not found"); return; }
                _startUI = startUI;

                // Clone the parchment scroll and place it where start UI sits, so MoveMenuToPlayer aims it.
                _panel = UnityEngine.Object.Instantiate(startUI.gameObject, startMenu.transform);
                _panel.name = "modpause_panel";
                _panel.transform.localPosition = startUI.localPosition;
                _panel.transform.localRotation = startUI.localRotation;
                var scrollScale = startUI.localScale;
                _panel.transform.localScale = new Vector3(scrollScale.x, scrollScale.y * PanelStretchY, scrollScale.z);

                var template = MenuUtil.FindChild(_panel.transform, "button new game");
                if (template == null)
                {
                    // Do not leave a live clone of the title menu around: its vanilla New Game and Quit
                    // buttons would fire real actions. Destroy and null it so OnPauseOpened can retry rather
                    // than caching a broken panel.
                    Plugin.Log.LogWarning("[PauseMenu] template button not found");
                    UnityEngine.Object.Destroy(_panel); _panel = null; return;
                }
                _buttonTemplate = template;

                // Record the vanilla buttons (by their StartMenuButton component's parent) BEFORE adding
                // ours. Robust to however they are nested under the scroll; name-based stripping missed
                // them, which left the originals overlapping our column.
                var vanilla = new List<GameObject>();
                foreach (var smb in _panel.GetComponentsInChildren<StartMenuButton>(true))
                {
                    var btn = (smb.transform.parent != null) ? smb.transform.parent.gameObject : smb.gameObject;
                    if (!vanilla.Contains(btn)) vanilla.Add(btn);
                }

                EnsureButtons();

                // Remove the original title buttons so only ours remain.
                foreach (var go in vanilla) UnityEngine.Object.Destroy(go);

                LayoutButtons();
                _panel.SetActive(false);

                var built = PanelBuilt;
                if (built != null)
                {
                    try { built(); }
                    catch (Exception e) { Plugin.Log.LogError($"[PauseMenu] a PanelBuilt listener threw: {e}"); }
                }
                Plugin.Log.LogInfo($"[PauseMenu] pause panel built with {_entries.Count} button(s)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[PauseMenu] install failed: {e}");
                // A half-built panel (vanilla buttons not yet stripped) must never go live; drop it so the
                // next open retries from scratch.
                if (_panel != null) { UnityEngine.Object.Destroy(_panel); _panel = null; }
            }
        }

        static void EnsureButtons()
        {
            if (_panel == null || _buttonTemplate == null) return;
            for (int i = 0; i < _entries.Count; i++)
                MenuUtil.EnsureButton(_panel.transform, _buttonTemplate, _entries[i].Id,
                    new Vector3(ColX, ColTopY - ColStep * i, ColZ));
        }

        /// <summary>
        /// Clone the same parchment scroll as a child of the panel, for a mod that wants a second sheet
        /// beside the button column (co-op hangs its crew roster on one). The clone has its title buttons
        /// stripped and starts inactive. Returns null if the menu has not been built yet.
        /// </summary>
        public static GameObject CloneScroll(string name, Vector3 localPos, float scale)
        {
            if (_panel == null || _startUI == null) return null;
            try
            {
                var scroll = UnityEngine.Object.Instantiate(_startUI.gameObject, _panel.transform);
                scroll.name = name;
                scroll.transform.localPosition = localPos;
                scroll.transform.localRotation = Quaternion.identity; // inherit the panel's player-facing aim
                // Counter the panel's vertical stretch so the extra sheet is not squashed.
                scroll.transform.localScale = new Vector3(scale, scale / PanelStretchY, scale);
                foreach (var smb in scroll.GetComponentsInChildren<StartMenuButton>(true))
                {
                    var btn = (smb.transform.parent != null) ? smb.transform.parent.gameObject : smb.gameObject;
                    UnityEngine.Object.Destroy(btn);
                }
                scroll.SetActive(false);
                return scroll;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[PauseMenu] CloneScroll('{name}') failed: {e}");
                return null;
            }
        }

        /// <summary>A button on the panel by id, for a contributor that needs the transform itself.</summary>
        public static Transform FindButton(string id)
        {
            return _panel != null ? MenuUtil.FindChild(_panel.transform, id) : null;
        }

        // ---- lifecycle ---------------------------------------------------------------------------------

        /// <summary>Called from the GameToSettings postfix: hide the vanilla settings panel, show ours.</summary>
        public static void OnPauseOpened(MonoBehaviour startMenu)
        {
            try
            {
                _startMenu = startMenu;
                if (_panel == null) Install(startMenu);
                if (_panel == null) return;
                SubPageFromPause = false;
                InvokeStartMenu("DisableSettingsMenu");
                _panel.SetActive(true);
                Refresh();
                RestoreTimescaleIfNeeded();
            }
            catch (Exception e) { Plugin.Log.LogError($"[PauseMenu] open failed: {e}"); }
        }

        /// <summary>
        /// Put the world back in motion when something asked us not to freeze it.
        ///
        /// GameToSettings has already set Time.timeScale to 0. Co-op needs it running while in a lobby, and
        /// this is re-asserted every refresh rather than only on open, because a session can be STARTED from
        /// this menu: the flag flips true after the open-time check has run, and the host then sat at
        /// timeScale 0 until they happened to close and reopen the menu. Nothing a host owes its crew runs in
        /// that state - the join-state send is a coroutine, so a guest joining a frozen host waited out the
        /// full snapshot timeout and was told it had been refused. Cheap to re-check, and a no-op once held.
        ///
        /// Note what this deliberately does NOT do: disable player control. Freezing the player means
        /// disabling the CharacterController, which kills gravity (you freeze mid-jump) and, on a moving
        /// ship, risks not tracking the deck. Walking around in a menu is the lesser problem.
        /// </summary>
        static void RestoreTimescaleIfNeeded()
        {
            var keep = KeepWorldRunning;
            if (keep == null) return;
            bool wanted;
            try { wanted = keep(); }
            catch (Exception e) { Plugin.Log.LogWarning("[PauseMenu] KeepWorldRunning threw: " + e.Message); return; }
            if (!wanted || Time.timeScale > 0f) return;

            float ts = 1f;
            try { ts = Traverse.Create(_startMenu).Field("unpausedTimescale").GetValue<float>(); } catch { }
            Time.timeScale = ts > 0f ? ts : 1f;
            Physics.autoSyncTransforms = true;
        }

        /// <summary>
        /// Re-show the pause parchment after one of our own sub-screens closes. Without this, closing the
        /// character screen would leave the player paused in a cursor menu with nothing on screen at all:
        /// our panel hidden, and vanilla's settings panel already suppressed by us.
        /// </summary>
        public static void Reopen()
        {
            if (_panel != null && _startMenu != null) OnPauseOpened(_startMenu);
        }

        /// <summary>
        /// Unpause and drop back to gameplay. Public because some actions must not be started from a paused
        /// game: co-op's join runs a multi-second coroutine that never progresses at timeScale 0, and its
        /// watchdog then concludes the host refused. Such actions have to begin from a running world.
        /// </summary>
        public static void ResumeGame()
        {
            InvokeStartMenu("SettingsToGame");
        }

        public static void Hide()
        {
            if (_panel != null && _panel.activeSelf) _panel.SetActive(false);
            var hiding = Hiding;
            if (hiding != null)
            {
                try { hiding(); }
                catch (Exception e) { Plugin.Log.LogError($"[PauseMenu] a Hiding listener threw: {e}"); }
            }
        }

        /// <summary>Refresh labels and contributed scenery while open. Called every frame.</summary>
        public static void Tick()
        {
            try
            {
                if (!IsOpen) return;
                Refresh();
            }
            catch (Exception e) { Plugin.Log.LogError($"[PauseMenu] tick failed: {e}"); }
        }

        /// <summary>
        /// Re-pin the world-space menu to the camera. MUST run from LateUpdate, NOT Update. The world does
        /// not pause when something holds KeepWorldRunning, so the boat sails this world-space menu out of
        /// view; MoveMenuToPlayer re-aims it at Camera.main, but Camera.main only finishes following the
        /// bobbing observer mirror in LateUpdate, so re-pinning in Update used LAST frame's camera pose and
        /// the menu lagged a frame, bobbing on screen as the boat moved.
        ///
        /// Keyed on Time.timeScale > 0 so a panel left open across a running-to-frozen transition stays put
        /// instead of flying off; a normal solo pause has timeScale 0 and correctly does not re-pin.
        /// </summary>
        public static void LatePin()
        {
            try
            {
                // Re-pin while EITHER our panel is open OR a sub-page opened from it (settings, quit-confirm)
                // is showing. Those sub-pages are vanilla startMenu children that ALSO drift while the world
                // keeps running, and our panel is HIDDEN while one is up, so gating only on IsOpen left them
                // un-pinned and they floated off at sea.
                //
                // KeepPinned covers the other half: a mod screen drawn OVER the menu hides our panel as it
                // opens (so no clickable world-space button is left behind it), which turns IsOpen false
                // while vanilla's startMenu root, carrying the logo and the parchment, is still active.
                bool pinning = IsOpen
                               || (SubPageFromPause && AnySubPageActive())
                               || CharacterScreen.IsOpen
                               || SafeKeepPinned();
                if (!pinning) return;
                if (_startMenu != null && Time.timeScale > 0f)
                    Traverse.Create(_startMenu).Method("MoveMenuToPlayer").GetValue();
            }
            catch (Exception e) { Plugin.Log.LogError($"[PauseMenu] late-pin failed: {e}"); }
        }

        static bool SafeKeepPinned()
        {
            var f = KeepPinned;
            if (f == null) return false;
            try { return f(); } catch { return false; }
        }

        static void Refresh()
        {
            if (_panel == null) return;
            RestoreTimescaleIfNeeded();

            var refreshing = Refreshing;
            if (refreshing != null)
            {
                try { refreshing(); }
                catch (Exception e) { Plugin.Log.LogError($"[PauseMenu] a Refreshing listener threw: {e}"); }
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var t = MenuUtil.FindChild(_panel.transform, e.Id);
                if (t == null) continue;

                bool visible = true;
                if (e.Visible != null)
                {
                    try { visible = e.Visible(); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[PauseMenu] '{e.Id}' Visible threw: {ex.Message}"); }
                }
                MenuUtil.SetActive(t, visible);
                if (!visible) continue;

                if (e.Label != null)
                {
                    string label = null;
                    try { label = e.Label(); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[PauseMenu] '{e.Id}' Label threw: {ex.Message}"); }
                    if (label != null) MenuUtil.SetLabel(t, label);
                }
            }

            LayoutButtons(); // re-stack the visible buttons so a hidden one closes the gap
        }

        // ---- clicks ------------------------------------------------------------------------------------

        public static bool HandleClick(string name)
        {
            if (name == null) return false;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Id != name) continue;
                var act = _entries[i].OnClick;
                if (act != null)
                {
                    try { act(); }
                    catch (Exception e) { Plugin.Log.LogError($"[PauseMenu] '{name}' click threw: {e}"); }
                }
                return true;
            }
            var fallback = ClickFallback;
            if (fallback != null)
            {
                try { return fallback(name); } catch { return false; }
            }
            return false;
        }

        /// <summary>Esc while our panel (or our settings sub-page) is open. Returns true if consumed.</summary>
        public static bool OnEscape(MonoBehaviour startMenu)
        {
            _startMenu = startMenu;
            if (IsOpen) { InvokeStartMenu("SettingsToGame"); return true; } // resume
            if (SubPageFromPause && _panel != null && AnySubPageActive())
            {
                ReturnToPanelFromSubPage();
                return true;
            }
            return false;
        }

        /// <summary>Sub-page "Back" pressed: if it was opened from our menu, return here instead of unpausing.</summary>
        public static bool OnSettingsBack(MonoBehaviour startMenu, StartMenuButtonType button)
        {
            _startMenu = startMenu;
            if (button != StartMenuButtonType.Back) return false;
            if (!SubPageFromPause || _panel == null) return false;
            // The new-game-only sub-panels are off limits; they should not appear in-game anyway.
            if (SubPanelActive("chooseIslandUI") || SubPanelActive("saveSlotUI")) return false;
            if (!AnySubPageActive()) return false;
            ReturnToPanelFromSubPage();
            return true;
        }

        static bool AnySubPageActive()
        {
            return SubPanelActive("settingsUI") || SubPanelActive("recoveryUI") || SubPanelActive("confirmQuitUI");
        }

        /// <summary>Close whichever sub-page is open and re-show our panel.</summary>
        static void ReturnToPanelFromSubPage()
        {
            SubPageFromPause = false;
            if (SubPanelActive("settingsUI")) InvokeStartMenu("DisableSettingsMenu");
            if (SubPanelActive("recoveryUI")) InvokeStartMenu("DisableRecoveryMenu");
            var cq = (_startMenu != null) ? Traverse.Create(_startMenu).Field("confirmQuitUI").GetValue<GameObject>() : null;
            if (cq != null && cq.activeInHierarchy) cq.SetActive(false);
            if (_panel != null) { _panel.SetActive(true); Refresh(); }
        }

        // ---- layout ------------------------------------------------------------------------------------

        /// <summary>
        /// Stack only the VISIBLE buttons, centered on the parchment, so a hidden button closes the gap
        /// instead of leaving a hole in the column.
        ///
        /// CENTERED rather than hung from a fixed top, because the count is not constant: it varies with
        /// which mods are installed and what state they are in. Anchoring the top meant a shorter list kept
        /// the same gap above the first button and dumped all its slack below the last, so one machine's menu
        /// sat visibly high on the scroll while another looked right. That was once reported as a resolution
        /// difference between two machines, and both were 1920x1080; it was the button count all along.
        ///
        /// The buttons also SHRINK as the column fills. The span is fixed, so each extra button shrinks the
        /// step, which reads as crowded. Scaling the buttons is the only lever that changes the
        /// gap-to-button ratio: stretching the parchment would squash these children, since they inherit its
        /// scale, and scaling it uniformly magnifies the crowding along with everything else.
        /// </summary>
        static void LayoutButtons()
        {
            if (_panel == null) return;

            int visible = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                var b = MenuUtil.FindChild(_panel.transform, _entries[i].Id);
                if (b != null && b.gameObject.activeSelf) visible++;
            }
            float step = visible > 1 ? Mathf.Min(ColStep, ColSpan / (visible - 1)) : ColStep;
            float scale = Mathf.Clamp(BodyTuning.MenuButtonScale != null ? BodyTuning.MenuButtonScale.Value : 1f, 0.5f, 1f);
            float top = ColCenterY + step * (visible - 1) * 0.5f;

            int n = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                var b = MenuUtil.FindChild(_panel.transform, _entries[i].Id);
                if (b == null || !b.gameObject.activeSelf) continue;
                b.localPosition = new Vector3(ColX, top - step * n, ColZ);
                // Undo the parchment's vertical stretch so the button plate stays square.
                b.localScale = new Vector3(scale, scale / PanelStretchY, scale);
                n++;
            }
        }

        // ---- vanilla plumbing --------------------------------------------------------------------------

        /// <summary>
        /// Hide the vanilla in-settings Recover/Quit buttons while our menu owns Settings, so the only
        /// entry points are our own dedicated buttons. Otherwise Back out of recovery or quit-confirm skips
        /// the settings level. EnableSettingsMenu re-activates them, so this runs right after it.
        /// </summary>
        static void HideInSettingsRecoverQuit()
        {
            if (_startMenu == null) return;
            var rec = Traverse.Create(_startMenu).Field("recoverButton").GetValue<GameObject>();
            if (rec != null) rec.SetActive(false);
            var quit = Traverse.Create(_startMenu).Field("quitButtonInSettings").GetValue<GameObject>();
            if (quit != null) quit.SetActive(false);
        }

        public static void InvokeStartMenu(string method)
        {
            if (_startMenu == null) return;
            Traverse.Create(_startMenu).Method(method).GetValue();
        }

        static void InvokeButtonClick(StartMenuButtonType type)
        {
            if (_startMenu == null) return;
            Traverse.Create(_startMenu).Method("ButtonClick", new object[] { type }).GetValue();
        }

        static bool SubPanelActive(string field)
        {
            if (_startMenu == null) return false;
            var go = Traverse.Create(_startMenu).Field(field).GetValue<GameObject>();
            return go != null && go.activeInHierarchy;
        }
    }
}

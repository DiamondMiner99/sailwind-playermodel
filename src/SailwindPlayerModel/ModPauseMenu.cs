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
        /// <summary>
        /// The layout above is designed for six buttons. Each button past six stretches the parchment by one more
        /// step instead of squeezing the column, because squeezed plates overlapped and the bright edge of each plate
        /// showed through the one below as a white line (co-op adds three buttons, so a host has eight). The panel's
        /// Y scale goes from PanelStretchY up to this cap; past it the plates shrink to fit. The panel is also
        /// raised so the scroll stays centered on screen at any stretch (RaisePanel).
        /// </summary>
        const float MaxStretchY = 1.75f;

        // Grouped layout, in SCREEN units: local Y times the stretch, about 279 px each at 1080p. Everything here
        // was measured off two 1080p screenshots (stretch 1.45 and 1.68, 2026-11-14), which agree: the scroll grows
        // about a pivot 870 px down the screen, reaching 405 px per unit of stretch above it and 143 below, with
        // the rollers' inner edges at 324 above and 72 below.
        const float PxPerUnit = 279f;
        const float PlateHeight = 0.258f;     // a full-size plate, 72 px
        const float PlateScale = 0.9f;        // 65 px
        const float InnerGap = 0.05f;         // 14 px between plates
        // Paper style, 15% up from the design pass on request (2026-11-14): 67 px plates, 9 px gaps.
        const float PaperPlateScale = 0.92f;
        const float PaperGap = 0.033f;
        const float RollerMargin = 0.129f;    // 36 px of parchment between the column and each roller
        /// <summary>Parchment between the rollers, per unit of stretch: (324 + 72) px.</summary>
        const float InnerPerStretch = 396f / PxPerUnit;
        /// <summary>Middle of the space between the rollers, in panel-local units above the pivot: (324 - 72) / 2 px.</summary>
        const float ColumnCenterLocal = 126f / PxPerUnit;
        // Screen placement: pivot as built, screen middle, and the scroll's middle above the pivot per stretch.
        const float PivotPx = 870f;
        const float ScreenMidPx = 540f;
        const float ScrollMidPerStretchPx = 131f;
        static Vector3 _panelBasePos;
        static float _stretch = PanelStretchY;
        static Vector3 _scrollScale = Vector3.one;

        /// <summary>A sheet from CloneScroll, kept at the screen position and shape it was made with when the panel
        /// stretches.</summary>
        class ClonedSheet
        {
            public Transform T;
            public Vector3 Pos;
            public float Scale;
            /// <summary>The scroll's own visible renderers (rollers, parchment), not what a mod hangs on it.</summary>
            public HashSet<Renderer> Scroll;
            /// <summary>Every renderer of the scroll, hidden ones too, so none of it is taken for a mod's content.</summary>
            public HashSet<Renderer> ScrollAll;
            public Paper Paper;
        }

        // ---- style ---------------------------------------------------------------------------------------

        /// <summary>How the pause menu is drawn. Paper (default since 2026-11-14) hangs the buttons on a sheet like the
        /// Sailwind banner; Scroll is the title menu's scroll. A screen-space panel and real scroll rollers were also
        /// tried or considered that day and dropped.</summary>
        public enum MenuStyle { Scroll, Paper }

        internal static MenuStyle Style => BodyTuning.PauseMenuStyle != null ? BodyTuning.PauseMenuStyle.Value : MenuStyle.Paper;

        /// <summary>
        /// A flat sheet of the game's own button paper behind a group of plates: a clone of the button row with its
        /// text and clicks removed and its emission (the only thing that colors that paper) set lighter, like the
        /// shipyard's backing sheet. Measured once at unit scale, then scaled to whatever it backs.
        /// </summary>
        class Paper
        {
            public GameObject Go;
            public Vector3 Center1;   // plate center at unit scale, panel-local, relative to the clone's position
            public Vector2 Size1;     // plate size at unit scale, panel-local
        }

        // Paper style, from the 2026-11-14 design pass. The game's own paper materials (shipyard) have a BLACK base
        // color, so emission alone sets the tone and scene light cannot wash it out; the sheet does the same.
        static readonly Color PaperEmission = new Color(0.66f, 0.59f, 0.54f, 1f);   // parchment, about #A8978A
        static readonly Color StripEmission = new Color(0.56f, 0.49f, 0.44f, 1f);   // folded ends, about #8E7D70
        const float PaperMarginX = 0.165f;   // 46 px each side of the plates
        const float PaperMarginY = 0.148f;   // 41 px above the first plate and below the last
        const float SheetMargin = 0.072f;    // 20 px round a mod sheet's rows (co-op's crew list)
        const float SheetGap = 0.057f;       // 16 px between the menu sheet and a mod sheet docked beside it
        const float StripHeight = 0.100f;    // 28 px
        const float StripShow = 0.036f;      // 10 px of strip beyond the sheet's top and bottom edges
        const float StripOverhangX = 0.014f; // 4 px wider than the sheet each side
        // In this rig a SMALLER panel-local z is nearer the camera (found 2026-11-14: a sheet placed at the plates'
        // min z minus a bit covered them). Paper goes at max z plus this; the end strips one step further back.
        const float PaperBehind = 0.01f;
        static Paper _stripTop, _stripBottom;
        // Mod sheet rows whose plates the Paper style hides (names are not buttons), to restore exactly those.
        static readonly HashSet<Renderer> _sheetHiddenPlates = new HashSet<Renderer>();
        static readonly HashSet<Renderer> _mainScroll = new HashSet<Renderer>();
        static Paper _mainPaper;
        static readonly List<ClonedSheet> _sheets = new List<ClonedSheet>();

        // ---- state -------------------------------------------------------------------------------------

        static GameObject _panel;
        static MonoBehaviour _startMenu;
        static Transform _startUI;
        static Transform _buttonTemplate;
        static readonly List<Entry> _entries = new List<Entry>();
        static bool _builtIns;

        /// <summary>Name of the inactive button row kept on the panel to build later buttons from.</summary>
        internal const string TemplateName = "modpause_button_template";

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
            AddEntry(entry);
        }

        // The registry write itself. RegisterBuiltIns calls this rather than Register, which would recurse
        // back into RegisterBuiltIns. Logging tolerates a null Plugin.Log, because another mod can reach this
        // from its own Awake before this plugin's Awake has run.
        static void AddEntry(Entry entry)
        {
            _entries.RemoveAll(e => e.Id == entry.Id);
            _entries.Add(entry);
            _entries.Sort((a, b) => a.Order.CompareTo(b.Order));
            Plugin.Log?.LogInfo($"[PauseMenu] Registered button '{entry.Id}' at order {entry.Order} ({_entries.Count} total)");
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
            if (t != null)
            {
                // Destroy is deferred to the end of the frame. Rename and hide the button first, so a Register
                // of the same id in this frame builds a fresh one instead of finding this one.
                t.name = id + "_removed";
                t.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(t.gameObject);
            }
            Plugin.Log?.LogInfo($"[PauseMenu] Unregistered button '{id}'");
            if (IsOpen) LayoutButtons();
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
                Plugin.Log?.LogInfo($"[PauseMenu] Visibility rule set on '{id}'");
                return;
            }
            Plugin.Log?.LogWarning($"[PauseMenu] SetVisible: no button '{id}'");
        }

        static void RegisterBuiltIns()
        {
            if (_builtIns) return;
            _builtIns = true;

            AddEntry(new Entry
            {
                Id = Resume,
                Order = Order.Resume,
                Label = () => "Resume",
                OnClick = () => InvokeStartMenu("SettingsToGame"), // unpause; its postfix hides our panel
            });

            AddEntry(new Entry
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

            AddEntry(new Entry
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

            AddEntry(new Entry
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

            AddEntry(new Entry
            {
                Id = Quit,
                Order = Order.Quit,
                Label = () => "Quit Game",
                OnClick = () =>
                {
                    SubPageFromPause = true; // so cancelling the confirm returns to our panel
                    // Open the dialog directly, as Settings and Recover do. ButtonClick(QuitMenu) ignores the
                    // click while vanilla's animsPlaying counter is held (the press-F prompt after a load holds
                    // it), and hiding the panel first left nothing on screen. The dialog's own Quit still goes
                    // through ButtonClick, so the save on quit and any mod hooked on it run as before; while the
                    // counter is held, OnQuitHeldOff does vanilla's quit in its place.
                    try { InvokeStartMenu("EnableQuitConfirmMenu"); }
                    catch (Exception e) { Plugin.Log.LogWarning("[PauseMenu] Could not open the quit confirmation: " + e.Message); }
                    if (SubPanelActive("confirmQuitUI")) Hide();
                    else
                    {
                        // Keep the parchment up rather than leave a blank paused screen.
                        SubPageFromPause = false;
                        Plugin.Log.LogWarning("[PauseMenu] The quit confirmation did not open");
                    }
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
                _panelBasePos = startUI.localPosition;
                _panel.transform.localRotation = startUI.localRotation;
                _scrollScale = startUI.localScale;
                _stretch = PanelStretchY;
                _sheets.Clear();
                _panel.transform.localScale = new Vector3(_scrollScale.x, _scrollScale.y * _stretch, _scrollScale.z);
                RaisePanel();

                var template = MenuUtil.FindChild(_panel.transform, "button new game");
                if (template == null)
                {
                    // Do not leave a live clone of the title menu around: its vanilla New Game and Quit
                    // buttons would fire real actions. Destroy and null it so OnPauseOpened can retry rather
                    // than caching a broken panel.
                    Plugin.Log.LogWarning("[PauseMenu] template button not found");
                    UnityEngine.Object.Destroy(_panel); _panel = null; _buttonTemplate = null; return;
                }

                // Record the vanilla buttons (by their StartMenuButton component's parent) BEFORE adding
                // ours. Robust to however they are nested under the scroll; name-based stripping missed
                // them, which left the originals overlapping our column.
                var vanilla = new List<GameObject>();
                foreach (var smb in _panel.GetComponentsInChildren<StartMenuButton>(true))
                {
                    var btn = (smb.transform.parent != null) ? smb.transform.parent.gameObject : smb.gameObject;
                    if (!vanilla.Contains(btn)) vanilla.Add(btn);
                }
                CollectScroll(_panel.transform, vanilla, _mainScroll);
                _mainPaper = _stripTop = _stripBottom = null;

                // A private copy to build buttons from later. The row found above is one of the title buttons
                // removed below, so it cannot serve as the template once this frame ends. The copy stays
                // inactive, and the click prefix refuses to run its vanilla New Game.
                var keep = UnityEngine.Object.Instantiate(template.gameObject, _panel.transform);
                keep.name = TemplateName;
                keep.SetActive(false);
                _buttonTemplate = keep.transform;

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
                _buttonTemplate = null;
            }
        }

        static void EnsureButtons()
        {
            if (_panel == null || _buttonTemplate == null) return;
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (MenuUtil.FindChild(_panel.transform, e.Id) != null) continue;
                MenuUtil.EnsureButton(_panel.transform, _buttonTemplate, e.Id,
                    new Vector3(ColX, ColTopY - ColStep * i, ColZ));
                // The template is inactive, so the clone is too. Start it in the state Refresh would choose,
                // so a hidden entry never shows or takes a place in the column.
                var made = MenuUtil.FindChild(_panel.transform, e.Id);
                if (made != null) made.gameObject.SetActive(SafeVisible(e));
            }
        }

        static bool SafeVisible(Entry e)
        {
            if (e.Visible == null) return true;
            try { return e.Visible(); }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[PauseMenu] '{e.Id}' Visible threw: {ex.Message}");
                return true;
            }
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
                scroll.transform.localRotation = Quaternion.identity; // inherit the panel's player-facing aim
                // Counter the panel's vertical stretch so the extra sheet is not squashed, and keep it where the
                // caller put it when a long button list stretches the panel further (PlaceSheet).
                var sheet = new ClonedSheet { T = scroll.transform, Pos = localPos, Scale = scale, Scroll = new HashSet<Renderer>(), ScrollAll = new HashSet<Renderer>() };
                _sheets.Add(sheet);
                PlaceSheet(sheet);
                var stripped = new List<GameObject>();
                foreach (var smb in scroll.GetComponentsInChildren<StartMenuButton>(true))
                {
                    var btn = (smb.transform.parent != null) ? smb.transform.parent.gameObject : smb.gameObject;
                    if (!stripped.Contains(btn)) stripped.Add(btn);
                }
                CollectScroll(scroll.transform, stripped, sheet.Scroll);
                foreach (var r in scroll.GetComponentsInChildren<Renderer>(true))
                {
                    bool mine = true;
                    foreach (var g in stripped) if (r.transform.IsChildOf(g.transform)) { mine = false; break; }
                    if (mine) sheet.ScrollAll.Add(r);
                }
                foreach (var btn in stripped) UnityEngine.Object.Destroy(btn);
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
                EnsureButtons(); // anything registered since the last open that has no button yet
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

                bool visible = SafeVisible(e);
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
            ApplyStyle();
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

        /// <summary>
        /// Quit in the confirmation our Quit Game button opened, while vanilla's animsPlaying counter is held. The press-F
        /// prompt after a load holds it until F is pressed, and ButtonClick ignores every click while it is held, so the
        /// dialog's Quit did nothing there. Runs vanilla's own quit (save if a save is allowed, then quit). Returns true if
        /// it handled the click; any other time vanilla's ButtonClick does it as before.
        /// </summary>
        public static bool OnQuitHeldOff(MonoBehaviour startMenu, StartMenuButtonType button)
        {
            _startMenu = startMenu;
            if (button != StartMenuButtonType.Quit || !SubPageFromPause || !SubPanelActive("confirmQuitUI")) return false;
            int held;
            try { held = Traverse.Create(startMenu).Field("animsPlaying").GetValue<int>(); } catch { return false; }
            if (held <= 0) return false;
            Plugin.Log.LogInfo("[PauseMenu] Quit while the start screen holds the menu; quitting as the game would");
            if (SaveLoadManager.readyToSave) SaveLoadManager.instance.SaveGame(compressed: true);
            Application.Quit();
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
        /// Evenly spaced; grouping by gaps was tried 2026-11-14 and dropped (small plates with big gaps looked
        /// odd). Distances below are SCREEN units: local Y times the panel's stretch, so they stay the same on
        /// screen whatever the stretch is.
        ///
        /// When the column is taller than the six-button design (BaseColumnExtent), the PARCHMENT grows to hold
        /// it, up to MaxStretchY, and every child is counter-scaled by the current stretch so plates and sheets
        /// keep their shape. Past the cap, plates and gaps shrink together so nothing overlaps or leaves the
        /// scroll.
        /// </summary>
        static void LayoutButtons()
        {
            if (_panel == null) return;

            _visibleScratch.Clear();
            for (int i = 0; i < _entries.Count; i++)
            {
                var b = MenuUtil.FindChild(_panel.transform, _entries[i].Id);
                if (b != null && b.gameObject.activeSelf) _visibleScratch.Add(b);
            }
            int visible = _visibleScratch.Count;
            if (visible == 0) return;

            bool paperLayout = Style == MenuStyle.Paper;
            float scale = (paperLayout ? PaperPlateScale : PlateScale) * Mathf.Clamp(BodyTuning.MenuButtonScale != null ? BodyTuning.MenuButtonScale.Value : 1f, 0.5f, 1f);
            float plate = PlateHeight * scale;

            // Offsets of each plate's center from the first one, in screen units at full size.
            _offsetScratch.Clear();
            float off = 0f;
            for (int i = 0; i < visible; i++)
            {
                if (i > 0) off += plate + (paperLayout ? PaperGap : InnerGap);
                _offsetScratch.Add(off);
            }
            float extent = off + plate;

            // Grow the parchment until the space between the rollers holds the column plus a margin at each end.
            float needed = (extent + 2f * RollerMargin) / InnerPerStretch;
            float newStretch = Mathf.Clamp(needed, PanelStretchY, MaxStretchY);
            float shrink = needed > MaxStretchY ? MaxStretchY / needed : 1f;
            scale *= shrink;

            if (!Mathf.Approximately(newStretch, _stretch))
            {
                _stretch = newStretch;
                _panel.transform.localScale = new Vector3(_scrollScale.x, _scrollScale.y * _stretch, _scrollScale.z);
                RaisePanel();
                for (int i = _sheets.Count - 1; i >= 0; i--)
                {
                    if (_sheets[i].T == null) { _sheets.RemoveAt(i); continue; }
                    PlaceSheet(_sheets[i]);
                }
            }

            // Centered on the column midpoint; screen units back to local by dividing by the stretch.
            float half = off * shrink * 0.5f;
            for (int i = 0; i < visible; i++)
            {
                var b = _visibleScratch[i];
                float y = ColumnCenterLocal + (half - _offsetScratch[i] * shrink) / _stretch;
                b.localPosition = new Vector3(ColX, y, ColZ);
                // Undo the parchment's vertical stretch so the button plate stays square.
                b.localScale = new Vector3(scale, scale / _stretch, scale);
            }
        }

        /// <summary>
        /// Moves the panel up so the scroll's middle sits on the screen's middle. As built it hangs low, with its
        /// pivot 870 px down a 1080 px screen. The parent is StartMenu, which MoveMenuToPlayer turns to the
        /// camera, so its local Y is screen up, and one screen unit is _scrollScale.y of its local units.
        /// </summary>
        static void RaisePanel()
        {
            float px = PivotPx - ScreenMidPx - ScrollMidPerStretchPx * _stretch;
            _panel.transform.localPosition = _panelBasePos + Vector3.up * (px / PxPerUnit * _scrollScale.y);
        }

        static readonly List<Transform> _visibleScratch = new List<Transform>();
        static readonly List<float> _offsetScratch = new List<float>();

        // ---- style application --------------------------------------------------------------------------

        static void CollectScroll(Transform root, List<GameObject> exclude, HashSet<Renderer> into)
        {
            into.Clear();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue;
                bool skip = false;
                foreach (var g in exclude)
                    if (g != null && r.transform.IsChildOf(g.transform)) { skip = true; break; }
                if (!skip) into.Add(r);
            }
        }

        static void SetRenderers(HashSet<Renderer> set, bool on)
        {
            foreach (var r in set) if (r != null && r.enabled != on) r.enabled = on;
        }

        /// <summary>Runs after every layout. Scroll: the vanilla look. Paper: the scroll hidden, the plates on a sheet
        /// of the game's button paper with darker folded ends, and mod sheets (co-op's crew list) as plain paper tags
        /// whose rows are text only, since names are not buttons.</summary>
        static void ApplyStyle()
        {
            var style = Style;
            bool paperStyle = style == MenuStyle.Paper;
            SetRenderers(_mainScroll, !paperStyle);
            UpdatePaper(ref _mainPaper, paperStyle, _visibleScratch, PaperMarginX, PaperMarginY / _stretch, PaperEmission);
            UpdateStrips(paperStyle);
            if (!paperStyle) RestoreSheetPlates();

            // Where a mod sheet docks in Paper style: right of the menu sheet, its first row level with the first plate.
            Vector3 mainMin, mainMax = Vector3.zero;
            bool dock = paperStyle && _mainPaper != null && _mainPaper.Go != null && _mainPaper.Go.activeSelf
                        && PlateBounds(_visibleScratch, out mainMin, out mainMax);
            float dockLeft = 0f, dockTop = 0f;
            if (dock)
            {
                var mt = _mainPaper.Go.transform;
                float right = mt.localPosition.x + (_mainPaper.Center1.x + _mainPaper.Size1.x * 0.5f) * mt.localScale.x;
                dockLeft = right + SheetGap + SheetMargin;
                dockTop = mainMax.y;
            }

            for (int i = _sheets.Count - 1; i >= 0; i--)
            {
                var sh = _sheets[i];
                if (sh.T == null)
                {
                    if (sh.Paper != null && sh.Paper.Go != null) UnityEngine.Object.Destroy(sh.Paper.Go);
                    _sheets.RemoveAt(i);
                    continue;
                }
                bool paper = paperStyle;
                bool shown = sh.T.gameObject.activeSelf;
                SetRenderers(sh.Scroll, !paper);
                if (!paper) PlaceSheet(sh); // back where its mod put it
                _rowScratch.Clear();
                if (paper && shown)
                {
                    for (int c = 0; c < sh.T.childCount; c++)
                    {
                        var child = sh.T.GetChild(c);
                        if (!child.gameObject.activeSelf || HoldsScroll(child, sh.ScrollAll)) continue;
                        _rowScratch.Add(child);
                        HideRowPlates(child);
                    }
                    if (dock && PlateBounds(_rowScratch, out var rowMin, out var rowMax))
                    {
                        var lp = sh.T.localPosition;
                        sh.T.localPosition = new Vector3(lp.x + (dockLeft - rowMin.x), lp.y + (dockTop - rowMax.y), lp.z);
                    }
                }
                UpdatePaper(ref sh.Paper, paper && shown, _rowScratch, SheetMargin, SheetMargin / _stretch, PaperEmission);
            }
        }

        static readonly List<Transform> _rowScratch = new List<Transform>();
        static readonly List<Transform> _rowScratch2 = new List<Transform>();

        static bool HoldsScroll(Transform t, HashSet<Renderer> scroll)
        {
            foreach (var r in scroll) if (r != null && r.transform.IsChildOf(t)) return true;
            return false;
        }

        static void HideRowPlates(Transform row)
        {
            foreach (var mr in row.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr.GetComponent<TextMesh>() != null || !mr.enabled) continue;
                mr.enabled = false;
                _sheetHiddenPlates.Add(mr);
            }
        }

        static void RestoreSheetPlates()
        {
            if (_sheetHiddenPlates.Count == 0) return;
            foreach (var r in _sheetHiddenPlates) if (r != null) r.enabled = true;
            _sheetHiddenPlates.Clear();
        }

        /// <summary>The darker folded ends of the main sheet, behind it, showing StripShow beyond its top and bottom.</summary>
        static void UpdateStrips(bool show)
        {
            bool on = show && _mainPaper != null && _mainPaper.Go != null && _mainPaper.Go.activeSelf;
            if (!on)
            {
                if (_stripTop != null && _stripTop.Go != null && _stripTop.Go.activeSelf) _stripTop.Go.SetActive(false);
                if (_stripBottom != null && _stripBottom.Go != null && _stripBottom.Go.activeSelf) _stripBottom.Go.SetActive(false);
                return;
            }
            if (_stripTop == null || _stripTop.Go == null) _stripTop = MakePaper(StripEmission, "modpause_paper_strip_top");
            if (_stripBottom == null || _stripBottom.Go == null) _stripBottom = MakePaper(StripEmission, "modpause_paper_strip_bottom");
            if (_stripTop == null || _stripBottom == null) return;

            // The main sheet's edges, from its own placement.
            var t = _mainPaper.Go.transform;
            float sx = t.localScale.x, sy = t.localScale.y;
            float cx = t.localPosition.x + _mainPaper.Center1.x * sx;
            float cy = t.localPosition.y + _mainPaper.Center1.y * sy;
            float halfW = _mainPaper.Size1.x * sx * 0.5f, halfH = _mainPaper.Size1.y * sy * 0.5f;
            float z = t.localPosition.z + _mainPaper.Center1.z + PaperBehind;
            float h = StripHeight / _stretch, show2 = StripShow / _stretch;
            float w = 2f * (halfW + StripOverhangX);
            PlaceStrip(_stripTop, cx, cy + halfH + show2 - h * 0.5f, w, h, z);
            PlaceStrip(_stripBottom, cx, cy - halfH - show2 + h * 0.5f, w, h, z);
        }

        static void PlaceStrip(Paper p, float cx, float cy, float w, float h, float z)
        {
            float sx = w / p.Size1.x, sy = h / p.Size1.y;
            var t = p.Go.transform;
            t.localScale = new Vector3(sx, sy, 1f);
            t.localPosition = new Vector3(cx - p.Center1.x * sx, cy - p.Center1.y * sy, z - p.Center1.z);
            if (!p.Go.activeSelf) p.Go.SetActive(true);
        }

        /// <summary>Sizes and places a paper sheet behind the plates of these rows, in panel-local space.</summary>
        static void UpdatePaper(ref Paper p, bool show, List<Transform> rows, float marginX, float marginY, Color tint)
        {
            Vector3 min, max;
            if (!show || rows.Count == 0 || !PlateBounds(rows, out min, out max))
            {
                if (p != null && p.Go != null && p.Go.activeSelf) p.Go.SetActive(false);
                return;
            }
            if (p == null || p.Go == null) p = MakePaper(tint, "modpause_paper");
            if (p == null) return;

            float w = (max.x - min.x) + 2f * marginX;
            float h = (max.y - min.y) + 2f * marginY;
            float sx = w / p.Size1.x, sy = h / p.Size1.y;
            var t = p.Go.transform;
            t.localScale = new Vector3(sx, sy, 1f);
            t.localPosition = new Vector3(
                (min.x + max.x) * 0.5f - p.Center1.x * sx,
                (min.y + max.y) * 0.5f - p.Center1.y * sy,
                max.z + PaperBehind - p.Center1.z);
            if (!p.Go.activeSelf) p.Go.SetActive(true);
        }

        static Paper MakePaper(Color tint, string name)
        {
            if (_buttonTemplate == null || _panel == null) return null;
            try
            {
                var go = UnityEngine.Object.Instantiate(_buttonTemplate.gameObject, _panel.transform);
                go.name = name;
                foreach (var smb in go.GetComponentsInChildren<StartMenuButton>(true)) UnityEngine.Object.DestroyImmediate(smb);
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
                foreach (var tm in go.GetComponentsInChildren<TextMesh>(true))
                {
                    tm.text = "";
                    var tr = tm.GetComponent<Renderer>();
                    if (tr != null) tr.enabled = false;
                }
                foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (mr.GetComponent<TextMesh>() != null) continue;
                    var m = mr.material;
                    if (m == null) continue;
                    if (m.HasProperty("_Color")) m.SetColor("_Color", Color.black);
                    if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", tint);
                }
                go.transform.localRotation = _buttonTemplate.localRotation;
                go.transform.localScale = Vector3.one;
                go.transform.localPosition = Vector3.zero;

                _rowScratch2.Clear();
                _rowScratch2.Add(go.transform);
                Vector3 min, max;
                if (!PlateBounds(_rowScratch2, out min, out max) || max.x - min.x < 1e-4f || max.y - min.y < 1e-4f)
                {
                    Plugin.Log.LogWarning("[PauseMenu] paper sheet: could not measure the button plate");
                    UnityEngine.Object.Destroy(go);
                    return null;
                }
                go.SetActive(false);
                return new Paper
                {
                    Go = go,
                    Center1 = (min + max) * 0.5f,
                    Size1 = new Vector2(max.x - min.x, max.y - min.y),
                };
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[PauseMenu] paper sheet failed: " + e);
                return null;
            }
        }

        /// <summary>Panel-local bounds of the plate meshes (not the text) under these rows, enabled or not, so a
        /// text-only row such as co-op's "Crew:" header still counts at a plate's size.</summary>
        static bool PlateBounds(List<Transform> rows, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            var panel = _panel.transform;
            foreach (var row in rows)
            {
                if (row == null) continue;
                foreach (var mr in row.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (mr.GetComponent<TextMesh>() != null) continue;
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var b = mf.sharedMesh.bounds;
                    for (int k = 0; k < 8; k++)
                    {
                        var corner = new Vector3(
                            (k & 1) == 0 ? b.min.x : b.max.x,
                            (k & 2) == 0 ? b.min.y : b.max.y,
                            (k & 4) == 0 ? b.min.z : b.max.z);
                        var lp = panel.InverseTransformPoint(mr.transform.TransformPoint(corner));
                        min = Vector3.Min(min, lp);
                        max = Vector3.Max(max, lp);
                    }
                    any = true;
                }
            }
            return any;
        }

        /// <summary>Puts a cloned sheet where it sits on screen at the base stretch, whatever the stretch is now.</summary>
        static void PlaceSheet(ClonedSheet sheet)
        {
            float k = PanelStretchY / _stretch;
            sheet.T.localPosition = new Vector3(sheet.Pos.x, sheet.Pos.y * k, sheet.Pos.z);
            sheet.T.localScale = new Vector3(sheet.Scale, sheet.Scale / _stretch, sheet.Scale);
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

        static bool SubPanelActive(string field)
        {
            if (_startMenu == null) return false;
            var go = Traverse.Create(_startMenu).Field(field).GetValue<GameObject>();
            return go != null && go.activeInHierarchy;
        }
    }
}

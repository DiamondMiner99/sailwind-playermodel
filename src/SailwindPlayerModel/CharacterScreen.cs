using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// The character screen: pick which Synty modular parts your avatar wears.
    ///
    /// OPENED FROM THE PAUSE MENU and nowhere else. The title screen has no pause menu, and that
    /// constraint is convenient rather than limiting here: the avatar body template is CLONED FROM A PORT
    /// NPC IN THE LOADED WORLD, so at the title screen there is no world, no template, and therefore nothing
    /// to preview or even enumerate. Being in-game is a precondition of this screen working at all.
    ///
    /// CURSOR: this panel does no cursor bookkeeping, and that is the design, not an omission. Vanilla's
    /// pause (StartMenu.GameToSettings) has already unlocked and shown the cursor and set
    /// GameState.inCursorMenu, so the cursor is free before we ever draw. A panel that can appear during
    /// gameplay has to snapshot and restore cursor state, and that snapshot going stale is a real bug
    /// class - it can hand the player a free cursor while the camera still turns. Rather than repeat that
    /// machinery and its hazard, this screen simply REFUSES TO EXIST outside a cursor menu: it will not open
    /// unless GameState.inCursorMenu, and a per-frame watchdog closes it the moment that stops being true
    /// (resume, quit to menu, anything). A welded, cursor-stealing panel is then not merely handled, it is
    /// unreachable.
    ///
    /// ESCAPE: handled here and CONSUMED, because ModPauseMenu.OnEscape would otherwise resume the game
    /// in the same frame - Update runs before StartMenu's LateUpdate, so both would fire on one keypress
    /// and Escape would close the screen AND unpause.
    ///
    /// THE PARCHMENT IS HIDDEN while this is open. Leaving it visible behind a screen-space IMGUI panel
    /// lets a click pass through to the world-space button underneath - and one of those is Quit Game.
    /// </summary>
    public static class CharacterScreen
    {
        private static bool _open;
        private static Vector2 _scroll;
        private static bool _stylesBuilt;
        private static bool _dirty;                 // choices changed since opening
        private static bool _normalized;            // slot indices folded into the live rig's ranges
        private static PlayerAppearance _working;     // edited copy; committed when the screen closes
        private static GUIStyle _panel, _title, _label, _button, _small, _row, _rowOn, _field;
        private static Texture2D _panelTex, _titleTex, _rowHoverTex;

        // OUTFITS. The picked row is the one Update, Rename and Delete act on; -1 is "Current look". _currentLook holds the
        // look that was on before the last outfit was tried, and clicking its row swaps that with what is on now, so the
        // last unsaved look is always a click away. It is one slot: unsaved edits to an outfit replace the look kept there
        // once another row is clicked. _pickedBaseline is the picked outfit as this rig wears it, which is what "edited" is
        // measured against: a saved line can be spelled differently (a shared line with keys missing, or written by an
        // older version) and still be the same look. A click only records what to do (_outfitAction), and the next Layout
        // event does it: the rows and buttons below a click must not change between the Layout pass that placed them and
        // the event still walking them.
        private const string CurrentLookText = "Current look", ConfirmText = "Sure?";
        private const float ConfirmSeconds = 3f;
        private const string NameFieldControl = "PlayerModel_OutfitName";
        private enum OutfitConfirm { None, Update, Delete }
        private static int _picked = -1;
        private static string _currentLook, _pickedBaseline;
        private static bool _baselinePending, _outfitTrouble;
        private static Vector2 _outfitScroll;
        private static System.Action _outfitAction;
        private static OutfitConfirm _confirm;
        private static float _confirmUntil;
        private static bool _renaming, _focusName, _columnHiddenLogged;
        private static string _nameEdit = "";
        private static float _outfitButtonW = 100f;

        // SIZE. Everything is laid out in 1080p pixels times _s, the scale the styles were last built at. It
        // is worked out on Layout events only, and only when the screen size or the setting changed, so the
        // Layout pass and the Repaint and mouse events after it always see the same numbers.
        private const float MinScale = 0.5f, MaxScale = 4f;
        private static float _s = 1f;
        private static float _sizedSetting = -1f;
        private static int _sizedWidth = -1, _sizedHeight = -1;
        private static float _loggedScale = -1f, _scaleLoggedAt;
        private static bool _logScale;              // set by Open: the next Layout logs the scale once
        private static bool _scaleFailed;
        // Column and button widths at _s. The text columns are measured in the font actually in use, so a
        // long color name such as "Dark brown" is never cut off.
        private static float _labelW = 130f, _valueW = 90f, _arrowW = 34f, _randomW = 120f, _doneW = 140f;
        private const string DoneText = "Done  (Esc)";
        // The stock skin with only its scrollbars sized to _s. GUI.skin goes back to the stock skin at the
        // start of every OnGUI call, so this never reaches another mod's panel. Null keeps the stock skin.
        private static GUISkin _skin;
        private static bool _skinFailed;
        // One solid texture per (color slot, value), so the swatches are not re-baked every frame.
        private static readonly System.Collections.Generic.Dictionary<string, Texture2D> _swatches =
            new System.Collections.Generic.Dictionary<string, Texture2D>();

        public static bool IsOpen { get { return _open; } }

        /// <summary>An outfit's name is being typed, so the keys pressed are letters and not for the game.</summary>
        public static bool IsTyping { get { return _open && _renaming; } }

        // The rows in the order they are drawn, by slot key: who the character is, then what they wear on the head,
        // then clothes, then gear. Slots are appended to PlayerAppearance in the order they were added, which is
        // not the order a player works down a screen. A slot missing here is drawn at the end.
        private static readonly string[] DisplayOrder =
        {
            "gender", "head", "hair", "eyebrow", "facialhair",
            "headgear", "hood", "face",
            "torso", "gloves", "hips", "legs",
            "back", "shoulders", "elbows", "knees", "belt",
        };
        // Where the gear rows start, so a little space sets them apart.
        private const string FirstGearRow = "back";
        private static int[] _order;

        private static int[] Order()
        {
            if (_order != null) return _order;
            var list = new System.Collections.Generic.List<int>();
            foreach (var key in DisplayOrder)
                for (int i = 0; i < PlayerAppearance.SlotCount; i++)
                    if (PlayerAppearance.Slots[i].Key == key) { list.Add(i); break; }
            for (int i = 0; i < PlayerAppearance.SlotCount; i++)
                if (!list.Contains(i)) list.Add(i);
            _order = list.ToArray();
            return _order;
        }

        private static int _keyConsumedFrame = -1;

        /// <summary>
        /// True if this screen swallowed a pause key this frame. Read by the StartMenu.LateUpdate prefix:
        /// MenuDriver.Update runs BEFORE LateUpdate, so the latch is still fresh when the prefix checks it.
        /// Without it, the same keypress that closes this screen also reaches vanilla and unpauses the
        /// game - the doc's claim that one press does one thing was simply not true, because hiding the
        /// parchment had already disarmed ModPauseMenu.OnEscape.
        /// </summary>
        public static bool ConsumedPauseKeyThisFrame { get { return _keyConsumedFrame == Time.frameCount; } }

        /// <summary>
        /// One line of context under the heading, or null for none. Standalone there is nothing useful to
        /// say, so nothing is shown; the co-op mod fills this in with whether the crew can see you, which is
        /// the part a player cannot work out from the preview on the right.
        /// </summary>
        public static System.Func<string> StatusLine;

        static string SafeStatusLine()
        {
            var f = StatusLine;
            if (f == null) return null;
            try { return f(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] status line threw: " + e.Message); return null; }
        }

        /// <summary>Open the screen. Refuses outside a cursor menu - see the class doc.</summary>
        public static void Open()
        {
            try
            {
                if (!GameState.inCursorMenu)
                {
                    Plugin.Log.LogWarning("[Character] Refused to open outside a cursor menu.");
                    return;
                }
                // Take a private COPY of the array. PlayerAppearance is a struct holding a byte[], so a plain
                // assignment would share the array with Plugin's stored appearance and every registry
                // entry - editing here would mutate them in place behind their backs.
                _working = PlayerAppearance.Deserialize(PlayerModel.LocalAppearance.Serialize());
                FitWorking();
                _dirty = false;
                _scroll = Vector2.zero;
                _logScale = true;
                OutfitStore.Load();
                _currentLook = _working.Serialize();
                _picked = FindOutfitWorn();
                _pickedBaseline = _picked >= 0 ? _currentLook : null;
                _baselinePending = false;
                _outfitTrouble = OutfitStore.LoadFailed;
                _outfitScroll = Vector2.zero;
                _outfitAction = null;
                _confirm = OutfitConfirm.None;
                _renaming = false;
                _open = true;
                ModPauseMenu.Hide();   // never leave clickable world-space buttons behind this panel
                CharacterPreviewStudio.Open();
                CharacterPreviewStudio.SetAppearance(_working);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Open failed: " + e.Message); }
        }

        /// <summary>Make a look just loaded into the working copy read as choices this screen can show and step from.</summary>
        private static void FitWorking()
        {
            // Gender has no "none". A look nobody has saved carries 0, and Apply draws anything but 2 as
            // male, so show it as Male. Wrapping it like the other slots turned 0 into Female.
            _working[0] = (byte)(_working[0] == 2 ? 2 : 1);
            // Fold indices into this rig's real ranges up front, so the screen never shows a number
            // like "174 / 8" that the body is not actually wearing (Apply wraps, the display did not).
            // With no body yet, Draw does it when the body appears.
            _normalized = false;
            var c0 = PlayerModel.GetCustomizer();
            if (c0 != null) NormalizeWorking(c0);
            FitColors(ref _working);
        }

        /// <summary>
        /// Colors saved as 0 mean "the NPC's own"; show them as the nearest named color so every row reads as a choice
        /// and stepping starts from where the player actually is.
        /// </summary>
        private static void FitColors(ref PlayerAppearance look)
        {
            for (int i = 0; i < PlayerAppearance.ColorSlotCount; i++)
                if (look.GetColor(i) == 0)
                {
                    int near = PlayerAppearance.NearestPaletteIndex(i);
                    if (near > 0) look.SetColor(i, (byte)near);
                }
        }

        /// <summary>
        /// Fold slot indices into this rig's real ranges. Gender (slot 0) was mapped in Open and is never
        /// wrapped here: NormalizeValue would turn an unsaved 0 into Female.
        /// </summary>
        private static void NormalizeWorking(PsychoticLab.CharacterCustomizer c)
        {
            for (int i = 1; i < PlayerAppearance.SlotCount; i++)
            {
                int n = PlayerAppearance.VariantCount(c, i);
                if (n > 0) _working[i] = PlayerAppearance.NormalizeValue(i, n, _working[i]);
            }
            _normalized = true;
        }

        /// <summary>Close without committing. Safe to call at any time, including when already closed.</summary>
        public static void ForceClose()
        {
            if (!_open) return;
            _open = false;
            // Each step in its own try: _open is already false, so a throw here would skip the parchment
            // below, and a second ForceClose would return at once, leaving a blank paused screen.
            // A name being typed is kept, as clicking away from it does, and the movement keys go back.
            try { if (_renaming) FinishRename(true); _outfitAction = null; }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Could not finish the outfit name: " + e.Message); }
            try { CharacterPreviewStudio.Close(); }   // the studio also self-destructs, but do not rely on that
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Could not close the preview: " + e.Message); }
            // Commit on EVERY exit path, not just Done/Escape. The preview has already changed how the
            // player looks locally, so an exit that skipped the commit (the watchdog firing because they
            // resumed, quit to menu, or were dropped from the lobby) would leave them wearing a look their
            // crewmates never heard about and that is gone next launch. Guarded by _dirty, so a look-only
            // visit costs nothing.
            try { Commit(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Could not commit your character: " + e.Message); }
            // Hand the player back the parchment we hid on the way in - but ONLY while still in a cursor
            // menu. The watchdog path below fires precisely BECAUSE the player already left one (resumed,
            // quit to title), and re-showing a menu there would strand a parchment over a resumed game.
            try { if (GameState.inCursorMenu) ModPauseMenu.Reopen(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Could not restore the pause menu: " + e.Message); }
        }

        /// <summary>Driven from MenuDriver.Update. Cheap no-op while closed.</summary>
        public static void Tick()
        {
            if (!_open) return;
            try
            {
                // The watchdog that makes a stranded panel impossible: if we are no longer in a cursor
                // menu, whatever took us out of it (resume, quit, a vanilla screen) owns the cursor now.
                if (!GameState.inCursorMenu) { ForceClose(); return; }

                // ALL THREE vanilla pause keys, not just Escape. Vanilla's StartMenu.LateUpdate also opens
                // on F10 and JoystickButton6, and the mod's interception in PauseMenuPatches is armed by
                // ModPauseMenu.IsOpen - which is FALSE while this screen is up, because opening it hid
                // the parchment. So an unhandled pause key fell straight through to vanilla, which saw no
                // active panel of its own (ours hidden, its settingsUI disabled by us) and re-entered
                // GameToSettings WHILE ALREADY PAUSED. That latches unpausedTimescale = Time.timeScale =
                // 0, and every later resume then writes 0 back: a permanent frozen world, unrecoverable
                // without restarting the game. GameToSettingsPatch's prefix now refuses that second pause
                // as a backstop, but consuming the key here is still what keeps it from reaching vanilla.
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F10) ||
                    Input.GetKeyDown(KeyCode.JoystickButton6))
                {
                    _keyConsumedFrame = Time.frameCount;
                    // Typing an outfit's name, the key only backs out of the name.
                    if (_renaming) FinishRename(false);
                    else ForceClose();   // commits; see ForceClose
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Character] Tick failed, closing: " + e.Message);
                ForceClose();
            }
        }

        private static void Commit()
        {
            if (!_dirty) return;
            _dirty = false;
            // Setting it is enough: PlayerModel persists the choice and re-dresses our own body in place, then
            // raises LocalAppearanceChanged, which co-op listens to so it can tell the crew.
            // Doing any of that again here would re-dress and re-send on every commit.
            PlayerModel.LocalAppearance = _working;
            Plugin.Log.LogInfo("[Character] Appearance committed: " + _working.Serialize());
        }

        /// <summary>
        /// Work out the scale for this screen and rebuild the styles if it changed. Runs on Layout events only.
        ///
        /// It starts from SailwindSkin.UiScale (the screen height and the UIScale setting) and is then held
        /// down twice. First so a 320 px tall panel, about the least that still shows a few rows, fits in the
        /// 80% of the screen the panel may use. Then so the widest row fits the panel's width without a
        /// sideways scrollbar, which is measured from the built styles and so can take another build. Widths
        /// are close to proportional to the scale, so one step down lands inside; the 2% covers font sizes
        /// rounding up. At 1080p and 720p with the setting at 1 neither limit applies and the scale is 1.
        /// </summary>
        private static void UpdateScale()
        {
            if (_scaleFailed)
            {
                if (!_stylesBuilt) BuildStyles(1f, false);
                return;
            }
            try
            {
                float setting = SailwindSkin.UiScale;
                int sw = Screen.width, sh = Screen.height;
                if (!_stylesBuilt || setting != _sizedSetting || sw != _sizedWidth || sh != _sizedHeight)
                {
                    _sizedSetting = setting;
                    _sizedWidth = sw;
                    _sizedHeight = sh;

                    float s = Mathf.Clamp(Mathf.Min(setting, sh * 0.8f / 320f), MinScale, MaxScale);
                    for (int pass = 0; ; pass++)
                    {
                        if (!_stylesBuilt || Mathf.Abs(s - _s) > 0.0005f) BuildStyles(s, true);
                        float need = RequiredPanelWidth(), room = PanelWidth();
                        if (need <= room || _s <= MinScale || pass >= 3) break;
                        s = Mathf.Max(MinScale, _s * room / need * 0.98f);
                    }
                }

                // One line when the screen opens, then at most one every 2 seconds while the scale changes, so
                // dragging a window edge or the setting's slider does not write a line per frame.
                if (_logScale || (Mathf.Abs(_s - _loggedScale) >= 0.05f && Time.unscaledTime - _scaleLoggedAt >= 2f))
                {
                    _logScale = false;
                    _loggedScale = _s;
                    _scaleLoggedAt = Time.unscaledTime;
                    float raw = BodyTuning.UIScale != null ? BodyTuning.UIScale.Value : 1f;
                    Plugin.Log.LogInfo($"[Character] UI scale {_s:F2} (screen {sw}x{sh}, setting {raw:F2}).");
                }
            }
            catch (System.Exception e)
            {
                _scaleFailed = true;
                Plugin.Log.LogWarning("[Character] Could not size the screen for this resolution, using the 1080p layout: " + e.Message);
                BuildStyles(1f, false);
            }
        }

        /// <summary>
        /// Build every style at scale s. Full builds also measure the text columns and size the scrollbars.
        /// The fallback after a failure (full false) is the fixed 1080p layout with the stock scrollbars.
        /// </summary>
        private static void BuildStyles(float s, bool full)
        {
            _s = s;
            // Sailwind's own parchment palette and typeface - see SailwindSkin for where the values come
            // from and why the font is usable at all (the game has no TextMeshPro, so its real
            // UnityEngine.Font assets can be assigned straight onto a GUIStyle).
            // Textures once: styles are rebuilt whenever the scale changes, and a Texture2D per build would leak.
            if (_panelTex == null) _panelTex = SailwindSkin.SolidTexture(SailwindSkin.Parchment);
            if (_titleTex == null) _titleTex = SailwindSkin.SolidTexture(SailwindSkin.ParchmentDark);

            // Called before Draw sets our skin, so this is the stock skin. Its margins are scaled along with
            // everything else rather than inherited as fixed pixels.
            var stock = GUI.skin;

            _panel = new GUIStyle(stock.box) { padding = Offset(18, 18, 14, 14), margin = Scaled(stock.box.margin, s), alignment = TextAnchor.UpperLeft };
            _panel.normal.background = _panelTex;

            _title = new GUIStyle(stock.label)
            {
                fontSize = (int)Px(24f), fontStyle = FontStyle.Bold, wordWrap = true,
                alignment = TextAnchor.MiddleLeft, padding = Offset(12, 12, 8, 8), margin = Scaled(stock.label.margin, s),
            }.WithFont();
            _title.normal.textColor = SailwindSkin.Parchment;   // light on the dark title band
            _title.normal.background = _titleTex;

            _label = new GUIStyle(stock.label)
            {
                fontSize = (int)Px(19f), wordWrap = false, padding = Offset(4, 4, 6, 6), margin = Scaled(stock.label.margin, s),
            }.WithFont();
            _label.normal.textColor = SailwindSkin.InkColor;

            _small = new GUIStyle(_label) { fontSize = (int)Px(16f), fontStyle = FontStyle.Italic, wordWrap = true };
            _small.normal.textColor = SailwindSkin.InkFaint;

            _button = new GUIStyle(stock.button)
            {
                fontSize = (int)Px(19f), padding = Offset(10, 10, 5, 5), margin = Scaled(stock.button.margin, s),
            }.WithFont();
            _button.normal.textColor = SailwindSkin.InkColor;
            _button.hover.textColor = SailwindSkin.ParchmentDark;

            // Outfit rows: plain ink on the parchment, shaded under the mouse, and the picked one in the title band's
            // colors. Clipped rather than wrapped, so a long name cannot push the column sideways.
            if (_rowHoverTex == null)
            {
                Color shade = SailwindSkin.ParchmentDark;
                shade.a = 0.25f;
                _rowHoverTex = SailwindSkin.SolidTexture(shade);
            }
            _row = new GUIStyle(_label)
            {
                alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, padding = Offset(8, 8, 5, 5), margin = Offset(0, 0, 1, 1),
            };
            _row.normal.background = null;
            _row.hover.background = _rowHoverTex;
            _row.hover.textColor = SailwindSkin.InkColor;
            _row.active.background = _rowHoverTex;
            _row.active.textColor = SailwindSkin.InkColor;
            _rowOn = new GUIStyle(_row);
            _rowOn.normal.background = _titleTex;
            _rowOn.normal.textColor = SailwindSkin.Parchment;
            _rowOn.hover.background = _titleTex;
            _rowOn.hover.textColor = SailwindSkin.Parchment;
            _rowOn.active.background = _titleTex;
            _rowOn.active.textColor = SailwindSkin.Parchment;

            _field = new GUIStyle(stock.textField)
            {
                fontSize = (int)Px(19f), padding = Offset(6, 6, 5, 5), margin = Scaled(stock.textField.margin, s), clipping = TextClipping.Clip,
            }.WithFont();

            _outfitButtonW = Px(100f);
            _labelW = Px(130f);
            _valueW = Px(90f);
            _arrowW = Px(34f);
            _randomW = Px(120f);
            _doneW = Px(140f);
            if (full)
            {
                // The old fixed 90 px value column left 82 px for text, and "Dark brown" is 114 px at 19 px, so
                // longer color names were cut off at every resolution. Measure every text a column can show
                // instead, in the real font and size. The slack keeps a last letter that exactly fits from
                // being clipped by rounding. The label column is measured the same way for the same reason.
                float slack = Px(2f);
                foreach (var slot in PlayerAppearance.Slots)
                    _labelW = Mathf.Max(_labelW, TextWidth(_label, slot.Label) + slack);
                foreach (var colorSlot in PlayerAppearance.ColorSlots)
                {
                    _labelW = Mathf.Max(_labelW, TextWidth(_label, colorSlot.Label) + slack);
                    foreach (var option in colorSlot.Palette)
                        _valueW = Mathf.Max(_valueW, TextWidth(_label, option.Name) + slack);
                }
                // Describe's texts, and DescribeColor's for a color it cannot name.
                foreach (var text in new[] { "Female", "Male", "None", "Default", MatchTorsoText, "888 / 888" })
                    _valueW = Mathf.Max(_valueW, TextWidth(_label, text) + slack);
                _randomW = Mathf.Max(_randomW, TextWidth(_button, "Randomize") + slack);
                _doneW = Mathf.Max(_doneW, TextWidth(_button, DoneText) + slack);
                foreach (var text in new[] { "Save new", "Update", "Rename", "Delete", ConfirmText })
                    _outfitButtonW = Mathf.Max(_outfitButtonW, TextWidth(_button, text) + slack);

                BuildSkin(stock, s);
            }
            else if (_skin != null)
            {
                Object.Destroy(_skin);
                _skin = null;
            }

            _stylesBuilt = true;
        }

        private static readonly GUIContent _measure = new GUIContent();

        private static float TextWidth(GUIStyle style, string text)
        {
            _measure.text = text;
            return Mathf.Ceil(style.CalcSize(_measure).x);
        }

        /// <summary>
        /// Size the scrollbars to the scale. IMGUI finds a scrollbar's thumb and arrow buttons by name in
        /// GUI.skin, so passing a bigger scrollbar style to BeginScrollView alone would leave a 15 px thumb in a
        /// wide track; the skin has to carry them. A copy of the stock skin, with fresh scaled copies of the
        /// stock scrollbar styles on each build so nothing is scaled twice. A failure keeps the stock
        /// scrollbars for the session; the mouse wheel scrolls either way.
        /// </summary>
        private static void BuildSkin(GUISkin stock, float s)
        {
            if (_skinFailed || stock == null || stock == _skin) return;
            try
            {
                if (_skin == null)
                {
                    _skin = Object.Instantiate(stock);
                    _skin.hideFlags = HideFlags.HideAndDontSave;
                }
                _skin.verticalScrollbar = Scaled(stock.verticalScrollbar, s);
                _skin.verticalScrollbarThumb = Scaled(stock.verticalScrollbarThumb, s);
                _skin.verticalScrollbarUpButton = Scaled(stock.verticalScrollbarUpButton, s);
                _skin.verticalScrollbarDownButton = Scaled(stock.verticalScrollbarDownButton, s);
                _skin.horizontalScrollbar = Scaled(stock.horizontalScrollbar, s);
                _skin.horizontalScrollbarThumb = Scaled(stock.horizontalScrollbarThumb, s);
                _skin.horizontalScrollbarLeftButton = Scaled(stock.horizontalScrollbarLeftButton, s);
                _skin.horizontalScrollbarRightButton = Scaled(stock.horizontalScrollbarRightButton, s);
            }
            catch (System.Exception e)
            {
                _skinFailed = true;
                try { if (_skin != null) Object.Destroy(_skin); }
                catch { }
                _skin = null;
                Plugin.Log.LogWarning("[Character] Could not size the scrollbars, keeping the stock ones: " + e.Message);
            }
        }

        private static GUIStyle Scaled(GUIStyle stock, float s)
        {
            return new GUIStyle(stock)
            {
                name = stock.name,   // thumbs and arrow buttons are looked up as this name plus a suffix
                fixedWidth = SailwindSkin.Px(stock.fixedWidth, s),
                fixedHeight = SailwindSkin.Px(stock.fixedHeight, s),
                margin = Scaled(stock.margin, s),
                padding = Scaled(stock.padding, s),
                overflow = Scaled(stock.overflow, s),
            };
        }

        private static RectOffset Scaled(RectOffset o, float s)
        {
            return new RectOffset(SailwindSkin.Px(o.left, s), SailwindSkin.Px(o.right, s),
                SailwindSkin.Px(o.top, s), SailwindSkin.Px(o.bottom, s));
        }

        /// <summary>Padding given in 1080p pixels, at the current scale.</summary>
        private static RectOffset Offset(int left, int right, int top, int bottom)
        {
            return new RectOffset((int)Px(left), (int)Px(right), (int)Px(top), (int)Px(bottom));
        }

        /// <summary>A length given in 1080p pixels, at the current scale, in whole pixels.</summary>
        private static float Px(float referencePx)
        {
            return SailwindSkin.Px(referencePx, _s);
        }

        private static float PanelWidth()
        {
            return Mathf.Min(Screen.width * 0.44f, Px(640f));
        }

        /// <summary>
        /// The narrowest panel that shows the widest row without a sideways scrollbar, in IMGUI's own layout
        /// arithmetic: neighbors in a row are spaced by the larger of their facing margins, a GUILayout.Space
        /// adds its width with no margin, the scroll view pads its rows by the larger of its padding and the
        /// rows' outer margins, and it gives up room for a vertical scrollbar, counted here as always shown.
        /// Must match the rows Draw lays out.
        /// </summary>
        private static float RequiredPanelWidth()
        {
            var skin = _skin != null ? _skin : GUI.skin;
            var view = skin.scrollView;
            var bar = skin.verticalScrollbar;
            RectOffset lm = _label.margin, bm = _button.margin, pad = _panel.padding;

            float labelToArrow = Mathf.Max(lm.right, bm.left);
            float slotRow = _labelW + labelToArrow + _arrowW + Mathf.Max(bm.right, lm.left) + _valueW + labelToArrow + _arrowW;
            float colorRow = _labelW + labelToArrow + _arrowW + bm.right + Px(20f) + Px(4f) + lm.left + _valueW + labelToArrow + _arrowW;
            float rows = Mathf.Max(slotRow, colorRow) + Mathf.Max(view.padding.left, lm.left) + Mathf.Max(view.padding.right, bm.right);
            float scroll = rows + bar.fixedWidth + bar.margin.left +
                Mathf.Max(view.margin.left, pad.left) + Mathf.Max(view.margin.right, pad.right);
            float bottom = _randomW + Mathf.Max(bm.right, bm.left) + _doneW + Mathf.Max(bm.left, pad.left) + Mathf.Max(bm.right, pad.right);
            return Mathf.Max(scroll, bottom);
        }

        /// <summary>Draw. Called from MenuDriver.OnGUI.</summary>
        public static void Draw()
        {
            if (!_open) return;
            if (!_stylesBuilt || Event.current.type == EventType.Layout) UpdateScale();
            // Scrollbars at this scale. IMGUI puts the stock skin back before the next OnGUI call, whoever makes it.
            if (_skin != null) GUI.skin = _skin;
            RunOutfitAction();

            // The live customizer on our own body is the only honest source of "how many variants does
            // THIS machine have". No body yet (the world has only just loaded, or the build is still being
            // retried) means no counts, and the screen says so rather than offering choices that would
            // silently do nothing.
            var customizer = PlayerModel.GetCustomizer();
            // The body can finish building while the screen is open (in co-op the world keeps running).
            // Fold the indices then, before any row is drawn or edited. Not a change the player made, so
            // nothing is marked dirty.
            if (customizer != null && !_normalized)
            {
                _normalized = true; // tried once, so a failure cannot repeat on every OnGUI event
                string beforeFit = _working.Serialize();
                try
                {
                    NormalizeWorking(customizer);
                    CharacterPreviewStudio.SetAppearance(_working);
                }
                catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Could not fit the rows to your body: " + e.Message); }
                // An outfit just put on is now as this rig wears it, so that is what counts as unedited. The same goes for
                // a screen opened before the body existed, whose look has only just been fitted.
                string fitted = _working.Serialize();
                if (_baselinePending || beforeFit == _pickedBaseline)
                {
                    _baselinePending = false;
                    _pickedBaseline = fitted;
                }
                if (beforeFit == _currentLook) _currentLook = fitted;
            }

            // Every size below is in 1080p pixels through Px, at the scale UpdateScale settled on. No GUI.matrix,
            // so text is rasterized at its real size and every click is tested against the rect it was drawn in.
            float w = PanelWidth();
            float h = Mathf.Min(Screen.height * 0.8f, Px(660f));
            float x = Screen.width * 0.06f;
            float y = (Screen.height - h) * 0.5f;

            GUI.depth = 0;

            // Where the column to the right of the preview starts; just past the panel when there is no preview yet.
            float columnX = x + w + Screen.width * 0.03f;

            // The character, to the RIGHT of the controls. This is the studio mannequin, not the player's
            // real body - see CharacterPreviewStudio for why the real one cannot be used (its renderers are
            // disabled while this screen is open, and in co-op the world is not paused).
            var preview = CharacterPreviewStudio.Texture;
            if (preview != null)
            {
                float gap = Px(12f), turnH = Px(30f), inset = Px(8f);
                float pw = Mathf.Min(Screen.width * 0.26f, Px(380f));
                // No taller than the panel, and short enough that the Turn row under it stays on screen at any scale.
                float maxPh = Mathf.Min(h, Screen.height - 2f * (gap + turnH));
                // Ask for a texture about as tall as it is drawn. From the fixed shape, not this texture's own,
                // which is off by a rounding: a swap must not move the request into the next step and back.
                CharacterPreviewStudio.RequestHeight(Mathf.Min(pw * CharacterPreviewStudio.HeightPerWidth, maxPh));
                float ph = pw * ((float)preview.height / preview.width);
                if (ph > maxPh) { ph = maxPh; pw = ph * ((float)preview.width / preview.height); }
                float px = x + w + Screen.width * 0.03f;
                float py = y + (h - ph) * 0.5f;

                GUI.Box(new Rect(px - inset, py - inset, pw + 2f * inset, ph + 2f * inset), GUIContent.none, _panel);
                GUI.DrawTexture(new Rect(px, py, pw, ph), preview, ScaleMode.ScaleToFit, false);

                // Turn controls, so a hat can be judged from behind as well as in front.
                float by = py + ph + gap;
                if (GUI.Button(new Rect(px, by, pw * 0.48f, turnH), "< Turn", _button)) CharacterPreviewStudio.Turn(-25f);
                if (GUI.Button(new Rect(px + pw * 0.52f, by, pw * 0.48f, turnH), "Turn >", _button)) CharacterPreviewStudio.Turn(25f);
                columnX = px + pw + inset + gap;
            }

            GUILayout.BeginArea(new Rect(x, y, w, h), _panel);
            GUILayout.Label("Character", _title);
            GUILayout.Space(Px(8f));

            if (customizer == null)
            {
                GUILayout.Label("Your character model has not been built yet. It is built shortly after the world " +
                    "loads. If this stays, resume the game for a moment and open this screen again.", _small);
            }
            else
            {
                // Say something the player cannot see for themselves. The preview panel to the right makes
                // "your changes show up" self-evident; that your CREW sees this is the non-obvious part.
                string status = SafeStatusLine();
                if (status != null)
                {
                    GUILayout.Label(status, _small);
                    GUILayout.Space(Px(6f));
                }
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

                foreach (int i in Order())
                {
                    if (PlayerAppearance.Slots[i].Key == FirstGearRow) GUILayout.Space(Px(10f));
                    int count = PlayerAppearance.VariantCount(customizer, i);
                    if (count <= 1) continue;   // nothing to choose between on this rig; do not show a dead row
                    // Vanilla has no female facial hair, so the row would be a control that visibly does
                    // nothing. Hide it rather than offer a lie.
                    if (PlayerAppearance.Slots[i].Key == "facialhair" && _working[0] == 2) continue;

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(PlayerAppearance.Slots[i].Label, _label, GUILayout.Width(_labelW));

                    if (GUILayout.Button("<", _button, GUILayout.Width(_arrowW))) Step(i, -1, count);
                    GUILayout.Label(Describe(i, count), _label, GUILayout.Width(_valueW));
                    if (GUILayout.Button(">", _button, GUILayout.Width(_arrowW))) Step(i, +1, count);

                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }

                GUILayout.Space(Px(10f));
                // The swatch and the space after it are also counted in RequiredPanelWidth.
                float swatchSize = Px(20f);
                for (int i = 0; i < PlayerAppearance.ColorSlotCount; i++)
                {
                    var slot = PlayerAppearance.ColorSlots[i];
                    int v = _working.GetColor(i);

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(slot.Label, _label, GUILayout.Width(_labelW));

                    if (GUILayout.Button("<", _button, GUILayout.Width(_arrowW))) StepColor(i, -1);
                    var sw = Swatch(i, v);
                    var r = GUILayoutUtility.GetRect(swatchSize, swatchSize, GUILayout.Width(swatchSize), GUILayout.Height(swatchSize));
                    if (sw != null) GUI.DrawTexture(new Rect(r.x, r.y + Px(2f), swatchSize, Px(16f)), sw);
                    GUILayout.Space(Px(4f));
                    GUILayout.Label(PlayerAppearance.DescribeColor(i, v), _label, GUILayout.Width(_valueW));
                    if (GUILayout.Button(">", _button, GUILayout.Width(_arrowW))) StepColor(i, +1);

                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }

                EndScrollScaled();
            }

            GUILayout.Space(Px(8f));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Randomize", _button, GUILayout.Width(_randomW)) && customizer != null)
            {
                // Draw each slot from its REAL range on this rig. The previous version reused
                // DeterministicFor, which is seeded for SteamId spread and emits 0..250 - values that Apply
                // wraps into range on the body but that the readout showed raw, hence rows reading
                // "241 / 23". Randomising within the live count keeps the number on screen equal to the
                // variant actually being worn. Slot 0 (gender) is deliberately left alone.
                for (int i = 1; i < PlayerAppearance.SlotCount; i++)
                {
                    int n = PlayerAppearance.VariantCount(customizer, i);
                    if (n <= 0) continue;
                    // The rows added after the core ones are worn now and then rather than on every roll: a hood,
                    // armor on every joint and a pack at once reads as a costume. Gloves keep to the torso half
                    // the time.
                    if (i >= PlayerAppearance.CoreSlotCount && Random.value > (PlayerAppearance.Slots[i].MatchesTorso ? 0.5f : 0.25f))
                    {
                        _working[i] = 0;
                        continue;
                    }
                    // NormalizeValue keeps each slot inside its OWN legal range, so only the slots that
                    // actually allow it can come out empty - a randomized character is never headless.
                    int lo = i >= PlayerAppearance.CoreSlotCount ? 1 : 0;
                    _working[i] = PlayerAppearance.NormalizeValue(i, n, Random.Range(lo, n + 1));
                }
                for (int i = 0; i < PlayerAppearance.ColorSlotCount; i++)
                    _working.SetColor(i, (byte)Random.Range(1, PlayerAppearance.ColorSlots[i].Palette.Length + 1));
                _dirty = true;
                Preview();
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(DoneText, _button, GUILayout.Width(_doneW)))
            {
                // ForceClose commits inside its own try, so a throw cannot leave this layout's groups
                // unclosed and the screen open. Do not commit here as well.
                ForceClose();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // Done above may just have closed the screen. Outfits need the body's part lists to fit a look to.
            if (_open && customizer != null) DrawOutfits(columnX, y, h);
        }

        /// <summary>
        /// IMGUI scrolls a wheel notch by delta * 20 px whatever the row size, and the rows are _s times their 1080p
        /// height, so hand the scroll view the wheel delta times _s: a notch then moves the same number of rows at any
        /// scale. The raw delta goes back afterwards, used or not, so nothing later in this event sees the scaled one.
        /// At scale 1 the event is not touched.
        /// </summary>
        private static void EndScrollScaled()
        {
            var ev = Event.current;
            bool wheel = ev != null && ev.type == EventType.ScrollWheel && _s != 1f;
            Vector2 rawDelta = wheel ? ev.delta : Vector2.zero;
            try
            {
                if (wheel) ev.delta = rawDelta * _s;
                GUILayout.EndScrollView();
            }
            finally
            {
                if (wheel) ev.delta = rawDelta;
            }
        }

        // ---- outfits -------------------------------------------------------------------------------------

        /// <summary>
        /// The column of saved outfits, in the free space to the right of the preview. Takes up nothing on the main panel
        /// and scrolls past what fits, so there is no limit on how many. With no room for its buttons, as in a very narrow
        /// window at a large UIScale, it is left out and the rest of the screen works as before.
        /// </summary>
        private static void DrawOutfits(float left, float y, float h)
        {
            float room = Screen.width - left - Px(16f);
            float w = Mathf.Min(room, Px(300f));
            float pad = _panel.padding.left + _panel.padding.right;
            if (w < _outfitButtonW + pad)
            {
                if (!_columnHiddenLogged)
                {
                    _columnHiddenLogged = true;
                    Plugin.Log.LogInfo($"[Character] No room for the outfits column at {Screen.width}x{Screen.height}, scale {_s:F2}.");
                }
                // A window resized while a name is being typed: there is no field left to type in or close.
                if (_renaming && _outfitAction == null) _outfitAction = () => FinishRename(true);
                return;
            }

            if (_confirm != OutfitConfirm.None && Time.unscaledTime > _confirmUntil && Event.current.type == EventType.Layout)
                _confirm = OutfitConfirm.None;

            var outfits = OutfitStore.Outfits;
            GUILayout.BeginArea(new Rect(left, y, w, h), _panel);
            GUILayout.Label("Outfits", _title);
            GUILayout.Space(Px(8f));

            // No sideways scrollbar: rows clip to the column.
            var skin = _skin != null ? _skin : GUI.skin;
            _outfitScroll = GUILayout.BeginScrollView(_outfitScroll, GUIStyle.none, skin.verticalScrollbar, GUILayout.ExpandHeight(true));
            if (GUILayout.Button(CurrentLookText, _picked < 0 ? _rowOn : _row, GUILayout.ExpandWidth(true)))
                _outfitAction = () => PutOnCurrentLook();
            string worn = _working.Serialize();
            for (int i = 0; i < outfits.Count; i++)
            {
                int index = i;
                // A star on the picked one once it has been edited: Update would change it. In front, where a long name
                // cannot clip it off.
                string name = i == _picked && worn != _pickedBaseline ? "* " + outfits[i].Name : outfits[i].Name;
                if (GUILayout.Button(name, i == _picked ? _rowOn : _row, GUILayout.ExpandWidth(true)))
                    _outfitAction = () => PutOnOutfit(index);
            }
            EndScrollScaled();

            GUILayout.Space(Px(8f));
            if (_outfitTrouble) GUILayout.Label("Could not read or save the outfits file. The log has the reason.", _small);
            if (_renaming) DrawNameEditor();
            else DrawOutfitButtons(w - pad, worn);
            GUILayout.EndArea();
        }

        /// <summary>Two buttons to a row where they fit, one above the other where they do not.</summary>
        private static void DrawOutfitButtons(float room, string worn)
        {
            bool pair = room >= 2f * _outfitButtonW + _button.margin.left;
            bool picked = _picked >= 0 && _picked < OutfitStore.Outfits.Count;
            bool wasEnabled = GUI.enabled;

            if (pair) GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save new", _button, GUILayout.ExpandWidth(true))) _outfitAction = SaveNewOutfit;

            GUI.enabled = wasEnabled && picked && worn != _pickedBaseline;
            if (GUILayout.Button(_confirm == OutfitConfirm.Update ? ConfirmText : "Update", _button, GUILayout.ExpandWidth(true)))
                _outfitAction = () => ConfirmThen(OutfitConfirm.Update, UpdatePickedOutfit);
            GUI.enabled = wasEnabled;
            if (pair)
            {
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
            }

            GUI.enabled = wasEnabled && picked;
            if (GUILayout.Button("Rename", _button, GUILayout.ExpandWidth(true))) _outfitAction = () => StartRename(_picked);
            if (GUILayout.Button(_confirm == OutfitConfirm.Delete ? ConfirmText : "Delete", _button, GUILayout.ExpandWidth(true)))
                _outfitAction = () => ConfirmThen(OutfitConfirm.Delete, DeletePickedOutfit);
            GUI.enabled = wasEnabled;
            if (pair) GUILayout.EndHorizontal();
        }

        private static void DrawNameEditor()
        {
            var ev = Event.current;
            if (ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter))
            {
                ev.Use();
                _outfitAction = () => FinishRename(true);
            }
            GUI.SetNextControlName(NameFieldControl);
            _nameEdit = GUILayout.TextField(_nameEdit ?? "", OutfitStore.MaxNameLength, _field, GUILayout.ExpandWidth(true));
            if (_focusName && ev.type == EventType.Repaint)
            {
                _focusName = false;
                GUI.FocusControl(NameFieldControl);
            }
            if (GUILayout.Button("OK", _button, GUILayout.ExpandWidth(true))) _outfitAction = () => FinishRename(true);
        }

        /// <summary>Do the click the last event recorded. Layout events only, ahead of anything being placed.</summary>
        private static void RunOutfitAction()
        {
            if (_outfitAction == null || Event.current.type != EventType.Layout) return;
            var action = _outfitAction;
            _outfitAction = null;
            try { action(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Outfit action failed: " + e.Message); }
        }

        /// <summary>
        /// The first saved outfit that is the look being worn, or -1. Each is read the way Open reads a look, so one
        /// spelled differently still matches; one with a part number past the end of a list does not, and simply is
        /// not picked when the screen opens.
        /// </summary>
        private static int FindOutfitWorn()
        {
            string worn = _working.Serialize();
            var outfits = OutfitStore.Outfits;
            for (int i = 0; i < outfits.Count; i++)
            {
                var look = PlayerAppearance.Deserialize(outfits[i].Look);
                look[0] = (byte)(look[0] == 2 ? 2 : 1);
                FitColors(ref look);
                if (look.Serialize() == worn) return i;
            }
            return -1;
        }

        // Update and Delete take a second click within a few seconds, because they cannot be taken back.
        private static void ConfirmThen(OutfitConfirm kind, System.Action action)
        {
            if (_confirm == kind && Time.unscaledTime <= _confirmUntil)
            {
                _confirm = OutfitConfirm.None;
                action();
                return;
            }
            _confirm = kind;
            _confirmUntil = Time.unscaledTime + ConfirmSeconds;
        }

        private static void PutOnOutfit(int index)
        {
            var outfits = OutfitStore.Outfits;
            if (index < 0 || index >= outfits.Count) return;
            if (_renaming) FinishRename(true);
            _confirm = OutfitConfirm.None;
            // Keep what is on unless it is already saved as the outfit picked, so it can be put back on.
            string worn = _working.Serialize();
            if (!PickedIsUnedited(worn)) _currentLook = worn;
            _picked = index;
            _baselinePending = true;
            Wear(outfits[index].Look);
        }

        /// <summary>
        /// Swap what is on with the look kept in the Current look row. Clicked again it swaps back, so whichever one the
        /// player did not want is still a click away, and closing the screen keeps the one on show.
        /// </summary>
        private static void PutOnCurrentLook()
        {
            if (_renaming) FinishRename(true);
            _confirm = OutfitConfirm.None;
            string worn = _working.Serialize();
            string back = _currentLook;
            if (!PickedIsUnedited(worn)) _currentLook = worn;
            _picked = -1;
            _pickedBaseline = null;
            _baselinePending = false;
            if (!string.IsNullOrEmpty(back) && back != worn) Wear(back);
        }

        private static bool PickedIsUnedited(string worn)
        {
            return _picked >= 0 && _picked < OutfitStore.Outfits.Count && worn == _pickedBaseline;
        }

        private static void Wear(string look)
        {
            _working = PlayerAppearance.Deserialize(look);
            _working[0] = (byte)(_working[0] == 2 ? 2 : 1);
            FitColors(ref _working);
            _dirty = true;
            // The gender has to reach the body before anything is fitted to it: the male and female part lists differ
            // in length, and folding against the old gender's would change a part the outfit saved. Preview writes it,
            // and Draw folds right after this, as it does after a gender step.
            Preview();
            _normalized = false;
        }

        private static void SaveNewOutfit()
        {
            _confirm = OutfitConfirm.None;
            var added = new OutfitStore.Outfit { Name = OutfitStore.NextName(), Look = _working.Serialize() };
            OutfitStore.Outfits.Add(added);
            _outfitTrouble = !OutfitStore.Save();
            // By reference: the write can take in lines someone added to the file, which land after this one.
            _picked = OutfitStore.Outfits.IndexOf(added);
            _pickedBaseline = added.Look;
            _baselinePending = false;
            _outfitScroll.y = float.MaxValue;   // IMGUI clamps it to the end of the list
            StartRename(_picked);
        }

        private static void UpdatePickedOutfit()
        {
            var outfits = OutfitStore.Outfits;
            if (_picked < 0 || _picked >= outfits.Count) return;
            outfits[_picked].Look = _working.Serialize();
            _pickedBaseline = outfits[_picked].Look;
            _baselinePending = false;
            _outfitTrouble = !OutfitStore.Save();
        }

        private static void DeletePickedOutfit()
        {
            var outfits = OutfitStore.Outfits;
            if (_picked < 0 || _picked >= outfits.Count) return;
            outfits.RemoveAt(_picked);
            _outfitTrouble = !OutfitStore.Save();
            // What is on stays on, and the Current look row still holds the look from before this outfit was tried.
            _picked = -1;
            _pickedBaseline = null;
            _baselinePending = false;
        }

        private static void StartRename(int index)
        {
            var outfits = OutfitStore.Outfits;
            if (index < 0 || index >= outfits.Count) return;
            _confirm = OutfitConfirm.None;
            _nameEdit = outfits[index].Name;
            _renaming = true;
            _focusName = true;
        }

        /// <summary>End the name edit, keeping what was typed or not. An empty name keeps the old one.</summary>
        private static void FinishRename(bool keep)
        {
            _renaming = false;
            _focusName = false;
            GUIUtility.keyboardControl = 0;
            var outfits = OutfitStore.Outfits;
            if (!keep || _picked < 0 || _picked >= outfits.Count) return;
            string name = OutfitStore.CleanName(_nameEdit);
            if (name.Length == 0 || name == outfits[_picked].Name) return;
            outfits[_picked].Name = name;
            _outfitTrouble = !OutfitStore.Save();
        }

        /// <summary>
        /// Prefix on GameInput.GetKey, GetKeyDown and GetKeyUp (registered through GuardedPatches): no game key is down
        /// while an outfit's name is typed. The scroll axis is left alone, since it is only a key if rebound to a letter.
        /// Those readers do not check for a menu, and in co-op the world keeps running
        /// behind this screen, so typing would walk the player around, jump, and turn a wheel or winch held through the
        /// pause. Solo they would queue up instead, a jump latched for when the game resumes. The screen's own keys read
        /// Input directly. A skipped patch only lets the letters through to the game.
        /// </summary>
        internal static class TypingHoldsGameKeys
        {
            internal static bool Prefix(ref bool __result)
            {
                if (!IsTyping) return true;
                __result = false;
                return false;
            }
        }

        private static string Describe(int slot, int count)
        {
            if (PlayerAppearance.Slots[slot].Key == "gender")
                return _working[slot] == 2 ? "Female" : "Male";
            // 0 is "wear nothing" - vanilla's -1, which a byte cannot carry - and is offered ONLY on the
            // slots where it makes sense (Hat, Hair). On anatomy it deletes the body part: None on Torso
            // left a floating head, None on Head made the player disappear outright.
            int v = _working[slot];
            if (v == 0) return PlayerAppearance.Slots[slot].MatchesTorso ? MatchTorsoText : "None";
            return v + " / " + count;
        }

        // Gloves at 0: the hands the torso comes with, which is how every body was drawn before the row existed.
        private const string MatchTorsoText = "Match torso";

        /// <summary>Cycle a color slot through its palette, wrapping at both ends.</summary>
        private static void StepColor(int slot, int dir)
        {
            int n = PlayerAppearance.ColorSlots[slot].Palette.Length;
            int cur = _working.GetColor(slot);
            if (cur <= 0) cur = PlayerAppearance.NearestPaletteIndex(slot);
            int v = ((cur - 1 + dir) % n + n) % n + 1;   // 1..n, wrapping
            _working.SetColor(slot, (byte)v);
            _dirty = true;
            Preview();
        }

        /// <summary>
        /// A solid swatch for a color slot's current value. For "as cloned" it reads the template's material,
        /// so the player sees what 0 means on this machine. Null if that cannot be known yet.
        /// </summary>
        private static Texture2D Swatch(int slot, int value)
        {
            var cs = PlayerAppearance.ColorSlots[slot];
            Color c;
            if (value > 0 && value <= cs.Palette.Length) c = cs.Palette[value - 1].Color;
            else
            {
                var m = BodyTemplate.SharedMaterial;
                if (m == null || cs.Props.Length == 0 || !m.HasProperty(cs.Props[0])) return null;
                c = m.GetColor(cs.Props[0]);
            }
            string key = cs.Key + "/" + value;
            Texture2D t;
            if (_swatches.TryGetValue(key, out t) && t != null) return t;
            t = SailwindSkin.SolidTexture(new Color(c.r, c.g, c.b, 1f));
            _swatches[key] = t;
            return t;
        }

        private static void Step(int slot, int dir, int count)
        {
            _working[slot] = PlayerAppearance.NormalizeValue(slot, count, _working[slot] + dir);
            _dirty = true;
            Preview();
            // Gender decides which part lists the rig counts against, and the male and female lists can
            // differ in length. Re-fold on the next draw so no row is left reading a number above its new
            // count. Preview() has already written the new gender onto the body, so the fold sees the new
            // lists, and it folds with the same function Apply wraps with, so nothing on the body moves.
            if (PlayerAppearance.Slots[slot].Key == "gender") _normalized = false;
        }

        /// <summary>
        /// Show the edit immediately on the player's own body and on the studio mannequin. The body is
        /// restyled in place (SyntyBody.RefreshAppearance), which re-runs the customizer's model build, so the
        /// preview is driven per click rather than per frame.
        /// </summary>
        private static void Preview()
        {
            try
            {
                // VISUAL ONLY - deliberately does not touch the config. BepInEx saves a ConfigEntry to disk
                // on assignment, so persisting here would mean a file write on every single arrow click.
                // Through the body rather than ApplyLive on the customizer, so the seated first-person fade
                // comes off before the restyle and goes back on next frame instead of being overwritten.
                var body = PlayerModel.Body;
                if (body != null) body.RefreshAppearance(_working);
                // The studio mannequin is what the player is actually looking at while the screen is open;
                // the real body may well have its renderers switched off right now.
                CharacterPreviewStudio.SetAppearance(_working);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Preview failed: " + e.Message); }
        }
    }
}

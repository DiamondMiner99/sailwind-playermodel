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
        // COLORS. The Colors section edits one target at a time, chosen on its Paint row (_paintTarget): the whole body
        // (BodyPart) or one worn part, whose cloth, trim, leather and metal follow the body's until given their own.
        // A swatch click opens the picker (DrawPickerPopup) for (_pickPart, _pickColor), on the base color or, with
        // _pickPattern, on the pattern's. _pickPart is NoPick when no picker is open. The HSV values are kept here while
        // dragging, so hue does not jump when saturation or brightness passes through zero. A change that adds or
        // removes rows (the Paint target, a pattern coming or going, a pick from a list) is recorded in _layoutAction
        // and done on the next Layout event, as the outfits do.
        private const int NoPick = -2, BodyPart = -1;
        private static int _pickPart = NoPick, _pickColor = -1, _paintTarget = BodyPart;
        private static float _hue, _sat, _val;
        private static bool _pickPattern;
        private static System.Action _layoutAction;
        private const string WholeBodyText = "Everything", CustomText = "Custom", BodyText = "Body", SameText = "Same as body";
        private static readonly System.Collections.Generic.List<int> _targets = new System.Collections.Generic.List<int>();
        // Section headers; a dropdown's button (faint: a part's color that follows the body's); the Size toggles; the
        // text of a list's rows; the tooltip; the picker's sliders, which draw nothing themselves.
        private static GUIStyle _section, _drop, _dropFaint, _listText, _listTextOn, _tip, _sliderBare, _thumbBare;
        private static float _dropW = 170f, _popLabelW = 110f, _buttonH = 34f, _listRowH = 30f;
        // TURNING THE PREVIEW. A Turn button turns the model a step on a click and keeps turning it while held; dragging
        // across the preview turns it with the mouse. _turnHoldId is the button being held, by its IMGUI control id.
        private static int _turnHoldId, _turnHoldDir;
        private static float _turnHoldSince;
        private const float TapTurnDeg = 15f, SpinDelay = 0.3f, SpinRate = 120f;
        // RANDOMIZE HISTORY. The looks in the order they were on, oldest first: the look before the first roll, then
        // each roll. _rollAt is the one on now; the back and forward buttons beside Randomize step through them.
        private static readonly System.Collections.Generic.List<string> _rolls = new System.Collections.Generic.List<string>();
        private static int _rollAt = -1;
        private const int RollsKept = 30;
        // The tooltip of the icon button under the mouse, set while repainting and drawn last.
        private static string _tipText;
        // TABS. The panel shows the Parts or the Colors, each with its own scroll position.
        private const int PartsTab = 0, ColorsTab = 1;
        private static int _tab = PartsTab;
        private static Vector2 _colorsScroll;
        // FLAT CONTROLS in the parchment palette, in place of Unity's grey bevelled ones: a fill with an ink edge,
        // lighter at rest, warmer under the mouse, darker pressed, and dark with light text when chosen (a tab, an open
        // list). Made for the edge width of the current scale (FlatTextures).
        private static Texture2D _flatRest, _flatHover, _flatPress, _flatOn, _flatField, _scrollTrack, _scrollThumb;
        private static int _flatEdge;
        private static GUIStyle _dropOn, _tabOff, _tabOn, _stepBox, _stepArrow, _stepValue;
        private static float _tabW = 110f;

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
                // Pictures added to, changed in or taken out of the patterns folder since it was last read. Every body
                // wearing one is drawn again with the new picture (your own fully, so the seated first-person copy of its
                // material follows), then the old pictures are freed, and the Pattern button is measured again.
                if (PatternImages.Rescan())
                {
                    PlayerAppearance.RecolorPictureBodies();
                    PlayerModel.ApplyAppearanceLive();
                    PatternImages.ReleaseReplaced();
                    _stylesBuilt = false;
                }
                // Take a private COPY of the array. PlayerAppearance is a struct holding a byte[], so a plain
                // assignment would share the array with Plugin's stored appearance and every registry
                // entry - editing here would mutate them in place behind their backs.
                _working = PlayerAppearance.Deserialize(PlayerModel.LocalAppearance.Serialize());
                FitWorking();
                _dirty = false;
                _scroll = Vector2.zero;
                _colorsScroll = Vector2.zero;
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
                _pickPart = NoPick;
                _pickColor = -1;
                _paintTarget = BodyPart;
                _turnHoldDir = 0;
                _rolls.Clear();
                _rollAt = -1;
                CloseDrop();
                _tipText = null;
                _layoutAction = null;
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
            try { CloseDrop(); }   // puts back a pattern shown on hover before the commit
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Could not close the list: " + e.Message); }
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
                    // An open list or picker closes first, then a name being typed backs out, then the screen closes.
                    if (_dropOpen) CloseDrop();
                    else if (_renaming) FinishRename(false);
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

            _section = new GUIStyle(_label) { fontSize = (int)Px(17f), fontStyle = FontStyle.Bold, padding = Offset(4, 4, 8, 2) };
            // Darker than the faint ink, which was too light to read on the parchment: between the title band and the ink.
            _section.normal.textColor = Color.Lerp(SailwindSkin.ParchmentDark, SailwindSkin.InkColor, 0.4f);

            _button = new GUIStyle(stock.button)
            {
                fontSize = (int)Px(19f), padding = Offset(10, 10, 5, 5), margin = Scaled(stock.button.margin, s),
            }.WithFont();
            FlatTextures(s);
            Flat(_button);
            _buttonH = Mathf.Ceil(_button.CalcSize(new GUIContent("Ag")).y);

            // A dropdown's button: text on the left, room on the right for the chevron and the dot (DropDecor).
            _drop = new GUIStyle(_button) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, padding = Offset(10, 30, 5, 5) };
            _dropFaint = new GUIStyle(_drop);
            _dropFaint.normal.textColor = SailwindSkin.InkFaint;
            // An open list's button: pressed, so it does not read as greyed out.
            _dropOn = new GUIStyle(_drop);
            _dropOn.normal.background = _flatPress;
            _dropOn.hover.background = _flatPress;

            _tabOff = new GUIStyle(_button) { padding = Offset(16, 16, 6, 6) };
            _tabOn = new GUIStyle(_tabOff);
            SetAll(_tabOn, _flatOn, SailwindSkin.Parchment);

            // A stepper: one framed box holding both arrows and the value between them.
            _stepBox = new GUIStyle { border = new RectOffset(_flatEdge, _flatEdge, _flatEdge, _flatEdge), padding = new RectOffset(_flatEdge, _flatEdge, _flatEdge, _flatEdge), margin = _button.margin };
            _stepBox.normal.background = _flatRest;
            _stepArrow = new GUIStyle(_button) { margin = new RectOffset(0, 0, 0, 0), padding = Offset(0, 0, 4, 4), alignment = TextAnchor.MiddleCenter };
            _stepArrow.normal.background = null;
            _stepValue = new GUIStyle(_label) { margin = new RectOffset(0, 0, 0, 0), padding = Offset(4, 4, 4, 4), alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
            _tip = new GUIStyle(_label) { fontSize = (int)Px(16f), padding = Offset(8, 8, 4, 4), alignment = TextAnchor.MiddleLeft };
            _tip.normal.textColor = SailwindSkin.Parchment;
            _sliderBare = new GUIStyle();
            _thumbBare = new GUIStyle { fixedWidth = Px(12f), fixedHeight = Px(22f) };

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

            _tip.normal.background = _titleTex;
            // A list's rows: the outfit rows' text, with the shading drawn by DrawList.
            _listText = new GUIStyle(_row);
            _listText.normal.background = null;
            _listText.hover.background = null;
            _listText.active.background = null;
            _listTextOn = new GUIStyle(_listText);
            _listTextOn.normal.textColor = SailwindSkin.Parchment;
            _listRowH = Mathf.Ceil(_listText.CalcSize(new GUIContent("Ag")).y);

            _field = new GUIStyle(stock.textField)
            {
                fontSize = (int)Px(19f), padding = Offset(6, 6, 5, 5), margin = Scaled(stock.textField.margin, s), clipping = TextClipping.Clip,
            }.WithFont();
            SetAll(_field, _flatField, SailwindSkin.InkColor);
            _field.border = new RectOffset(_flatEdge, _flatEdge, _flatEdge, _flatEdge);

            _outfitButtonW = Px(100f);
            _labelW = Px(130f);
            _valueW = Px(90f);
            _arrowW = Px(34f);
            _randomW = Px(120f);
            _doneW = Px(140f);
            _dropW = Px(170f);
            _popLabelW = Px(110f);
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
                    _labelW = Mathf.Max(_labelW, TextWidth(_label, colorSlot.Label) + slack);
                _labelW = Mathf.Max(_labelW, TextWidth(_label, "Paint") + slack);
                _tabW = Mathf.Max(Px(110f), Mathf.Max(TextWidth(_tabOff, "Parts"), TextWidth(_tabOff, "Colors")) + slack);
                // Describe's texts.
                foreach (var text in new[] { "Female", "Male", "None", MatchTorsoText, "888 / 888" })
                    _valueW = Mathf.Max(_valueW, TextWidth(_label, text) + slack);
                // A dropdown is as wide as the longest thing it can show: the color names, the parts, the patterns.
                var dropTexts = new System.Collections.Generic.List<string> { CustomText, BodyText, SameText, WholeBodyText, "Default" };
                foreach (var colorSlot in PlayerAppearance.ColorSlots)
                    foreach (var option in colorSlot.Palette) dropTexts.Add(option.Name);
                foreach (var slot in PlayerAppearance.Slots) dropTexts.Add(slot.Label);
                dropTexts.AddRange(PlayerAppearance.PatternNames);
                dropTexts.AddRange(PatternImages.Names);
                foreach (var text in dropTexts)
                    _dropW = Mathf.Max(_dropW, TextWidth(_drop, text) + slack);
                foreach (var text in new[] { "Hue", "Saturation", "Brightness", "Pattern", "Scale" })
                    _popLabelW = Mathf.Max(_popLabelW, TextWidth(_label, text) + slack);
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
                // A plain track and a framed thumb, not Unity's grey ones.
                SetAll(_skin.verticalScrollbar, _scrollTrack, SailwindSkin.InkColor);
                _skin.verticalScrollbar.border = new RectOffset(0, 0, 0, 0);
                SetAll(_skin.verticalScrollbarThumb, _scrollThumb, SailwindSkin.InkColor);
                _skin.verticalScrollbarThumb.border = new RectOffset(_flatEdge, _flatEdge, _flatEdge, _flatEdge);
                _skin.verticalScrollbarUpButton = Scaled(stock.verticalScrollbarUpButton, s);
                _skin.verticalScrollbarDownButton = Scaled(stock.verticalScrollbarDownButton, s);
                _skin.horizontalScrollbar = Scaled(stock.horizontalScrollbar, s);
                _skin.horizontalScrollbarThumb = Scaled(stock.horizontalScrollbarThumb, s);
                _skin.horizontalScrollbarLeftButton = Scaled(stock.horizontalScrollbarLeftButton, s);
                _skin.horizontalScrollbarRightButton = Scaled(stock.horizontalScrollbarRightButton, s);
                // The color picker's sliders.
                _skin.horizontalSlider = Scaled(stock.horizontalSlider, s);
                _skin.horizontalSliderThumb = Scaled(stock.horizontalSliderThumb, s);
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
            float slotRow = _labelW + labelToArrow + StepperW();
            // A color row: label, the color's swatch and the pattern's, each with the space after it, then the dropdown.
            float colorRow = _labelW + lm.right + 2f * (SwatchW() + Px(4f)) + bm.left + _dropW;
            // The Paint row: label and dropdown.
            float paintRow = _labelW + labelToArrow + _dropW;
            float rows = Mathf.Max(slotRow, Mathf.Max(colorRow, paintRow)) + Mathf.Max(view.padding.left, lm.left) + Mathf.Max(view.padding.right, bm.right);
            float scroll = rows + bar.fixedWidth + bar.margin.left +
                Mathf.Max(view.margin.left, pad.left) + Mathf.Max(view.margin.right, pad.right);
            // Randomize, back, forward, Done.
            float between = Mathf.Max(bm.right, bm.left);
            float bottom = _randomW + 2f * _buttonH + 3f * between + _doneW + Mathf.Max(bm.left, pad.left) + Mathf.Max(bm.right, pad.right);
            return Mathf.Max(scroll, bottom);
        }

        /// <summary>Draw. Called from MenuDriver.OnGUI.</summary>
        public static void Draw()
        {
            if (!_open) return;
            _screenMouse = Event.current.mousePosition;   // top level: no area or scroll view yet (see ToScreen)
            BeginOverlays();
            try { DrawScreen(); }
            finally { EndOverlays(); }
        }

        private static void DrawScreen()
        {
            if (!_stylesBuilt || Event.current.type == EventType.Layout) UpdateScale();
            // Scrollbars at this scale. IMGUI puts the stock skin back before the next OnGUI call, whoever makes it.
            if (_skin != null) GUI.skin = _skin;
            RunOutfitAction();
            RunLayoutAction();
            if (Event.current.type == EventType.Repaint) _tipText = null;

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
            float y = (Screen.height - h) * 0.5f;

            GUI.depth = 0;

            // The panel, the preview and the outfits column stand side by side as one group, centered on the screen
            // with the same gap between each. Measured first, then placed. Where the screen is too narrow for all of
            // it, the group starts at the margin and the outfits column takes what room is left (DrawOutfits).
            float gap = Px(28f), margin = Px(16f), inset = Px(8f), turnGap = Px(12f), turnH = Px(30f);
            var preview = CharacterPreviewStudio.Texture;
            float pw = 0f, ph = 0f;
            if (preview != null)
            {
                // As tall as makes the box and the Turn row under it span the panel's height, so the three columns
                // line up; no wider than about a quarter of the screen, and then centered on the panel instead.
                float perW = (float)preview.height / preview.width;
                float fitPh = h - 2f * inset - turnGap - turnH;
                pw = Mathf.Max(Px(120f), Mathf.Min(fitPh / perW, Mathf.Min(Screen.width * 0.26f, Px(440f))));
                ph = pw * perW;
                // Ask for a texture about as tall as it is drawn. From the fixed shape, not this texture's own,
                // which is off by a rounding: a swap must not move the request into the next step and back.
                CharacterPreviewStudio.RequestHeight(pw * CharacterPreviewStudio.HeightPerWidth);
            }
            float previewBox = preview != null ? pw + 2f * inset : 0f;
            float outfitsW = Px(300f);
            float total = w + (preview != null ? gap + previewBox : 0f) + gap + outfitsW;
            float x = Mathf.Max(margin, (Screen.width - total) * 0.5f);
            float columnX = x + w + gap + (preview != null ? previewBox + gap : 0f);

            // The character, to the RIGHT of the controls. This is the studio mannequin, not the player's
            // real body - see CharacterPreviewStudio for why the real one cannot be used (its renderers are
            // disabled while this screen is open, and in co-op the world is not paused).
            if (preview != null)
            {
                float px = x + w + gap + inset;
                float groupH = ph + 2f * inset + turnGap + turnH;
                float py = y + Mathf.Max(0f, h - groupH) * 0.5f + inset;

                GUI.Box(new Rect(px - inset, py - inset, pw + 2f * inset, ph + 2f * inset), GUIContent.none, _panel);
                GUI.DrawTexture(new Rect(px, py, pw, ph), preview, ScaleMode.ScaleToFit, false);
                PreviewDrag(new Rect(px, py, pw, ph), pw);

                // Turn controls, so a hat can be judged from behind as well as in front. Held, they keep turning.
                float by = py + ph + inset + turnGap, bw = Px(64f), bgap = Px(12f);
                float bx = px + (pw - 2f * bw - bgap) * 0.5f;
                TurnButton(new Rect(bx, by, bw, turnH), UiIcons.Kind.TurnLeft, "Turn left", -1);
                TurnButton(new Rect(bx + bw + bgap, by, bw, turnH), UiIcons.Kind.TurnRight, "Turn right", +1);
                if (Event.current.type == EventType.Repaint) UpdateSpin();
            }
            else CharacterPreviewStudio.SpinDegPerSec = 0f;

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
                // Tabs: the parts, or the colors. A switch changes every row, so it waits for the next Layout event.
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Parts", _tab == PartsTab ? _tabOn : _tabOff, GUILayout.Width(_tabW)) && _tab != PartsTab)
                    _layoutAction = () => SetTab(PartsTab);
                if (GUILayout.Button("Colors", _tab == ColorsTab ? _tabOn : _tabOff, GUILayout.Width(_tabW)) && _tab != ColorsTab)
                    _layoutAction = () => SetTab(ColorsTab);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                Rule();

                if (_tab == PartsTab)
                {
                    _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
                    foreach (int i in Order())
                    {
                        if (PlayerAppearance.Slots[i].Key == FirstGearRow) Section("Gear", 16f);   // a gap: a section of its own
                        int count = PlayerAppearance.VariantCount(customizer, i);
                        if (count <= 1) continue;   // nothing to choose between on this rig; do not show a dead row
                        // Vanilla has no female facial hair, so the row would be a control that visibly does
                        // nothing. Hide it rather than offer a lie.
                        if (PlayerAppearance.Slots[i].Key == "facialhair" && _working[0] == 2) continue;

                        GUILayout.BeginHorizontal();
                        GUILayout.Label(PlayerAppearance.Slots[i].Label, _label, GUILayout.Width(_labelW));
                        int dir = Stepper(Describe(i, count));
                        if (dir != 0) Step(i, dir, count);
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                        if (PlayerAppearance.Slots[i].Key == "facialhair") DrawStubbleRow();
                    }
                }
                else
                {
                    _colorsScroll = GUILayout.BeginScrollView(_colorsScroll, GUILayout.ExpandHeight(true));
                    EnsurePick();
                    DrawPaintTarget();
                    GUILayout.Space(Px(4f));
                    for (int i = 0; i < PlayerAppearance.ColorSlotCount; i++)
                    {
                        // A part has its own cloth, trim, leather and metal; skin and hair belong to the whole body.
                        if (_paintTarget != BodyPart && !PlayerAppearance.ColorSlots[i].PerPart) continue;
                        DrawColorRow(_paintTarget, i);
                    }
                    GUILayout.Space(Px(8f));
                    Rule();
                    EnsurePick();
                    DrawPickerDocked();
                }

                EndScrollScaled();
            }

            GUILayout.Space(Px(8f));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Randomize", _button, GUILayout.Width(_randomW)) && customizer != null) Randomize(customizer);
            // Back and forward through the rolls, as Deep Rock Galactic's character screen does.
            if (IconButton(UiIcons.Kind.Back, "Previous look", _rollAt > 0)) _layoutAction = () => StepRoll(-1);
            if (IconButton(UiIcons.Kind.Forward, "Next look", _rollAt >= 0 && _rollAt < _rolls.Count - 1)) _layoutAction = () => StepRoll(+1);
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
            if (_open && customizer != null) DrawOutfits(columnX, y, h, outfitsW);

            // On top of everything, in this order: the picker, a list (which can open from the picker), the tooltip.
            if (!_open) return;
            DrawList();
            DrawTooltip();
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
        private static void DrawOutfits(float left, float y, float h, float width)
        {
            float room = Screen.width - left - Px(16f);
            float w = Mathf.Min(room, width);
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
            ClosePicker();   // its sliders hold the old look's color; runs on a Layout event (RunOutfitAction)
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

        // ---- flat controls, tabs, steppers -------------------------------------------------------------------

        /// <summary>
        /// The flat control textures, for an ink edge as wide as the scale (1 px at 1080p): a square of fill with the
        /// edge round it, nine-sliced by the styles (border = the edge), so any size of button keeps a thin edge.
        /// </summary>
        private static void FlatTextures(float s)
        {
            int edge = Mathf.Max(1, Mathf.RoundToInt(s));
            if (edge == _flatEdge && _flatRest != null) return;
            _flatEdge = edge;
            foreach (var old in new[] { _flatRest, _flatHover, _flatPress, _flatOn, _flatField, _scrollThumb })
                if (old != null) Object.Destroy(old);
            Color ink = SailwindSkin.ParchmentDark;
            _flatRest = FrameTexture(Color.Lerp(SailwindSkin.Parchment, SailwindSkin.ButtonPlate, 0.45f), ink, edge);
            _flatHover = FrameTexture(SailwindSkin.ButtonPlate, ink, edge);
            _flatPress = FrameTexture(Color.Lerp(SailwindSkin.Parchment, SailwindSkin.ParchmentDark, 0.28f), ink, edge);
            _flatOn = FrameTexture(SailwindSkin.ParchmentDark, ink, edge);
            _flatField = FrameTexture(Color.Lerp(SailwindSkin.Parchment, Color.white, 0.3f), ink, edge);
            _scrollThumb = FrameTexture(Color.Lerp(SailwindSkin.Parchment, SailwindSkin.ParchmentDark, 0.45f), ink, edge);
            if (_scrollTrack == null) _scrollTrack = SailwindSkin.SolidTexture(Color.Lerp(SailwindSkin.Parchment, SailwindSkin.ParchmentDark, 0.15f));
        }

        private static Texture2D FrameTexture(Color fill, Color edgeColor, int edge)
        {
            int size = edge * 2 + 2;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool rim = x < edge || y < edge || x >= size - edge || y >= size - edge;
                    px[y * size + x] = rim ? edgeColor : fill;
                }
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
            };
            t.SetPixels(px);
            t.Apply(false, true);
            return t;
        }

        /// <summary>A button in the flat parchment look, every state of it, ink text throughout.</summary>
        private static void Flat(GUIStyle st)
        {
            st.border = new RectOffset(_flatEdge, _flatEdge, _flatEdge, _flatEdge);
            st.overflow = new RectOffset(0, 0, 0, 0);
            st.normal.background = _flatRest;
            st.hover.background = _flatHover;
            st.active.background = _flatPress;
            st.focused.background = _flatRest;
            st.onNormal.background = _flatOn;
            st.onHover.background = _flatOn;
            st.onActive.background = _flatOn;
            st.onFocused.background = _flatOn;
            st.normal.textColor = st.hover.textColor = st.active.textColor = st.focused.textColor = SailwindSkin.InkColor;
            st.onNormal.textColor = st.onHover.textColor = st.onActive.textColor = st.onFocused.textColor = SailwindSkin.Parchment;
        }

        /// <summary>Every state of a style with one background and text color.</summary>
        private static void SetAll(GUIStyle st, Texture2D background, Color text)
        {
            foreach (var state in new[] { st.normal, st.hover, st.active, st.focused, st.onNormal, st.onHover, st.onActive, st.onFocused })
            {
                state.background = background;
                state.textColor = text;
            }
        }

        private static void SetTab(int tab)
        {
            _tab = tab;
            ClosePicker();
            CloseDrop();
        }

        /// <summary>A faint rule across the panel, then <paramref name="after"/> UI pixels of space.</summary>
        private static void Rule(float after = 6f)
        {
            var rule = GUILayoutUtility.GetRect(1f, Mathf.Max(1f, Px(1f)), GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                var old = GUI.color;
                GUI.color = SailwindSkin.InkFaint;
                GUI.DrawTexture(rule, Texture2D.whiteTexture);
                GUI.color = old;
            }
            GUILayout.Space(Px(after));
        }

        private static float StepperW() { return 2f * _arrowW + _valueW + 2f * _flatEdge; }

        /// <summary>A part row's stepper: both arrows and the value in one framed box. -1 or +1 on a click, else 0.</summary>
        private static int Stepper(string value)
        {
            int dir = 0;
            GUILayout.BeginHorizontal(_stepBox, GUILayout.Width(StepperW()));
            if (GUILayout.Button("<", _stepArrow, GUILayout.Width(_arrowW))) dir = -1;
            GUILayout.Label(value, _stepValue, GUILayout.Width(_valueW));
            if (GUILayout.Button(">", _stepArrow, GUILayout.Width(_arrowW))) dir = +1;
            GUILayout.EndHorizontal();
            return dir;
        }

        // ---- colors -----------------------------------------------------------------------------------------

        private static float SwatchW() { return Px(20f); }

        private static int IconPx() { return Mathf.Max(8, Mathf.RoundToInt(Px(22f))); }

        /// <summary>
        /// A swatch that opens or closes the picker for (part, color slot, base or pattern color). Drawn with a tint over
        /// the white texture, so a color being dragged makes no texture per frame. A frame marks it as something to
        /// click, heavier under the mouse and while its picker is open. A pattern swatch shows the base color striped
        /// with the pattern's. One taken from the body is washed toward the parchment. Not visible still takes its room,
        /// so the rows line up whether or not they have a pattern. Its size is counted in RequiredPanelWidth.
        /// </summary>
        private static void SwatchButton(Color? fill, Color? stripes, bool picking, int part, int colorSlot, bool pattern,
            bool inherited, bool visible)
        {
            float size = SwatchW();
            var r = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            GUILayout.Space(Px(4f));
            if (!visible) return;
            var ev = Event.current;
            if (ev.type == EventType.Repaint)
            {
                var box = new Rect(r.x, r.y + Px(2f), size, Px(16f));
                var old = GUI.color;
                bool strong = picking || r.Contains(ev.mousePosition);
                float edge = strong ? Px(2f) : Mathf.Max(1f, Px(1f));
                GUI.color = strong ? SailwindSkin.InkColor : SailwindSkin.InkFaint;
                GUI.DrawTexture(new Rect(box.x - edge, box.y - edge, box.width + 2f * edge, box.height + 2f * edge), Texture2D.whiteTexture);
                GUI.color = Wash(fill.HasValue ? Opaque(fill.Value) : SailwindSkin.ParchmentDark, inherited);
                GUI.DrawTexture(box, Texture2D.whiteTexture);
                if (stripes.HasValue)
                {
                    GUI.color = Wash(Opaque(stripes.Value), inherited);
                    GUI.DrawTexture(box, UiIcons.Get(UiIcons.Kind.Stripes, Mathf.RoundToInt(size)));
                }
                GUI.color = old;
            }
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) TogglePicker(part, colorSlot, pattern);
        }

        private static Color Opaque(Color c) { return new Color(c.r, c.g, c.b, 1f); }

        /// <summary>A color the part takes from the body, washed toward the parchment.</summary>
        private static Color Wash(Color c, bool inherited)
        {
            return inherited ? Color.Lerp(c, SailwindSkin.Parchment, 0.45f) : c;
        }

        /// <summary>
        /// A rect drawn inside the panel (its area and scroll view), in the screen's own GUI coordinates, where the list
        /// and the picker are drawn. Taken from the mouse: _screenMouse is where it was at the top level of this event,
        /// Event.current.mousePosition where it is in the panel's coordinates now, and the difference is the panel's
        /// offset, scroll included. GUIUtility.GUIToScreenPoint does not give this in a built game: it put the lists in
        /// the top left corner and off the screen.
        /// </summary>
        private static Rect ToScreen(Rect r)
        {
            var offset = _screenMouse - Event.current.mousePosition;
            return new Rect(r.x + offset.x, r.y + offset.y, r.width, r.height);
        }

        private static Vector2 _screenMouse;

        /// <summary>A section header with a faint rule under it.</summary>
        private static void Section(string text, float above = 4f)
        {
            GUILayout.Space(Px(above));
            GUILayout.Label(text, _section);
            Rule(4f);
        }

        /// <summary>The Paint row: what the color rows under it edit, the whole body or one part, from a dropdown.</summary>
        private static void DrawPaintTarget()
        {
            var targets = PaintTargets();
            // A part taken off, with nothing of its own left to reset, drops out of the list. Put right on a Layout
            // event, ahead of the rows that depend on it, so every event of the frame lays out the same rows.
            if (Event.current.type == EventType.Layout && !targets.Contains(_paintTarget))
            {
                _paintTarget = BodyPart;
                if (_pickPart >= 0) ClosePicker();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Paint", _label, GUILayout.Width(_labelW));
            Dropdown(PaintKey, TargetName(_paintTarget), false,
                PaintItems, targets.IndexOf(_paintTarget), i =>
                {
                    var now = PaintTargets();
                    if (i < 0 || i >= now.Count) return;
                    _paintTarget = now[i];
                    ClosePicker();
                });
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// The whole body, then the paintable parts in the order of their rows: those worn, and any taken off that
        /// still have colors of their own, so those can be put back. Gloves are always offered: with none picked they
        /// are the hands the torso comes with, and painting them paints those.
        /// </summary>
        private static System.Collections.Generic.List<int> PaintTargets()
        {
            _targets.Clear();
            _targets.Add(BodyPart);
            foreach (int i in Order())
            {
                if (!PlayerAppearance.IsPaintable(i)) continue;
                bool worn = _working[i] != 0 || PlayerAppearance.Slots[i].MatchesTorso;
                if (worn || HasOwnPaint(i)) _targets.Add(i);
            }
            return _targets;
        }

        private static System.Collections.Generic.List<DropItem> PaintItems()
        {
            var items = new System.Collections.Generic.List<DropItem>();
            foreach (int t in PaintTargets())
                items.Add(new DropItem { Text = TargetName(t) });
            return items;
        }

        private static bool HasOwnPaint(int part)
        {
            return _working.HasPartColors(part) || _working.HasPartPatterns(part);
        }

        private static string TargetName(int target)
        {
            return target < 0 || target >= PlayerAppearance.SlotCount ? WholeBodyText : PlayerAppearance.Slots[target].Label;
        }

        /// <summary>
        /// One color row: the color's swatch, the pattern's when there is one, and a dropdown of named colors. On a
        /// part, the dropdown starts with Body (take the body's color), and a color or pattern the part takes from the
        /// body is washed out, its name faint. Either swatch opens the picker on its color.
        /// </summary>
        private static void DrawColorRow(int part, int k)
        {
            var cs = PlayerAppearance.ColorSlots[k];
            bool onPart = part != BodyPart;

            Color body;
            bool known = _working.TryGetBodyColor(k, out body);
            Color32 own = default(Color32);
            bool isOwn = onPart && _working.TryGetPartRgb(part, k, out own);
            Color? shown = isOwn ? (Color)own : known ? body : (Color?)null;

            PlayerAppearance.Pattern pat = default(PlayerAppearance.Pattern), ownPat;
            bool patInherited = false;
            if (!onPart) _working.TryGetBodyPattern(k, out pat);
            else
            {
                patInherited = !_working.TryGetPartPattern(part, k, out ownPat);
                pat = _working.EffectivePattern(part, k);
            }
            bool hasPat = PlayerAppearance.TakesPattern(k) && pat.Type > 0;

            GUILayout.BeginHorizontal();
            GUILayout.Label(cs.Label, _label, GUILayout.Width(_labelW));
            SwatchButton(shown, null, IsPicking(part, k, false), part, k, false, onPart && !isOwn, true);
            SwatchButton(shown, hasPat ? (Color?)(Color)pat.Color : null, IsPicking(part, k, true), part, k, true,
                (onPart && !isOwn) || patInherited, hasPat);

            string text;
            int selected;
            bool faint = false;
            if (!onPart)
            {
                Color32 custom;
                int v = _working.GetColor(k);
                bool isCustom = _working.TryGetRgb(k, out custom);
                text = isCustom ? CustomText : PlayerAppearance.DescribeColor(k, v);
                selected = isCustom ? -1 : v - 1;
            }
            else if (!isOwn)
            {
                text = BodyText;
                selected = 0;
                faint = true;
            }
            else
            {
                int match = PaletteMatch(k, own);
                text = match >= 0 ? cs.Palette[match].Name : CustomText;
                selected = match >= 0 ? match + 1 : -1;
            }
            int slot = k, target = part;
            Dropdown(ColorKey(part, k), text, faint, () => ColorItems(slot, target != BodyPart), selected,
                i => PickNamedColor(target, slot, i));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static System.Collections.Generic.List<DropItem> ColorItems(int k, bool onPart)
        {
            var items = new System.Collections.Generic.List<DropItem>();
            if (onPart)
            {
                Color body;
                bool known = _working.TryGetBodyColor(k, out body);
                items.Add(new DropItem { Text = BodyText, Swatch = known ? (Color?)Wash(Opaque(body), true) : null });
            }
            foreach (var option in PlayerAppearance.ColorSlots[k].Palette)
                items.Add(new DropItem { Text = option.Name, Swatch = option.Color });
            return items;
        }

        /// <summary>A named color from a row's dropdown. On a part, entry 0 is Body: the part goes back to the body's color.</summary>
        private static void PickNamedColor(int part, int k, int index)
        {
            var palette = PlayerAppearance.ColorSlots[k].Palette;
            if (part == BodyPart)
            {
                if (index < 0 || index >= palette.Length) return;
                _working.SetRgb(k, null);
                _working.SetColor(k, (byte)(index + 1));
            }
            else if (index == 0) _working.SetPartRgb(part, k, null);
            else if (index - 1 < palette.Length) _working.SetPartRgb(part, k, (Color32)palette[index - 1].Color);
            else return;
            _dirty = true;
            if (_pickPart == part && _pickColor == k && !_pickPattern) LoadPicker();   // the sliders follow
            PreviewColors();
        }

        private static int PaletteMatch(int k, Color32 c)
        {
            var palette = PlayerAppearance.ColorSlots[k].Palette;
            for (int i = 0; i < palette.Length; i++)
            {
                Color32 q = palette[i].Color;
                if (q.r == c.r && q.g == c.g && q.b == c.b) return i;
            }
            return -1;
        }

        // ---- dropdowns --------------------------------------------------------------------------------------

        private struct DropItem
        {
            public string Text;
            public Color? Swatch;
            /// <summary>A pattern list row: a swatch of the pattern (type Pattern, 0 none) in the colors it would wear.</summary>
            public bool PatternSwatch;
            public int Pattern;
            public Color PatBase, PatColor;
            public byte PatSize;
            /// <summary>A picture's row (Pattern is PlayerAppearance.ImageType): the picture, and its width over height.</summary>
            public Texture Image;
            public float ImageAspect;
        }

        private const int PaintKey = 1, PatternKey = 2;
        private static int ColorKey(int part, int k) { return 1000 + (part + 2) * 32 + k; }

        /// <summary>
        /// A dropdown in a GUILayout row: a button showing the choice, with a chevron and, when dot is set, the mark of a
        /// part with colors of its own. A click opens the list over the screen (DrawList); the items are built only
        /// then. A pick runs on the next Layout event, since it can add or remove rows.
        /// </summary>
        private static void Dropdown(int key, string text, bool faint,
            System.Func<System.Collections.Generic.List<DropItem>> items, int selected, System.Action<int> pick)
        {
            var style = _dropOpen && _dropKey == key ? _dropOn : faint ? _dropFaint : _drop;
            bool clicked = GUILayout.Button(text, style, GUILayout.Width(_dropW));
            var r = GUILayoutUtility.GetLastRect();
            DropDecor(r, style);
            if (clicked) ToggleList(key, ToScreen(r), items, selected, pick);
        }

        /// <summary>The same, at a fixed place: for the picker, which is drawn outside the layout.</summary>
        private static void Dropdown(Rect r, int key, string text, System.Func<System.Collections.Generic.List<DropItem>> items,
            int selected, System.Action<int> pick)
        {
            var style = _dropOpen && _dropKey == key ? _dropOn : _drop;
            bool clicked = GUI.Button(r, text, style);
            DropDecor(r, style);
            if (clicked) ToggleList(key, ToScreen(r), items, selected, pick);
        }

        private static void DropDecor(Rect r, GUIStyle style)
        {
            if (Event.current.type != EventType.Repaint) return;
            bool hover = r.Contains(Event.current.mousePosition);
            Color ink = hover ? style.hover.textColor : style.normal.textColor;
            float icon = IconPx();
            UiIcons.Draw(new Rect(r.xMax - Px(26f), r.y, Px(20f), r.height), UiIcons.Kind.Chevron, Mathf.RoundToInt(icon * 0.8f), ink);
        }

        private static void ToggleList(int key, Rect anchor, System.Func<System.Collections.Generic.List<DropItem>> items,
            int selected, System.Action<int> pick)
        {
            if (_dropOpen && _dropKey == key) { CloseDrop(); return; }
            _dropItems.Clear();
            _dropItems.AddRange(items());
            if (_dropItems.Count == 0) return;
            _dropOpen = true;
            _hoverShown = -1;
            _dropKey = key;
            _dropAnchor = anchor;
            _dropSelected = selected;
            _dropPick = pick;
            _dropPlaced = false;
            // As wide as the button, or its longest entry.
            float w = anchor.width;
            foreach (var item in _dropItems)
                w = Mathf.Max(w, TextWidth(_listText, item.Text) + (item.Swatch.HasValue ? Px(28f) : item.PatternSwatch ? PatternSwatchW() + Px(14f) : 0f) + Px(12f));
            _dropWidth = w;
            _dropFirst = Mathf.Max(0, selected - 3);
        }

        private static void CloseDrop()
        {
            // A pattern shown on hover goes back when the list closes without a pick.
            if (_hoverShown >= 0)
            {
                _hoverShown = -1;
                PreviewColors();
            }
            _dropOpen = false;
            _dropPick = null;
            _dropPlaced = false;
        }

        // ---- overlays: the open list and the picker -----------------------------------------------------------
        // Both float over the screen and are drawn last, so they are on top. IMGUI gives the mouse to controls in the
        // order they are drawn, so drawn last they would come last for it too: a click on the list would reach the row
        // under it first. So BeginOverlays, at the start of every event, hides a mouse event that lands on an overlay
        // from everything drawn before it (its type is set to Ignore), and the overlay takes it back when it draws.
        // A press outside the list closes the list and goes no further. A press outside the picker closes the picker
        // and still reaches what it landed on, so a click on another swatch moves the picker there. A turn of the wheel
        // outside either closes it, since what it belongs to is about to scroll away.

        private enum Overlay { None, List }
        private static Overlay _hiddenFor;
        private static EventType _hiddenType;

        private static bool _dropOpen, _dropPlaced;
        private static int _dropKey, _dropSelected, _dropFirst;
        // The Pattern list row being shown on the character while the mouse is on it, -1 for none.
        private static int _hoverShown = -1;
        private static float _dropWidth;
        private static Rect _dropAnchor, _dropRect;
        private static readonly System.Collections.Generic.List<DropItem> _dropItems = new System.Collections.Generic.List<DropItem>();
        private static System.Action<int> _dropPick;


        private static void BeginOverlays()
        {
            _hiddenFor = Overlay.None;
            var ev = Event.current;
            var t = ev.type;
            bool press = t == EventType.MouseDown, wheel = t == EventType.ScrollWheel;
            // A drag or a release belongs to the control its press started, wherever the mouse is now.
            bool loose = (t == EventType.MouseDrag || t == EventType.MouseUp) && GUIUtility.hotControl == 0;
            if (!press && !wheel && !loose) return;
            Vector2 m = ev.mousePosition;
            if (_dropOpen && _dropPlaced)
            {
                if (_dropRect.Contains(m)) { Hide(Overlay.List); return; }
                if (press) { CloseDrop(); ev.Use(); return; }
                if (wheel) CloseDrop();
            }
        }

        private static void Hide(Overlay o)
        {
            _hiddenFor = o;
            _hiddenType = Event.current.type;
            Event.current.type = EventType.Ignore;
        }

        private static void Reveal(Overlay o)
        {
            if (_hiddenFor != o) return;
            Event.current.type = _hiddenType;
            _hiddenFor = Overlay.None;
        }

        /// <summary>An event hidden for an overlay that was not drawn (it closed meanwhile) goes back as it was.</summary>
        private static void EndOverlays()
        {
            if (_hiddenFor != Overlay.None) Reveal(_hiddenFor);
        }

        /// <summary>A parchment panel with a faint ink edge and a soft shadow, for the list and the picker.</summary>
        private static void DrawFloatingPanel(Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            float edge = Mathf.Max(1f, Px(1f)), drop = Px(4f);
            GUI.color = new Color(0f, 0f, 0f, 0.25f);
            GUI.DrawTexture(new Rect(r.x + drop, r.y + drop, r.width, r.height), Texture2D.whiteTexture);
            GUI.color = SailwindSkin.InkFaint;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.DrawTexture(new Rect(r.x + edge, r.y + edge, r.width - 2f * edge, r.height - 2f * edge), _panelTex);
        }

        /// <summary>The open dropdown's list: under its button, or above it where there is more room; scrolls with the wheel.</summary>
        private static void DrawList()
        {
            if (!_dropOpen) return;
            Reveal(Overlay.List);
            var ev = Event.current;
            int n = _dropItems.Count;
            float rowH = _listRowH, pad = Px(4f);
            float below = Screen.height - _dropAnchor.yMax - Px(8f), above = _dropAnchor.y - Px(8f);
            int fitBelow = Mathf.FloorToInt((below - 2f * pad) / rowH), fitAbove = Mathf.FloorToInt((above - 2f * pad) / rowH);
            bool down = fitBelow >= Mathf.Min(n, 8) || fitBelow >= fitAbove;
            int shown = Mathf.Clamp(Mathf.Min(n, down ? fitBelow : fitAbove), 1, n);
            float h = shown * rowH + 2f * pad;
            float x = Mathf.Clamp(_dropAnchor.x, Px(4f), Mathf.Max(Px(4f), Screen.width - _dropWidth - Px(4f)));
            float y = down ? _dropAnchor.yMax + Px(2f) : _dropAnchor.y - h - Px(2f);
            _dropRect = new Rect(x, y, _dropWidth, h);
            _dropPlaced = true;
            _dropFirst = Mathf.Clamp(_dropFirst, 0, n - shown);

            if (ev.type == EventType.ScrollWheel && _dropRect.Contains(ev.mousePosition))
            {
                _dropFirst = Mathf.Clamp(_dropFirst + (ev.delta.y > 0f ? 1 : -1), 0, n - shown);
                ev.Use();
            }

            DrawFloatingPanel(_dropRect);
            bool bar = n > shown;
            float barW = bar ? Px(6f) : 0f;
            int hovered = -1;
            for (int j = 0; j < shown; j++)
            {
                int i = _dropFirst + j;
                var item = _dropItems[i];
                var r = new Rect(x + pad, y + pad + j * rowH, _dropWidth - 2f * pad - barW, rowH);
                bool hover = r.Contains(ev.mousePosition);
                if (hover) hovered = i;
                if (ev.type == EventType.MouseDown && ev.button == 0 && hover)
                {
                    var pick = _dropPick;
                    int index = i;
                    if (pick != null) _layoutAction = () => pick(index);
                    _hoverShown = -1;   // the pick takes over from the preview: no flash of the old look between
                    CloseDrop();
                    ev.Use();
                    return;
                }
                if (ev.type != EventType.Repaint) continue;
                bool on = i == _dropSelected;
                if (on) GUI.DrawTexture(r, _titleTex);
                else if (hover) GUI.DrawTexture(r, _rowHoverTex);
                float left = 0f;
                if (item.Swatch.HasValue)
                {
                    var old = GUI.color;
                    var box = new Rect(r.x + Px(8f), r.center.y - Px(7f), Px(18f), Px(14f));
                    float edge = Mathf.Max(1f, Px(1f));
                    GUI.color = on ? SailwindSkin.Parchment : SailwindSkin.InkFaint;
                    GUI.DrawTexture(new Rect(box.x - edge, box.y - edge, box.width + 2f * edge, box.height + 2f * edge), Texture2D.whiteTexture);
                    GUI.color = Opaque(item.Swatch.Value);
                    GUI.DrawTexture(box, Texture2D.whiteTexture);
                    GUI.color = old;
                    left = Px(28f);
                }
                else if (item.PatternSwatch)
                {
                    float sh = Mathf.Min(r.height - Px(6f), Px(26f));
                    DrawPatternSwatch(new Rect(r.x + Px(8f), r.center.y - sh * 0.5f, PatternSwatchW(), sh), item, on);
                    left = PatternSwatchW() + Px(14f);
                }
                (on ? _listTextOn : _listText).Draw(new Rect(r.x + left, r.y, r.width - left, r.height), item.Text, false, false, false, false);
            }
            if (_dropKey == PatternKey && ev.type == EventType.Repaint) HoverPattern(hovered);
            if (bar && ev.type == EventType.Repaint)
            {
                // Where the rows shown sit in the whole list.
                var old = GUI.color;
                var track = new Rect(x + _dropWidth - pad - barW, y + pad, barW, shown * rowH);
                GUI.color = new Color(0f, 0f, 0f, 0.12f);
                GUI.DrawTexture(track, Texture2D.whiteTexture);
                GUI.color = SailwindSkin.InkFaint;
                GUI.DrawTexture(new Rect(track.x, track.y + track.height * _dropFirst / n, barW, track.height * shown / n), Texture2D.whiteTexture);
                GUI.color = old;
            }
            if (ev.type == EventType.MouseDown && _dropRect.Contains(ev.mousePosition)) ev.Use();
        }

        private static float PatternSwatchW() { return Px(42f); }

        /// <summary>A pattern list row's swatch: framed like the color swatches, the base color with the pattern over it.</summary>
        private static void DrawPatternSwatch(Rect box, DropItem item, bool on)
        {
            var old = GUI.color;
            box = new Rect(Mathf.Round(box.x), Mathf.Round(box.y), Mathf.Round(box.width), Mathf.Round(box.height));
            // A faint one pixel edge, so the tile is what stands out.
            Color frame = on ? SailwindSkin.Parchment : SailwindSkin.InkFaint;
            frame.a *= 0.6f;
            GUI.color = frame;
            GUI.DrawTexture(new Rect(box.x - 1f, box.y - 1f, box.width + 2f, box.height + 2f), Texture2D.whiteTexture);
            GUI.color = Opaque(item.PatBase);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            if (item.Image != null)
            {
                // The picture at the swatch's height, repeated across it, tinted like the body draws it.
                GUI.color = Opaque(item.PatColor);
                float across = box.width / Mathf.Max(box.height * Mathf.Max(item.ImageAspect, 0.05f), 1f);
                GUI.DrawTextureWithTexCoords(box, item.Image, new Rect(0f, 0f, across, 1f), true);
            }
            else if (item.Pattern > 0 && item.Pattern < PlayerAppearance.PatternKeys.Length)
            {
                GUI.color = Opaque(item.PatColor);
                // The body's band count goes as 1.5 * 0.4^size (BodyPattern.cginc), so the bands here widen by its inverse.
                // The Scale is rounded to 16 steps, finer than a swatch this size shows, so the strips drawn for the list
                // stay few however much the slider moves.
                byte size = (byte)(Mathf.RoundToInt(item.PatSize / 17f) * 17);
                float k = 1.5f * Mathf.Pow(0.4f, size / 255f);
                GUI.DrawTexture(box, UiIcons.PatternStrip(item.Pattern, (int)box.width, (int)box.height, Px(0.6f) * 0.95f / k, size));
            }
            GUI.color = old;
        }

        // ---- the picker -------------------------------------------------------------------------------------

        private static bool IsPicking(int part, int k, bool pattern)
        {
            return _pickPart == part && _pickColor == k && _pickPattern == pattern;
        }


        /// <summary>
        /// The picker, in the Colors tab under the color rows, always showing the selected swatch: for cloth and trim a
        /// Pattern dropdown and, with a pattern, its Scale, then hue, saturation and brightness sliders whose tracks show
        /// the colors they lead to. It takes the same height whatever it shows, so the layout never moves under it. A
        /// change recolors at once, without rebuilding the model (PreviewColors), since nothing but a color moved.
        /// </summary>
        private static void DrawPickerDocked()
        {
            float rowH = Mathf.Max(_buttonH, Px(30f)) + Px(4f), gap = Px(10f);
            var area = GUILayoutUtility.GetRect(10f, 6f * rowH, GUILayout.ExpandWidth(true));
            if (_pickPart == NoPick || _pickColor < 0) return;
            bool takes = PlayerAppearance.TakesPattern(_pickColor);
            PlayerAppearance.Pattern eff;
            bool patterned = EffectivePickPattern(out eff) && eff.Type > 0;
            float lx = area.x + Px(4f), fx = lx + _popLabelW + gap;
            float right = Mathf.Min(area.xMax - Px(8f), fx + Px(340f));
            float cy = area.y;
            GUI.Label(new Rect(lx, cy, area.width, rowH), PickTitle(), _section);
            cy += rowH;
            if (takes)
            {
                GUI.Label(new Rect(lx, cy, _popLabelW, rowH), "Pattern", _label);
                Dropdown(new Rect(fx, cy + (rowH - _buttonH) * 0.5f, _dropW, _buttonH), PatternKey, DescribePattern(),
                    PatternItems, PatternChoice(), SetPatternChoice);
                cy += rowH;
                if (patterned)
                {
                    // From small to large, as fine as a drag goes. Its track is plain: there is no color to show.
                    float size = eff.Size / 255f;
                    if (GradientSlider(new Rect(lx, cy, right - lx, rowH), fx, "Scale", ref size, null))
                        SetPatternSize(Mathf.RoundToInt(size * 255f));
                    cy += rowH;
                }
            }

            UpdateGradients();
            bool changed = false;
            changed |= GradientSlider(new Rect(lx, cy, right - lx, rowH), fx, "Hue", ref _hue, _gradHue); cy += rowH;
            changed |= GradientSlider(new Rect(lx, cy, right - lx, rowH), fx, "Saturation", ref _sat, _gradSat); cy += rowH;
            changed |= GradientSlider(new Rect(lx, cy, right - lx, rowH), fx, "Brightness", ref _val, _gradVal);
            if (!changed) return;
            var c = (Color32)Color.HSVToRGB(_hue, _sat, _val);
            if (_pickPattern && patterned)
            {
                // On a part following the body's pattern, the first change gives the part a pattern of its own.
                eff.Color = c;
                SetPickPattern(eff);
            }
            else if (_pickPart == BodyPart) _working.SetRgb(_pickColor, c);
            else _working.SetPartRgb(_pickPart, _pickColor, c);
            _dirty = true;
            PreviewColors();
        }

        /// <summary>
        /// Keep a swatch selected for the docked picker: the cloth of the target on the Paint row when nothing valid is
        /// (the screen just opened, the target changed, a roll or an outfit replaced the look), and the base color
        /// when the selected pattern is gone. Changes no rows, so it can run on any event.
        /// </summary>
        private static void EnsurePick()
        {
            bool valid = _pickPart == _paintTarget && _pickColor >= 0 && _pickColor < PlayerAppearance.ColorSlotCount
                && (_paintTarget == BodyPart || PlayerAppearance.ColorSlots[_pickColor].PerPart);
            if (!valid)
            {
                _pickPart = _paintTarget;
                _pickColor = ClothSlot();
                _pickPattern = false;
                LoadPicker();
                return;
            }
            PlayerAppearance.Pattern eff;
            if (_pickPattern && !(EffectivePickPattern(out eff) && eff.Type > 0))
            {
                _pickPattern = false;
                LoadPicker();
            }
        }

        private static int ClothSlot()
        {
            for (int i = 0; i < PlayerAppearance.ColorSlotCount; i++)
                if (PlayerAppearance.ColorSlots[i].Key == "c_primary") return i;
            return 0;
        }

        private static string PickTitle()
        {
            string what = PlayerAppearance.ColorSlots[_pickColor].Label + (_pickPattern ? " pattern" : "");
            return _pickPart == BodyPart ? what : TargetName(_pickPart) + ": " + what;
        }

        /// <summary>
        /// A slider over a gradient of the colors it leads to, with a marker for the thumb. The slider itself draws
        /// nothing (bare styles); its thumb is _thumbW wide, so the gradient runs between the thumb's two end points.
        /// </summary>
        private static bool GradientSlider(Rect row, float fieldX, string label, ref float value, Texture2D gradient)
        {
            GUI.Label(new Rect(row.x, row.y, fieldX - row.x, row.height), label, _label);
            float sh = Px(22f), tw = _thumbBare.fixedWidth;
            var r = new Rect(fieldX, row.center.y - sh * 0.5f, row.xMax - fieldX, sh);
            var ev = Event.current;
            if (ev.type == EventType.Repaint)
            {
                float band = Px(10f), edge = Mathf.Max(1f, Px(1f));
                var track = new Rect(r.x + tw * 0.5f, r.center.y - band * 0.5f, r.width - tw, band);
                var old = GUI.color;
                GUI.color = SailwindSkin.InkFaint;
                GUI.DrawTexture(new Rect(track.x - edge, track.y - edge, track.width + 2f * edge, track.height + 2f * edge), Texture2D.whiteTexture);
                GUI.color = old;
                if (gradient != null) GUI.DrawTexture(track, gradient);
            }
            float next = GUI.HorizontalSlider(r, value, 0f, 1f, _sliderBare, _thumbBare);
            if (ev.type == EventType.Repaint)
            {
                float cx = r.x + tw * 0.5f + (r.width - tw) * next;
                var mark = new Rect(Mathf.Round(cx - Px(4f)), r.center.y - Px(10f), Px(8f), Px(20f));
                float edge = Px(2f);
                var old = GUI.color;
                GUI.color = SailwindSkin.InkColor;
                GUI.DrawTexture(mark, Texture2D.whiteTexture);
                GUI.color = SailwindSkin.Parchment;
                GUI.DrawTexture(new Rect(mark.x + edge, mark.y + edge, mark.width - 2f * edge, mark.height - 2f * edge), Texture2D.whiteTexture);
                GUI.color = old;
            }
            if (Mathf.Approximately(next, value)) return false;
            value = next;
            return true;
        }

        private static Texture2D _gradHue, _gradSat, _gradVal;
        private static Vector3 _gradFor = new Vector3(-1f, -1f, -1f);
        private const int GradientSteps = 64;

        /// <summary>
        /// The slider tracks: every hue at full strength, then gray to the hue at this brightness, then black to the
        /// color. Remade only when the color moves.
        /// </summary>
        private static void UpdateGradients()
        {
            if (_gradHue == null)
            {
                _gradHue = MakeGradient();
                _gradSat = MakeGradient();
                _gradVal = MakeGradient();
                var hue = new Color[GradientSteps];
                for (int i = 0; i < GradientSteps; i++) hue[i] = Color.HSVToRGB(i / (GradientSteps - 1f), 1f, 1f);
                _gradHue.SetPixels(hue);
                _gradHue.Apply(false);
            }
            var now = new Vector3(_hue, _sat, _val);
            if (now == _gradFor) return;
            _gradFor = now;
            var sat = new Color[GradientSteps];
            var val = new Color[GradientSteps];
            for (int i = 0; i < GradientSteps; i++)
            {
                float t = i / (GradientSteps - 1f);
                sat[i] = Color.HSVToRGB(_hue, t, _val);
                val[i] = Color.HSVToRGB(_hue, _sat, t);
            }
            _gradSat.SetPixels(sat);
            _gradSat.Apply(false);
            _gradVal.SetPixels(val);
            _gradVal.Apply(false);
        }

        private static Texture2D MakeGradient()
        {
            return new Texture2D(GradientSteps, 1, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        // A new pattern's size: the middle of the slider.
        private const byte MediumSize = 128;

        /// <summary>The open pick's own pattern entry (the body's, or the part's), if it has one.</summary>
        private static bool PickPattern(out PlayerAppearance.Pattern p)
        {
            return _pickPart == BodyPart ? _working.TryGetBodyPattern(_pickColor, out p) : _working.TryGetPartPattern(_pickPart, _pickColor, out p);
        }

        /// <summary>The pattern the open pick shows: its own entry, else, on a part, the body's.</summary>
        private static bool EffectivePickPattern(out PlayerAppearance.Pattern p)
        {
            if (PickPattern(out p)) return true;
            return _pickPart >= 0 && _working.TryGetBodyPattern(_pickColor, out p);
        }

        private static void SetPickPattern(PlayerAppearance.Pattern? p)
        {
            if (_pickPart == BodyPart) _working.SetBodyPattern(_pickColor, p);
            else _working.SetPartPattern(_pickPart, _pickColor, p);
        }

        /// <summary>
        /// The open pick's pattern as a position in its list of choices. On a part the first choice is "Same as body"
        /// (no entry of its own), then None and the patterns; on the body it is None and the patterns.
        /// </summary>
        private static int PatternChoice()
        {
            PlayerAppearance.Pattern p;
            if (!PickPattern(out p)) return 0;
            int at = p.Type;
            if (p.Type == PlayerAppearance.ImageType)
            {
                // A picture comes after the built in patterns, in the folder's order. One not in the folder selects nothing.
                int i = PatternImages.Names.IndexOf(p.Image);
                at = i >= 0 ? PlayerAppearance.PatternNames.Length + i : -1;
                if (at < 0) return -1;
            }
            return at + (_pickPart != BodyPart ? 1 : 0);
        }

        private static string DescribePattern()
        {
            PlayerAppearance.Pattern own;
            if (PickPattern(out own) && own.Type == PlayerAppearance.ImageType && own.Image != null) return own.Image;
            int choice = PatternChoice();
            if (_pickPart != BodyPart)
            {
                if (choice == 0) return SameText;
                choice--;
            }
            return PlayerAppearance.PatternNames[Mathf.Clamp(choice, 0, PlayerAppearance.PatternNames.Length - 1)];
        }

        private static System.Collections.Generic.List<DropItem> PatternItems()
        {
            // Each swatch in the colors the pick would wear: its base color under the pattern color it has, or would get.
            Color under;
            Color32 own;
            if (_pickPart >= 0 && _working.TryGetPartRgb(_pickPart, _pickColor, out own)) under = own;
            else if (!_working.TryGetBodyColor(_pickColor, out under)) under = SailwindSkin.ParchmentDark;
            PlayerAppearance.Pattern eff;
            bool hasEff = EffectivePickPattern(out eff) && eff.Type > 0;
            bool effImage = hasEff && eff.Type == PlayerAppearance.ImageType;
            // A picture's color is a tint and a pattern's is a second color, so neither carries over to the other kind.
            Color over = hasEff && !effImage ? (Color)eff.Color : (Color)DefaultPatternColor();
            Color tint = effImage ? (Color)eff.Color : Color.white;
            byte size = hasEff ? eff.Size : MediumSize;   // the swatches at the Scale set now

            var items = new System.Collections.Generic.List<DropItem>();
            if (_pickPart != BodyPart)
            {
                // "Same as body" shows the body's pattern.
                PlayerAppearance.Pattern body;
                bool has = _working.TryGetBodyPattern(_pickColor, out body) && body.Type > 0;
                var same = new DropItem { Text = SameText, PatternSwatch = true, Pattern = has ? body.Type : 0,
                    PatBase = under, PatColor = has ? (Color)body.Color : over, PatSize = has ? body.Size : size };
                Texture2D bodyImage;
                float bodyAspect;
                if (has && body.Type == PlayerAppearance.ImageType && PatternImages.TryGet(body.Image, out bodyImage, out bodyAspect))
                {
                    same.Image = bodyImage;
                    same.ImageAspect = bodyAspect;
                }
                items.Add(same);
            }
            for (int t = 0; t < PlayerAppearance.PatternNames.Length; t++)
                items.Add(new DropItem { Text = PlayerAppearance.PatternNames[t], PatternSwatch = true, Pattern = t,
                    PatBase = under, PatColor = over, PatSize = size });
            // The pictures in the patterns folder, by name. All decoded first, so the list holds only usable ones and
            // lines up with the names PatternChoice and ApplyPatternChoice index.
            PatternImages.LoadListed();
            foreach (var name in PatternImages.Names)
            {
                Texture2D image;
                float aspect;
                PatternImages.TryGet(name, out image, out aspect);
                items.Add(new DropItem { Text = name, PatternSwatch = true, Pattern = PlayerAppearance.ImageType,
                    PatBase = under, PatColor = tint, PatSize = size, Image = image, ImageAspect = aspect });
            }
            return items;
        }

        /// <summary>
        /// A pattern picked from the list. A new one starts from the pattern the pick shows now (the body's, on a part
        /// that follows it), else medium in a color that reads against the base.
        /// </summary>
        private static void SetPatternChoice(int choice)
        {
            if (_pickPart == NoPick) return;
            ApplyPatternChoice(choice);
            PlayerAppearance.Pattern now;
            if (!EffectivePickPattern(out now) || now.Type == 0) _pickPattern = false;
            LoadPicker();
            _dirty = true;
            PreviewColors();
        }

        /// <summary>
        /// Put a pattern list choice into _working, and nothing else: shared by a pick and by the hover preview, which
        /// runs it on a copy.
        /// </summary>
        private static void ApplyPatternChoice(int choice)
        {
            bool part = _pickPart != BodyPart;
            int type = part ? choice - 1 : choice;   // -1 on a part: same as body
            if (type < 0 || (!part && type == 0))
            {
                // A body with no pattern needs no entry, and "same as body" on a part is no entry.
                SetPickPattern(null);
            }
            else
            {
                PlayerAppearance.Pattern basis;
                bool hasBasis = EffectivePickPattern(out basis) && basis.Type > 0;
                var next = hasBasis ? basis : new PlayerAppearance.Pattern { Size = MediumSize, Color = DefaultPatternColor() };
                bool wasImage = hasBasis && basis.Type == PlayerAppearance.ImageType;
                int image = type - PlayerAppearance.PatternNames.Length;
                if (image >= 0)
                {
                    var names = PatternImages.Names;
                    if (image >= names.Count) return;
                    next.Type = PlayerAppearance.ImageType;
                    next.Image = names[image];
                    // A picture starts as drawn: a white tint.
                    if (!wasImage) next.Color = new Color32(255, 255, 255, 255);
                }
                else
                {
                    next.Type = (byte)Mathf.Clamp(type, 0, PlayerAppearance.PatternKeys.Length - 1);
                    next.Image = null;
                    // A picture's tint is usually white, which is no color for stripes.
                    if (wasImage) next.Color = DefaultPatternColor();
                }
                SetPickPattern(next);
            }
        }

        /// <summary>
        /// Show the Pattern list row under the mouse on the character, or with -1 put the look back. Runs on a copy of
        /// the look, so nothing is kept until a row is clicked. Only on a change of row: a recolor, no rebuild.
        /// </summary>
        private static void HoverPattern(int index)
        {
            if (index == _hoverShown || _pickPart == NoPick) return;
            _hoverShown = index;
            if (index < 0) { PreviewColors(); return; }
            var saved = _working;
            try
            {
                _working = PlayerAppearance.Deserialize(saved.Serialize());
                ApplyPatternChoice(index);
                PreviewColors();
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Pattern preview failed: " + e.Message); }
            finally { _working = saved; }
        }

        private static void SetPatternSize(int size)
        {
            PlayerAppearance.Pattern p;
            if (!EffectivePickPattern(out p) || p.Type == 0) return;
            p.Size = (byte)Mathf.Clamp(size, 0, 255);
            SetPickPattern(p);
            _dirty = true;
            PreviewColors();
        }

        /// <summary>A first pattern color that reads against the base: a dark shade of a light color, a pale tint of a dark one.</summary>
        private static Color32 DefaultPatternColor()
        {
            Color baseColor = Color.white;
            Color32 own;
            if (_pickPart >= 0 && _working.TryGetPartRgb(_pickPart, _pickColor, out own)) baseColor = own;
            else _working.TryGetBodyColor(_pickColor, out baseColor);
            float h, sat, v;
            Color.RGBToHSV(baseColor, out h, out sat, out v);
            return v > 0.5f ? (Color32)Color.HSVToRGB(h, sat, v * 0.35f) : (Color32)Color.Lerp(baseColor, Color.white, 0.75f);
        }

        /// <summary>Open the picker on a swatch, or close it when it is already open there.</summary>
        private static void TogglePicker(int part, int colorSlot, bool pattern)
        {
            if (IsPicking(part, colorSlot, pattern)) return;
            _pickPart = part;
            _pickColor = colorSlot;
            _pickPattern = pattern;
            if (_dropOpen && _dropKey == PatternKey) CloseDrop();
            LoadPicker();
        }

        private static void ClosePicker()
        {
            _pickPart = NoPick;
            _pickColor = -1;
            _pickPattern = false;
            if (_dropOpen && _dropKey == PatternKey) CloseDrop();
        }

        /// <summary>Start the sliders at the color the pick shows now: the pattern's, the part's own, else the body's.</summary>
        private static void LoadPicker()
        {
            Color c = Color.white;
            Color32 own;
            PlayerAppearance.Pattern pattern;
            if (_pickPattern && EffectivePickPattern(out pattern) && pattern.Type > 0) c = pattern.Color;
            else if (_pickPart >= 0 && _working.TryGetPartRgb(_pickPart, _pickColor, out own)) c = own;
            else _working.TryGetBodyColor(_pickColor, out c);
            Color.RGBToHSV(c, out _hue, out _sat, out _val);
        }

        // ---- icon buttons and the tooltip ---------------------------------------------------------------------

        /// <summary>A square button with an icon, in a GUILayout row. Its tooltip shows while the mouse is on it.</summary>
        private static bool IconButton(UiIcons.Kind icon, string tip, bool enabled)
        {
            bool was = GUI.enabled;
            GUI.enabled = was && enabled;
            bool hit = GUILayout.Button(GUIContent.none, _button, GUILayout.Width(_buttonH), GUILayout.Height(_buttonH));
            var r = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.Repaint)
            {
                bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
                Color tint = !GUI.enabled ? SailwindSkin.InkFaint : hover ? _button.hover.textColor : _button.normal.textColor;
                UiIcons.Draw(r, icon, IconPx(), tint);
                if (hover) _tipText = tip;
            }
            GUI.enabled = was;
            return hit;
        }

        /// <summary>The tooltip of the icon under the mouse, beside it. Repaint only, after everything else.</summary>
        private static void DrawTooltip()
        {
            if (Event.current.type != EventType.Repaint || string.IsNullOrEmpty(_tipText)) return;
            var content = new GUIContent(_tipText);
            var size = _tip.CalcSize(content);
            var m = Event.current.mousePosition;
            float x = Mathf.Clamp(m.x + Px(14f), Px(4f), Mathf.Max(Px(4f), Screen.width - size.x - Px(4f)));
            float y = m.y + Px(20f);
            if (y + size.y > Screen.height - Px(4f)) y = m.y - size.y - Px(8f);
            _tip.Draw(new Rect(x, y, size.x, size.y), content, false, false, false, false);
        }

        // ---- turning the preview -------------------------------------------------------------------------

        /// <summary>
        /// A Turn button, with its curved arrow. A click turns the model a step at once; held past SpinDelay it keeps
        /// turning (UpdateSpin). Built on its own control id rather than GUI.RepeatButton, whose true or false per event
        /// says nothing about how long it has been held.
        /// </summary>
        private static void TurnButton(Rect r, UiIcons.Kind icon, string tip, int dir)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var ev = Event.current;
            switch (ev.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (ev.button == 0 && r.Contains(ev.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        _turnHoldId = id;
                        _turnHoldDir = dir;
                        _turnHoldSince = Time.unscaledTime;
                        CharacterPreviewStudio.Turn(dir * TapTurnDeg);
                        ev.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id) ev.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        _turnHoldDir = 0;
                        ev.Use();
                    }
                    break;
                case EventType.Repaint:
                    _button.Draw(r, GUIContent.none, id);
                    bool hover = r.Contains(ev.mousePosition);
                    UiIcons.Draw(r, icon, IconPx(), hover || GUIUtility.hotControl == id ? _button.hover.textColor : _button.normal.textColor);
                    if (hover) _tipText = tip;
                    break;
            }
        }

        /// <summary>The held Turn button's spin, handed to the studio, which turns the model each frame.</summary>
        private static void UpdateSpin()
        {
            bool held = _turnHoldDir != 0 && GUIUtility.hotControl == _turnHoldId;
            if (!held) _turnHoldDir = 0;
            CharacterPreviewStudio.SpinDegPerSec = held && Time.unscaledTime - _turnHoldSince >= SpinDelay ? _turnHoldDir * SpinRate : 0f;
        }

        /// <summary>
        /// Drag across the preview to turn the model: one preview width turns it all the way round, its front following
        /// the mouse.
        /// </summary>
        private static void PreviewDrag(Rect r, float width)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var ev = Event.current;
            switch (ev.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (ev.button == 0 && r.Contains(ev.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        ev.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        CharacterPreviewStudio.Turn(-ev.delta.x * 360f / Mathf.Max(width, 1f));
                        ev.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        ev.Use();
                    }
                    break;
            }
        }

        // ---- randomize history ---------------------------------------------------------------------------

        /// <summary>
        /// Roll a random look. The look on now is kept first, so the back button can return to it: as the newest entry
        /// unless the history is already at it unchanged. Rolls ahead of the one on now are dropped, as in a browser.
        /// </summary>
        private static void Randomize(PsychoticLab.CharacterCustomizer customizer)
        {
            string now = _working.Serialize();
            if (_rollAt >= 0 && _rollAt < _rolls.Count - 1) _rolls.RemoveRange(_rollAt + 1, _rolls.Count - _rollAt - 1);
            if (_rollAt < 0 || _rolls[_rollAt] != now) _rolls.Add(now);

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
            _working.Stubble = (byte)Random.Range(0, PlayerAppearance.StubbleNames.Length);
            // A roll is palette colors on every part.
            _working.ClearCustomColors();
            RollPatterns();
            ClosePicker();
            CloseDrop();
            _dirty = true;
            Preview();

            _rolls.Add(_working.Serialize());
            while (_rolls.Count > RollsKept) _rolls.RemoveAt(0);
            _rollAt = _rolls.Count - 1;
        }

        /// <summary>
        /// Now and then a pattern on a roll: on the cloth about a third of the time, on the trim about a fifth, of any
        /// type, at a scale away from the extremes, in another color of the same palette that stands out from the base.
        /// Whole-body only: a roll keeps one color per slot, since random colors on every part look like a costume.
        /// </summary>
        private static void RollPatterns()
        {
            for (int i = 0; i < PlayerAppearance.ColorSlotCount; i++)
            {
                if (!PlayerAppearance.TakesPattern(i)) continue;
                float chance = PlayerAppearance.ColorSlots[i].Key == "c_primary" ? 0.35f : 0.2f;
                if (Random.value > chance) continue;
                var palette = PlayerAppearance.ColorSlots[i].Palette;
                int v = _working.GetColor(i);
                Color under = v > 0 && v <= palette.Length ? palette[v - 1].Color : Color.gray;
                Color over = under;
                for (int tries = 0; tries < 10 && Mathf.Abs(Luma(over) - Luma(under)) < 0.25f; tries++)
                    over = palette[Random.Range(0, palette.Length)].Color;
                // A palette with nothing far enough from the base: a dark shade of a light color, a pale tint of a dark one.
                if (Mathf.Abs(Luma(over) - Luma(under)) < 0.25f)
                    over = Luma(under) > 0.5f ? Color.Lerp(under, Color.black, 0.6f) : Color.Lerp(under, Color.white, 0.7f);
                _working.SetBodyPattern(i, new PlayerAppearance.Pattern
                {
                    Type = (byte)Random.Range(1, PlayerAppearance.PatternKeys.Length),
                    Size = (byte)Random.Range(48, 208),
                    Color = (Color32)over,
                });
            }
        }

        private static float Luma(Color c) { return 0.299f * c.r + 0.587f * c.g + 0.114f * c.b; }

        /// <summary>
        /// Step back or forward through the rolls. An edit made to the look on now is kept in its place, so stepping
        /// back to it brings the edit back. Runs on a Layout event: the rows can change.
        /// </summary>
        private static void StepRoll(int dir)
        {
            int to = _rollAt + dir;
            if (_rollAt < 0 || _rollAt >= _rolls.Count || to < 0 || to >= _rolls.Count) return;
            _rolls[_rollAt] = _working.Serialize();
            _rollAt = to;
            CloseDrop();
            Wear(_rolls[to]);
        }

        /// <summary>Do the row change the last click recorded. Layout events only, ahead of anything being placed.</summary>
        private static void RunLayoutAction()
        {
            if (_layoutAction == null || Event.current.type != EventType.Layout) return;
            var action = _layoutAction;
            _layoutAction = null;
            try { action(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Color row action failed: " + e.Message); }
        }

        /// <summary>
        /// Recolor the player's body and the studio mannequin without rebuilding either. For color changes only; a
        /// change of part goes through Preview.
        /// </summary>
        private static void PreviewColors()
        {
            try
            {
                var c = PlayerModel.GetCustomizer();
                if (c != null) _working.ApplyColorsLive(c);
                CharacterPreviewStudio.SetColors(_working);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Color preview failed: " + e.Message); }
        }

        /// <summary>
        /// Full, light or no stubble, under Facial hair (so male only, like that row). A color change, so the body is
        /// recolored rather than rebuilt.
        /// </summary>
        private static void DrawStubbleRow()
        {
            var names = PlayerAppearance.StubbleNames;
            int at = Mathf.Clamp(_working.Stubble, 0, names.Length - 1);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Stubble", _label, GUILayout.Width(_labelW));
            int dir = Stepper(names[at]);
            if (dir != 0)
            {
                _working.Stubble = (byte)((at + dir + names.Length) % names.Length);
                _dirty = true;
                PreviewColors();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
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

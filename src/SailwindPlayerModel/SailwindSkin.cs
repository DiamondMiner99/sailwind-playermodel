using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Sailwind's own typeface and palette, for IMGUI panels that should not look like a
    /// developer console bolted onto a sailing game.
    ///
    /// THE USEFUL SURPRISE: Sailwind contains NO TextMeshPro at all - no TMPro assembly in Managed/, no
    /// TMP references anywhere in the game code. Its entire UI is world-space UnityEngine.TextMesh driven
    /// by real UnityEngine.Font assets. That matters because a TMP_FontAsset CANNOT be assigned to a
    /// GUIStyle, so the usual answer for modded IMGUI is "match the colours and accept a mismatched
    /// font". Here the game's actual typeface is available, and it is a dynamic TrueType face, so
    /// GUIStyle.fontSize and fontStyle behave normally rather than being locked to a baked atlas size.
    ///
    /// The palette is sampled from the shipped assets rather than eyeballed: the start-menu scroll mesh
    /// uses material "scroll_item_paint", whose parchment swatch is #BD9D83 ramping to #524439, and the
    /// button plates are lit by an emission colour of #DBA583. Menu label text is near-black (17,17,19).
    ///
    /// Everything here degrades quietly: a missing font leaves IMGUI's default, and the colours stand on
    /// their own.
    /// </summary>
    public static class SailwindSkin
    {
        // Sampled from Sailwind's own UI assets.
        public static readonly Color Parchment = new Color32(0xBD, 0x9D, 0x83, 0xFF);
        public static readonly Color ParchmentDark = new Color32(0x52, 0x44, 0x39, 0xFF);
        public static readonly Color ButtonPlate = new Color32(0xDB, 0xA5, 0x83, 0xFF);
        public static readonly Color InkColor = new Color32(17, 17, 19, 0xFF);
        public static readonly Color InkFaint = new Color32(0x52, 0x44, 0x39, 0xCC);

        private static Font _font;
        private static bool _searched;

        /// <summary>
        /// Sailwind's menu typeface ("IMMORTAL"), or null if it cannot be found - in which case callers
        /// should simply leave GUIStyle.font alone and take IMGUI's default.
        /// </summary>
        public static Font MenuFont
        {
            get
            {
                if (_searched) return _font;
                _searched = true;
                try
                {
                    // Prefer the font by name. FindObjectsOfTypeAll reaches assets that are loaded but not
                    // referenced by an active object, which is what a menu font is most of the time.
                    foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
                    {
                        if (f == null || string.IsNullOrEmpty(f.name)) continue;
                        if (f.name.IndexOf("IMMORTAL", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _font = f;
                            break;
                        }
                    }

                    // Fall back to whatever the game's own TextMeshes are using: if the named lookup ever
                    // breaks (a renamed asset, a game update), borrowing the font off a live label is still
                    // right, where hardcoding a name would silently regress to Arial.
                    if (_font == null)
                    {
                        foreach (var tm in Resources.FindObjectsOfTypeAll<TextMesh>())
                        {
                            if (tm != null && tm.font != null) { _font = tm.font; break; }
                        }
                    }

                    Plugin.Log.LogInfo(_font != null
                        ? "[UI] Using Sailwind's menu font: " + _font.name
                        : "[UI] Sailwind's menu font not found; modded panels will use the default face.");
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("[UI] Font lookup failed, using the default face: " + e.Message);
                }
                return _font;
            }
        }

        /// <summary>A 1x1 texture of a flat colour, marked so it never leaks into a scene or a save.</summary>
        public static Texture2D SolidTexture(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        /// <summary>Apply the game's face to a style, if we have it. No-op otherwise.</summary>
        public static GUIStyle WithFont(this GUIStyle s)
        {
            var f = MenuFont;
            if (f != null && s != null) s.font = f;
            return s;
        }

        private static bool _uiScaleFailed;

        /// <summary>
        /// How many times its 1080p size a screen-space panel should be drawn: the screen height over 1080,
        /// never less than 1, times the UIScale setting.
        ///
        /// WHY PANELS NEED THIS AND THE GAME DOES NOT. Sailwind has no screen-space UI. Its menus and hints
        /// are world-space TextMesh in front of the camera, so their pixel size already follows the screen
        /// height. IMGUI is laid out in raw pixels, and Sailwind.exe is per-monitor DPI aware, so Windows
        /// never enlarges it either: without this a panel sized for 1080p covers half as much of a 4K screen.
        ///
        /// The floor of 1 keeps shorter screens (720p, a Steam Deck) at the 1080p size, where the text is
        /// still readable, instead of shrinking with them. Callers scale font sizes and layout numbers by
        /// this, never GUI.matrix: a matrix stretches glyphs rasterized at the small size, and one left set
        /// by a throw could move another mod's panel away from where it tests its clicks.
        ///
        /// Returns 1, the 1080p size, if anything about it fails, and stops trying after the first failure.
        /// </summary>
        public static float UiScale
        {
            get
            {
                if (_uiScaleFailed) return 1f;
                try
                {
                    float auto = Mathf.Max(1f, Screen.height / 1080f);
                    float user = 1f;
                    var entry = BodyTuning.UIScale;
                    if (entry != null)
                    {
                        user = entry.Value;
                        user = float.IsNaN(user) || float.IsInfinity(user) ? 1f : Mathf.Clamp(user, 0.5f, 2.5f);
                    }
                    float s = auto * user;
                    return float.IsNaN(s) || float.IsInfinity(s) || s <= 0f ? 1f : s;
                }
                catch (System.Exception e)
                {
                    _uiScaleFailed = true;
                    Plugin.Log.LogWarning("[UI] Could not work out the panel scale, keeping the 1080p size: " + e.Message);
                    return 1f;
                }
            }
        }

        /// <summary>A size given in 1080p pixels, at scale s, rounded to whole pixels.</summary>
        public static int Px(float referencePx, float s)
        {
            return Mathf.RoundToInt(referencePx * s);
        }
    }
}

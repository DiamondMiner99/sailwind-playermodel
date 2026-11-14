using System.Collections.Generic;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Small icons for the character screen, drawn in code at the size they are shown: the curved turn arrows under the
    /// preview, the circular back and forward arrows beside Randomize, the dropdown chevron, the dot that marks a part
    /// with colors of its own, and the stripes on a pattern swatch. White on clear, tinted when drawn, so one texture
    /// serves every state. Drawn here rather than taken from a font, since Sailwind's typeface has no arrows, or from
    /// image files, which would need a license and would not stay sharp at every UI scale.
    ///
    /// Each shape is a signed distance, sampled at every pixel's center, which gives a smooth edge at any size. The
    /// shapes are centered on their own bounds, so an arrowhead does not push the icon off center.
    /// </summary>
    internal static class UiIcons
    {
        public enum Kind { TurnLeft, TurnRight, Back, Forward, Chevron, Dot, Stripes }

        private static readonly Dictionary<int, Texture2D> _cache = new Dictionary<int, Texture2D>();

        public static Texture2D Get(Kind kind, int size)
        {
            size = Mathf.Clamp(size, 6, 256);
            int key = (int)kind * 1000 + size;
            Texture2D t;
            if (_cache.TryGetValue(key, out t) && t != null) return t;
            t = Make(kind, size);
            _cache[key] = t;
            return t;
        }

        /// <summary>Draw an icon at its own pixel size, centered in r and tinted. Repaint only.</summary>
        public static void Draw(Rect r, Kind kind, int size, Color tint)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var t = Get(kind, size);
            var old = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(new Rect(Mathf.Round(r.center.x - size * 0.5f), Mathf.Round(r.center.y - size * 0.5f), size, size), t);
            GUI.color = old;
        }

        private static readonly Dictionary<long, Texture2D> _strips = new Dictionary<long, Texture2D>();
        private const int MaxStrips = 128;

        /// <summary>
        /// A pattern drawn flat, w by h pixels, white with the pattern as alpha, for the swatches in the Pattern list. The
        /// same shapes as the body shader (BodyPattern.cginc), flat; the fade comes in toward the bottom, starting where the body's would for this size. unit sets the band size: one UI
        /// pixel at the current scale, or less for a small swatch.
        /// </summary>
        public static Texture2D PatternStrip(int type, int w, int h, float unit, byte size)
        {
            w = Mathf.Clamp(w, 1, 2048);
            h = Mathf.Clamp(h, 1, 256);
            float u = Mathf.Max(0.25f, unit);
            long key = ((long)Mathf.RoundToInt(u * 100f) << 52) | ((long)size << 44) | ((long)type << 40) | ((long)w << 20) | (long)h;
            Texture2D t;
            if (_strips.TryGetValue(key, out t) && t != null) return t;
            // A bound on what the strips can hold: past it they are all freed and drawn again as needed.
            if (_strips.Count >= MaxStrips)
            {
                foreach (var old in _strips.Values) if (old != null) UnityEngine.Object.Destroy(old);
                _strips.Clear();
            }
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f - h * 0.5f;
                    float a = StripAmount(type, fx, fy, w, h, u, size / 255f);
                    px[y * w + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
                }
            t = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
                name = "PlayerModel pattern strip " + type,
            };
            t.SetPixels32(px);
            t.Apply(false, true);
            _strips[key] = t;
            return t;
        }

        private static float StripAmount(int type, float x, float y, float w, float h, float u, float size)
        {
            switch (type)
            {
                case 1: return Band(y, 10f * u, 0.5f);                                   // stripes
                case 2: return Band(x, 11f * u, 0.12f);                                  // pinstripes: thin and apart
                case 3: return Mathf.Abs(Band(y, 14f * u, 0.5f) - Band(x, 14f * u, 0.5f));   // checks
                case 4:                                                                   // plaid
                {
                    float p = 20f * u;
                    float thin = Mathf.Max(Band(y + 0.5f * p, p, 0.06f), Band(x + 0.5f * p, p, 0.06f));
                    return Mathf.Clamp01(0.5f * Band(y, p, 0.35f) + 0.5f * Band(x, p, 0.35f) + 0.8f * thin);
                }
                case 5:                                                                   // fade: rows run bottom up
                    return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(Mathf.Lerp(0.55f, 0.05f, size), 1f, 0.5f - y / Mathf.Max(1f, h)));
                case 6: return Band(y, 10f * u, 0.28f);                                  // breton
                case 7: return Band(x + y, 12f * u, 0.5f);                               // diagonal
                case 8:                                                                   // chevron
                {
                    float p = 16f * u;
                    float zig = Mathf.Abs(x / p - Mathf.Floor(x / p) - 0.5f) * 2f;
                    return Band(y + zig * 0.5f * p, 10f * u, 0.5f);
                }
                case 9:
                case 10:                                                                  // diamonds, argyle
                {
                    float p = 16f * u;
                    float da = x + 1.3f * y, db = x - 1.3f * y;
                    float d = Mathf.Abs(Band(da, p, 0.5f) - Band(db, p, 0.5f));
                    if (type == 9) return d;
                    return Mathf.Max(0.55f * d, Mathf.Max(Band(da + 0.5f * p, p, 0.1f), Band(db + 0.5f * p, p, 0.1f)));
                }
                case 11: return 0.5f * Band(y, 12f * u, 0.5f) + 0.5f * Band(x, 12f * u, 0.5f);   // gingham
                case 12: return Mathf.Max(Band(y, 16f * u, 0.1f), Band(x, 16f * u, 0.1f));       // windowpane
                case 13: return Band(y + 2.5f * u * Mathf.Sin(x / (20f * u) * 6.2831853f), 9f * u, 0.45f);   // waves
                case 14:                                                                  // dots
                {
                    float p = 10f * u;
                    float row = Mathf.Floor(y / p);
                    float gx = x / p + ((Mathf.Abs(row) % 2f) > 0.5f ? 0.5f : 0f);
                    float fx = gx - Mathf.Floor(gx) - 0.5f, fy = y / p - row - 0.5f;
                    return Mathf.Clamp01(0.3f * p - Mathf.Sqrt(fx * fx + fy * fy) * p + 0.5f);
                }
                case 15: return Mathf.Clamp01(x - 0.5f * w + 0.5f);                      // halves
                case 16:                                                                  // quartered: rows run bottom up
                {
                    float half = Mathf.Clamp01(x - 0.5f * w + 0.5f);
                    return y < 0f ? 1f - half : half;
                }
            }
            return 0f;
        }

        /// <summary>The shader's PatBand in pixels: bands period apart, duty the share that is pattern, a one pixel soft edge.</summary>
        private static float Band(float x, float period, float duty)
        {
            float v = x / period;
            float tri = Mathf.Abs(v - Mathf.Floor(v) - 0.5f) * 2f;
            float soft = 1f / period;
            float lo = 1f - duty - soft, hi = 1f - duty + soft;
            return Mathf.Clamp01((tri - lo) / (hi - lo));
        }

        private static Texture2D Make(Kind kind, int size)
        {
            float s = size;
            Vector2 shift = kind == Kind.Stripes ? Vector2.zero : CenterShift(kind, s);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // Texture rows run bottom up, like the math here: y is up.
                    float d = Distance(kind, new Vector2(x + 0.5f, y + 0.5f) - shift, s);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(0.5f - d) * 255f));
                }
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
                name = "PlayerModel icon " + kind,
            };
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        /// <summary>How far to move a shape so the box around what it draws is centered in the icon.</summary>
        private static Vector2 CenterShift(Kind kind, float s)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            int n = Mathf.CeilToInt(s * 2f);
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    var p = new Vector2(i * 0.5f + 0.25f, j * 0.5f + 0.25f);
                    if (Distance(kind, p, s) >= 0.5f) continue;
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                }
            if (minX > maxX) return Vector2.zero;
            return new Vector2(s * 0.5f - (minX + maxX) * 0.5f, s * 0.5f - (minY + maxY) * 0.5f);
        }

        /// <summary>Distance in pixels from p to the shape, negative inside.</summary>
        private static float Distance(Kind kind, Vector2 p, float s)
        {
            switch (kind)
            {
                // Turning the model: a C (counterclockwise) or a reversed C (clockwise), open on one side.
                case Kind.TurnLeft: return Arrow(Mirror(p, s), s, 180f, 120f);
                case Kind.TurnRight: return Arrow(p, s, 180f, 120f);
                // Back and forward through the rolls: nearly closed circles, open at the top.
                case Kind.Back: return Arrow(Mirror(p, s), s, 60f, 60f);
                case Kind.Forward: return Arrow(p, s, 60f, 60f);
                case Kind.Chevron:
                    return Triangle(p, new Vector2(0.22f * s, 0.64f * s), new Vector2(0.78f * s, 0.64f * s), new Vector2(0.5f * s, 0.34f * s));
                case Kind.Dot:
                    return (p - new Vector2(0.5f * s, 0.5f * s)).magnitude - 0.24f * s;
                case Kind.Stripes:
                {
                    // Diagonal bands, three across, for a swatch that stands for a pattern.
                    float period = s / 3f;
                    float u = (p.x + p.y) * 0.70710678f / period;
                    return (Mathf.Abs(u - Mathf.Floor(u) - 0.5f) - 0.25f) * period;
                }
            }
            return 1e3f;
        }

        private static Vector2 Mirror(Vector2 p, float s) { return new Vector2(s - p.x, p.y); }

        private static Vector2 Polar(float degrees, float r)
        {
            float a = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
        }

        /// <summary>
        /// A clockwise circular arrow around the icon's center, with a gap of gapDeg centered on gapCenterDeg (degrees
        /// counterclockwise from the right). The stroke starts at the gap's lower edge, runs clockwise round to its upper
        /// edge, and ends in the head there, pointing back across the gap. Mirrored, it turns counterclockwise.
        /// </summary>
        private static float Arrow(Vector2 p, float s, float gapCenterDeg, float gapDeg)
        {
            var q = p - new Vector2(0.5f * s, 0.5f * s);
            float radius = 0.28f * s, half = 0.07f * s;
            float start = gapCenterDeg - gapDeg * 0.5f;
            float span = 360f - gapDeg;
            float end = start - span;

            float phi = Mathf.Atan2(q.y, q.x) * Mathf.Rad2Deg;
            float stroke;
            if (Mathf.Repeat(start - phi, 360f) <= span) stroke = Mathf.Abs(q.magnitude - radius) - half;
            else stroke = Mathf.Min((q - Polar(start, radius)).magnitude, (q - Polar(end, radius)).magnitude) - half;

            float e = end * Mathf.Deg2Rad;
            var at = Polar(end, radius);
            var along = new Vector2(Mathf.Sin(e), -Mathf.Cos(e));   // the clockwise direction there
            var across = new Vector2(Mathf.Cos(e), Mathf.Sin(e));
            float length = 0.36f * s, wide = 0.18f * s;
            var tip = at + along * (length * 0.6f);
            var back = at - along * (length * 0.4f);
            float head = Triangle(q, tip, back + across * wide, back - across * wide);
            return Mathf.Min(stroke, head);
        }

        /// <summary>Signed distance to a triangle (Inigo Quilez's formulation).</summary>
        private static float Triangle(Vector2 p, Vector2 p0, Vector2 p1, Vector2 p2)
        {
            Vector2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
            Vector2 v0 = p - p0, v1 = p - p1, v2 = p - p2;
            Vector2 pq0 = v0 - e0 * Mathf.Clamp01(Vector2.Dot(v0, e0) / Vector2.Dot(e0, e0));
            Vector2 pq1 = v1 - e1 * Mathf.Clamp01(Vector2.Dot(v1, e1) / Vector2.Dot(e1, e1));
            Vector2 pq2 = v2 - e2 * Mathf.Clamp01(Vector2.Dot(v2, e2) / Vector2.Dot(e2, e2));
            float sign = Mathf.Sign(e0.x * e2.y - e0.y * e2.x);
            Vector2 d = Vector2.Min(Vector2.Min(
                new Vector2(Vector2.Dot(pq0, pq0), sign * (v0.x * e0.y - v0.y * e0.x)),
                new Vector2(Vector2.Dot(pq1, pq1), sign * (v1.x * e1.y - v1.y * e1.x))),
                new Vector2(Vector2.Dot(pq2, pq2), sign * (v2.x * e2.y - v2.y * e2.x)));
            return -Mathf.Sqrt(d.x) * Mathf.Sign(d.y);
        }
    }
}

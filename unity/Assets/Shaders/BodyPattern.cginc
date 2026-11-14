// Patterns for the player's clothes, shared by PatternedBody and SeatedBodyFade.
//
// A pattern is drawn inside the cloth (Color_Primary) or trim (Color_Secondary) region of the game's color masks, in
// place of the flat color there. The game's UVs cannot carry a pattern: each face samples one cell of a palette sheet,
// so the pattern is worked out from POSITION instead.
//
// THE POSITION IS THE REST POSE. _PatSpace is set per renderer every frame by the mod (BodyPatterns.cs): it takes a
// world position, goes into the space of the bone the part hangs on, and from there to where that point sat with the
// character standing at rest, measured in body heights (feet near 0, head near 1) around the body's center line. So a
// stripe stays on the sleeve as the arm swings instead of the sleeve sliding through the stripes. A part that bends in
// the middle (a leg at the knee) follows its upper bone, so its lower half drifts a little. The character's meshes are
// not readable on the CPU, which rules out baking rest positions into the mesh.
//
// _PatA and _PatB are (type, size from 0 small to 1 large, unused, unused) for cloth and trim; _PatAColor and _PatBColor the pattern's color.
// _PatRange.xy is the rest height range of the part, for Fade and Quartered, and _PatRange.w is 1 once the mod has
// placed this renderer; until then (w stays at the material's 0) no pattern is drawn, rather than one at a meaningless position. Types: 0 none, 1 stripes,
// 2 pinstripes, 3 checks, 4 plaid, 5 fade, 6 breton, 7 diagonal, 8 chevron, 9 diamonds, 10 argyle, 11 gingham,
// 12 windowpane, 13 waves, 14 dots, 15 halves, 16 quartered. The mod's PlayerAppearance.PatternKeys table must stay in
// this order.
//
// Around the body, u runs over roughly 0.55 body heights at the chest, so a count n around and m up give square cells
// when n is about 0.55 m. Every count around is a whole number, so the pattern meets itself down the back.

#ifndef SAILWIND_BODY_PATTERN
#define SAILWIND_BODY_PATTERN

float4x4 _PatSpace;
float4 _PatA, _PatB, _PatRange;
fixed4 _PatAColor, _PatBColor;

// Bands from a triangle wave, softened by how fast x changes on screen so distant stripes blur instead of shimmering.
// duty is the share of each period that is pattern.
// fw is how fast x changes on screen.
float PatBandW(float x, float duty, float fw)
{
    float tri = abs(frac(x) - 0.5) * 2.0;           // 1 at the band center, 0 between bands, no jump at the wrap
    float w = max(fw * 2.0, 0.0001);
    return smoothstep(1.0 - duty - w, 1.0 - duty + w, tri);
}

float PatBand(float x, float duty) { return PatBandW(x, duty, fwidth(x)); }

// How much of the pattern color shows at rest position p (0..1).
float PatAmount(float type, float size, float3 p, float4 range)
{
    if (type < 0.5) return 0;
    // Size 0 is small (1.5 times the base count), 1 large (0.6 times), geometric between so each step looks alike.
    float k = 1.5 * pow(0.4, saturate(size));
    // Around the body's center line, 0..1. Used for lines that run down the body. u wraps from 1 to 0 down the middle
    // of the back, so its screen rate is taken from whichever of u and a copy wrapping at the front is smooth here
    // (fwidth across the wrap would draw a smeared line down the back), and every count around the body is a whole
    // number, so the bands meet at the wrap.
    float u = atan2(p.x, p.z) * 0.15915494 + 0.5;
    float du = min(fwidth(u), fwidth(frac(u + 0.5)));

    if (type < 1.5) return PatBand(p.y * 14.0 * k, 0.5);                     // stripes
    if (type < 2.5) { float n = round(40.0 * k); return PatBandW(u * n, 0.18, du * n); }   // pinstripes
    if (type < 3.5)                                                           // checks
    {
        float a = PatBand(p.y * 7.0 * k, 0.5);
        float n = round(10.0 * k);
        float b = PatBandW(u * n, 0.5, du * n);
        return abs(a - b);
    }
    if (type < 4.5)                                                           // plaid
    {
        float h = PatBand(p.y * 5.0 * k, 0.35);
        float n = round(7.0 * k);
        float v = PatBandW(u * n, 0.35, du * n);
        float thin = max(PatBand(p.y * 5.0 * k + 0.5, 0.06), PatBandW(u * n + 0.5, 0.06, du * n));
        return saturate(0.5 * h + 0.5 * v + 0.8 * thin);
    }
    if (type < 5.5)
    {
        // fade: the part's top keeps its color, the pattern color comes in toward its bottom; size moves where it starts
        float t = saturate((range.y - p.y) / max(range.y - range.x, 0.01));
        float start = lerp(0.55, 0.05, saturate(size));
        return smoothstep(start, 1.0, t);
    }
    if (type < 6.5) return PatBand(p.y * 16.0 * k, 0.28);                     // breton: narrow bands, wide gaps
    if (type < 7.5)                                                           // diagonal
    {
        float n = round(8.0 * k), m = 14.0 * k;
        return PatBandW(u * n + p.y * m, 0.5, du * n + fwidth(p.y * m));
    }
    if (type < 8.5)                                                           // chevron: bands bent into zigzags
    {
        float n = round(10.0 * k), m = 12.0 * k;
        float zig = abs(frac(u * n) - 0.5) * 2.0;
        return PatBandW(p.y * m + 0.6 * zig, 0.5, fwidth(p.y * m) + du * n * 1.2);
    }
    if (type < 10.5)                                                          // diamonds, and argyle
    {
        float n = round(6.0 * k), m = 7.0 * k;
        float fw = du * n + fwidth(p.y * m);
        float da = u * n + p.y * m, db = u * n - p.y * m;
        float d = abs(PatBandW(da, 0.5, fw) - PatBandW(db, 0.5, fw));
        if (type < 9.5) return d;
        // argyle: the diamonds at half strength, crossed by thin full-strength lines through their middles
        float lines = max(PatBandW(da + 0.5, 0.07, fw), PatBandW(db + 0.5, 0.07, fw));
        return max(0.55 * d, lines);
    }
    if (type < 11.5)                                                          // gingham: overlaps darker
    {
        float n = round(14.0 * k), m = 10.0 * k;
        return 0.5 * PatBand(p.y * m, 0.5) + 0.5 * PatBandW(u * n, 0.5, du * n);
    }
    if (type < 12.5)                                                          // windowpane: a thin grid
    {
        float n = round(7.0 * k), m = 5.0 * k;
        return max(PatBand(p.y * m, 0.06), PatBandW(u * n, 0.06, du * n));
    }
    if (type < 13.5)                                                          // waves
    {
        float n = round(6.0 * k), m = 12.0 * k;
        float wave = 0.3 * sin(u * n * 6.2831853);
        return PatBandW(p.y * m + wave, 0.45, fwidth(p.y * m) + du * n * 6.2831853 * 0.3);
    }
    if (type < 14.5)                                                          // dots, every other row shifted half
    {
        float n = round(5.0 * k), m = 9.0 * k;
        float gy = p.y * m;
        float gx = u * n + 0.5 * fmod(abs(floor(gy)), 2.0);
        float d = length(float2(frac(gx) - 0.5, frac(gy) - 0.5));
        float w = max(max(du * n, fwidth(gy)), 0.0001);
        return 1.0 - smoothstep(0.3 - w, 0.3 + w, d);
    }
    // halves: one side of the body in each color. quartered: each garment's lower half the other way round, split at
    // the middle of its own height, so a shirt, a skirt and a hat each show all four quarters.
    float e = max(fwidth(p.x), 0.0001);
    float side = smoothstep(-e, e, p.x);
    if (type < 15.5) return side;
    float mid = 0.5 * (range.x + range.y), ey = max(fwidth(p.y), 0.0001);
    float low = 1.0 - smoothstep(mid - ey, mid + ey, p.y);
    return lerp(side, 1.0 - side, low);
}

// The cloth and trim colors at this world position, with their patterns.
void PatColors(float3 worldPos, fixed3 primary, fixed3 secondary, out fixed3 outPrimary, out fixed3 outSecondary)
{
    outPrimary = primary;
    outSecondary = secondary;
    if ((_PatA.x < 0.5 && _PatB.x < 0.5) || _PatRange.w < 0.5) return;
    float3 p = mul(_PatSpace, float4(worldPos, 1.0)).xyz;
    outPrimary = lerp(primary, _PatAColor.rgb, PatAmount(_PatA.x, _PatA.y, p, _PatRange));
    outSecondary = lerp(secondary, _PatBColor.rgb, PatAmount(_PatB.x, _PatB.y, p, _PatRange));
}

#endif

// Shared blade builder for PlayShift grass (included by the URP and Built-in shaders).
// One instance = one blade, read from _Blades. Its height and cut state come from the logical cell it
// belongs to (_Cells: x = grown height in metres, y = time the cell was cut, or -1 while grown).
//
//  * Cut animation: when a cell is cut, its blades shorten over _CutAnim seconds with a small kick away
//    from the cutting head. With _StubbleHeight 0 (default) they vanish and the green lawn ground shows
//    (never bare dirt); a positive _StubbleHeight leaves short stubble instead.
//  * Interaction: uncut blades near a cutting head lean away from it (more for tall grass and while the
//    blades spin). Roots never move: every blade is a constant-length arc rooted at its anchor.
//  * Wind: rolling gusts; taller grass sways more.
//  * Variety: height, width, lean and colour vary per blade (yellow-green, olive, dark green, fresh green,
//    dry golden patches).
#ifndef PLAYSHIFT_GRASS_BLADE_INCLUDED
#define PLAYSHIFT_GRASS_BLADE_INCLUDED

#define PLAYSHIFT_MAX_CUTTERS 4

StructuredBuffer<float4> _Blades;   // x, z (field-local metres), cell index, random seed
StructuredBuffer<float2> _Cells;    // grown height (m), cut time (s) or -1

float4x4 _FieldToWorld;
float _InstanceOffset;
float _LodWidthScale;
float4 _HeightClamp;                // x = min, y = max (metres)
float _GrassTime;                   // same clock the logic stamps cut times with
float _CutAnim;                     // seconds for a cut blade to shorten
float4 _Cutters[PLAYSHIFT_MAX_CUTTERS]; // xyz = head centre (world), w = radius
float _CutterCount;
float _Spin;                        // 0..1 blade speed

float4 _ColYellowGreen, _ColGoldenGreen, _ColDarkGreen, _ColFreshGreen, _ColDry;
float4 _StubbleColor, _StubbleTipColor;
float _DryAmount, _BladeWidth, _WidthGrowth, _StubbleHeight, _LeanAmount;
float4 _WindDirection;
float _WindStrength, _WindSpeed;

struct GrassBlade
{
    float3 positionWS;
    float3 normalWS;
    float3 color;
    float t;        // 0 at root, 1 at tip
    float cut;      // 0 grown .. 1 fully cut
};

float PsHash(float p) { p = frac(p * 0.1031); p *= p + 33.33; p *= p + p; return frac(p); }

float PsNoise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = PsHash(i.x * 7.1 + i.y * 113.3), b = PsHash(i.x * 7.1 + 7.1 + i.y * 113.3);
    float c = PsHash(i.x * 7.1 + (i.y + 1.0) * 113.3), d = PsHash(i.x * 7.1 + 7.1 + (i.y + 1.0) * 113.3);
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// uv.x = side (0 left, 1 right), uv.y = position along the blade (0 root, 1 tip).
GrassBlade BuildBlade(uint instanceID, float2 uv)
{
    GrassBlade o;
    uint id = instanceID + (uint)_InstanceOffset;
    float4 blade = _Blades[id];
    float2 cell = _Cells[(uint)blade.z];
    float s = blade.w * 1000.0;
    float r1 = PsHash(s + 1.7), r2 = PsHash(s + 11.3), r3 = PsHash(s + 23.9);
    float r4 = PsHash(s + 37.1), r5 = PsHash(s + 51.7), r6 = PsHash(s + 63.2);

    float3 root = mul(_FieldToWorld, float4(blade.x, 0.0, blade.y, 1.0)).xyz;
    float t = uv.y;
    float side = uv.x * 2.0 - 1.0;

    // Grown height: cell noise x per-blade variety, inside the level's range.
    float full = clamp(cell.x * (0.82 + 0.36 * r1), _HeightClamp.x, _HeightClamp.y);
    float cutAge = cell.y < 0.0 ? -1.0 : _GrassTime - cell.y;
    float k = cutAge < 0.0 ? 0.0 : saturate(cutAge / max(_CutAnim, 1e-3));
    float ease = 1.0 - pow(1.0 - k, 3.0);
    float stub = _StubbleHeight * (0.65 + 0.7 * r5);
    float h = lerp(full, stub, ease);

    float tall = smoothstep(0.12, 1.5, full) * (1.0 - ease);
    float facing = r2 * 6.2831853;
    float2 wdir = float2(cos(facing), sin(facing));
    float2 bdir = float2(-wdir.y, wdir.x);

    // Mostly upright so tall grass keeps its height; a few blades lean or flop.
    float arc = _LeanAmount * (0.08 + 0.22 * r3 * r3 + 0.12 * tall * r6);

    float2 windDir = normalize(_WindDirection.xz + 1e-5);
    float ph = dot(root.xz, windDir) * 0.9 - _GrassTime * _WindSpeed;
    float gust = 0.55 * sin(ph) + 0.3 * sin(ph * 2.7 + r4 * 6.28) + 0.15 * sin(_GrassTime * 7.0 + s);
    float windArc = _WindStrength * (0.3 + 0.9 * tall) * (0.6 + 0.4 * gust);

    // Lean away from every nearby cutting head; strongest for tall grass while the blades spin.
    float2 push = 0;
    float2 kickDir = bdir;
    float nearest = 1e6;
    [unroll] for (int c = 0; c < PLAYSHIFT_MAX_CUTTERS; c++)
    {
        float active = c < (int)_CutterCount ? 1.0 : 0.0;
        float2 away = root.xz - _Cutters[c].xz;
        float dist = length(away);
        float2 n = dist > 1e-3 ? away / dist : bdir;
        float infl = active * (1.0 - smoothstep(_Cutters[c].w * 0.7, _Cutters[c].w * 2.3, dist));
        push += n * infl * (0.35 + 0.55 * _Spin) * (0.6 + 0.9 * tall);
        if (active > 0.5 && dist < nearest) { nearest = dist; kickDir = n; }
    }
    push *= 1.0 - ease;

    float2 b = bdir * arc + windDir * windArc + push;
    // Struck blades kick outward for an instant, then settle as stubble.
    b += kickDir * sin(k * 3.14159) * 0.9 * step(0.0, cutAge) * step(cutAge, _CutAnim);

    float bl = length(b);
    float theta = min(bl, 1.45);
    float2 dir = bl > 1e-4 ? b / bl : bdir;
    float a = theta * t;
    float up = theta < 1e-3 ? h * t : h * sin(a) / theta;
    float along = theta < 1e-3 ? 0.0 : h * (1.0 - cos(a)) / theta;

    // Varied widths; taller levels have thicker blades. Tapers to a point.
    float w = _BladeWidth * (0.7 + 0.6 * r4)
            * (1.0 + _WidthGrowth * (sqrt(max(full, 0.05) / 0.2) - 1.0))
            * lerp(1.0, 1.35, ease) * _LodWidthScale;
    w *= 1.0 - pow(t, 1.6) * 0.92;
    // With no stubble (_StubbleHeight 0) a cut blade collapses completely: no sliver, no shadow, just lawn.
    w *= lerp(1.0, step(1e-4, _StubbleHeight), ease);

    float3 wax = float3(wdir.x, 0.0, wdir.y);
    o.positionWS = root + float3(dir.x * along, up, dir.y * along) + wax * side * w;
    float3 tng = float3(dir.x * sin(a), cos(a), dir.y * sin(a));
    float3 n0 = normalize(cross(wax, tng) + wax * side * 0.5);
    o.normalWS = normalize(lerp(n0, float3(0, 1, 0), 0.3));
    o.t = t;
    o.cut = ease;

    // Colour variety with dry golden patches.
    float dryPatch = PsNoise(root.xz * 0.35 + 3.1);
    // Mostly yellow-green and golden-green, with darker and fresh greens mixed through.
    float3 tip = r6 < 0.35 ? _ColYellowGreen.rgb : r6 < 0.6 ? _ColGoldenGreen.rgb : r6 < 0.78 ? _ColDarkGreen.rgb : _ColFreshGreen.rgb;
    tip = lerp(tip, _ColDry.rgb, saturate(_DryAmount * (smoothstep(0.66, 0.9, dryPatch) * (0.3 + 0.4 * r5) + 0.08 * step(0.95, r1))));
    float3 rootC = tip * float3(0.35, 0.42, 0.3);
    float3 grown = lerp(rootC, tip, smoothstep(0.0, 0.85, t)) * (0.85 + 0.3 * r2);
    float3 stubC = lerp(_StubbleColor.rgb, _StubbleTipColor.rgb, smoothstep(0.3, 1.0, t)) * (0.85 + 0.3 * r1);
    o.color = lerp(grown, stubC, ease) * lerp(0.55, 1.0, smoothstep(0.0, 0.7, t));
    return o;
}

// Back-lit translucency: light shining through blades towards the viewer.
float GrassTranslucency(float3 viewDirWS, float3 lightDirWS, float t, float cut)
{
    return pow(saturate(dot(-viewDirWS, lightDirWS)), 4.0) * t * (1.0 - cut);
}

#endif

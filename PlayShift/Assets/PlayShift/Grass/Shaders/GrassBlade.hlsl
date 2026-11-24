// Shared blade builder for PlayShift grass (included by the URP and Built-in shaders).
// One instance = one blade, generated procedurally per chunk (no blade buffer). Its height and cut state come from the logical cell it
// belongs to (_Cells: x = grown height in metres, y = time the cell was cut, or -1 while grown).
//
//  * Cut animation: when a cell is cut, its blades are sliced down over _CutAnim seconds with a small kick away
//    from the cutting head; with _StubbleHeight 0 (default) they vanish and the fresh-cut turf of the ground
//    shader shows (a positive _StubbleHeight leaves short stubble instead).
//    Each blade reads its cut state from a point jittered by up to ~half a cell (_CutEdgeJitter), so a cut path
//    has a ragged, natural edge instead of 10 cm stair steps (the cutting logic still uses the exact cells).
//  * Interaction: uncut blades near a cutting head lean away from it (more for tall grass and while the
//    blades spin). Roots never move: every blade is a constant-length arc rooted at its anchor.
//  * Wind: rolling gusts; taller grass sways more.
//  * Tufts: blades are pulled toward jittered tuft centres (_TuftSize grid) and fan out from them, so the long grass
//    grows in full, fluffy clumps; tips turn light golden (_TipGold).
//  * Variety: height, width, lean and colour vary per blade (yellow-green, olive, dark green, fresh green,
//    dry golden patches).
#ifndef PLAYSHIFT_GRASS_BLADE_INCLUDED
#define PLAYSHIFT_GRASS_BLADE_INCLUDED

#define PLAYSHIFT_MAX_CUTTERS 4

// Per cell: base grown height (m, without _HeightBonus), cut time (s) or -1. Tile-major: 32x32-cell tiles one
// after another, cells x-fastest inside a tile (see GrassField.CopyRenderTile).
StructuredBuffer<float2> _Cells;
float4 _CellGrid;                   // cellsX, cellsZ, 1/cellSizeX, 1/cellSizeZ
float _TilesX;
float _HeightBonus;                 // gift height bonus (m), one value for the whole field

// Per draw (chunk). Blade positions are generated here from (seed, instance index): no blade buffer exists.
float4 _ChunkRect;                  // x0, z0, sizeX, sizeZ (field-local metres)
float _ChunkSeed;

float4x4 _FieldToWorld;
float _LodWidthScale;
float4 _GrassFade;                  // global: x = blade fade start, y = blade draw end (m from camera); the ground
                                    // shader (GrassGround) shows grown grass beyond, from the same cell data
float4 _HeightClamp;                // x = min, y = max (metres)
float _GrassTime;                   // same clock the logic stamps cut times with
float _CutAnim;                     // seconds for a cut blade to shorten
float4 _Cutters[PLAYSHIFT_MAX_CUTTERS]; // xyz = head centre (world), w = radius
float _CutterCount;
float _Spin;                        // 0..1 blade speed

float4 _ColYellowGreen, _ColGoldenGreen, _ColDarkGreen, _ColFreshGreen, _ColDry;
float4 _StubbleColor, _StubbleTipColor;
float _DryAmount, _BladeWidth, _WidthGrowth, _StubbleHeight, _LeanAmount, _CutEdgeJitter;
float _TuftSize, _TuftPull, _TuftFan, _TipGold, _TipDroop;
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

// PCG integer hash (good distribution, cheap); 24-bit float in [0, 1).
uint PsPcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}
float PsU01(uint h) { return (h >> 8) * (1.0 / 16777216.0); }

// Blade i of the current chunk: uniform random position inside the chunk + per-blade seed. Any prefix of the
// sequence is a uniform subsample, which is what LOD relies on (draw the first N).
float4 PsProceduralBlade(uint i)
{
    uint h = PsPcg(i ^ PsPcg((uint)_ChunkSeed));
    uint hx = PsPcg(h), hz = PsPcg(hx), hs = PsPcg(hz);
    float2 p = _ChunkRect.xy + float2(PsU01(hx), PsU01(hz)) * _ChunkRect.zw;
    return float4(p.x, p.y, 0.0, PsU01(hs));
}

// Tiled cell lookup for a field-local position.
float2 PsCellAt(float2 p)
{
    uint cx = min((uint)max(p.x * _CellGrid.z, 0.0), (uint)_CellGrid.x - 1u);
    uint cz = min((uint)max(p.y * _CellGrid.w, 0.0), (uint)_CellGrid.y - 1u);
    uint tile = (cz >> 5) * (uint)_TilesX + (cx >> 5);
    return _Cells[tile * 1024u + (cz & 31u) * 32u + (cx & 31u)];
}

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
    float4 blade = PsProceduralBlade(instanceID);
    // Tufts: pull toward the jittered centre of this blade's tuft cell (stays inside the cell).
    float2 tufted = 0;
    if (_TuftSize > 0.0)
    {
        float2 tg = floor(blade.xy / _TuftSize);
        // Centre anywhere in the cell (a partly jittered grid lines up into visible rows in the distance).
        float2 tc = (tg + float2(PsHash(tg.x * 17.3 + tg.y * 311.7), PsHash(tg.x * 91.1 + tg.y * 47.9 + 5.3))) * _TuftSize;
        tufted = blade.xy - tc;
        blade.xy = tc + tufted * (1.0 - _TuftPull);
        tufted /= _TuftSize; // -1..1-ish offset from the tuft centre
    }
    float2 jit = float2(PsHash(blade.w * 1000.0 + 81.3), PsHash(blade.w * 1000.0 + 97.9)) - 0.5;
    float2 cell = PsCellAt(blade.xy + jit * _CutEdgeJitter / _CellGrid.zw);
    cell.x += _HeightBonus;
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
    // Distance fade: blades sink smoothly into the ground-shaded grass before the draw distance (no hard edge).
    if (_GrassFade.y > 0.0)
        h *= 1.0 - smoothstep(_GrassFade.x, _GrassFade.y, distance(root.xz, _WorldSpaceCameraPos.xz));

    float tall = smoothstep(0.12, 1.5, full) * (1.0 - ease);
    float facing = r2 * 6.2831853;
    float2 wdir = float2(cos(facing), sin(facing));
    float2 bdir = float2(-wdir.y, wdir.x);
    // Blades of a tuft fan outward from its centre (blade face across the bend, like a ribbon).
    float tl = length(tufted);
    if (tl > 1e-3)
    {
        bdir = normalize(lerp(bdir, tufted / tl, _TuftFan));
        wdir = float2(bdir.y, -bdir.x);
    }

    // Mostly upright so tall grass keeps its height; a few blades lean or flop.
    float arc = _LeanAmount * (0.08 + 0.22 * r3 * r3 + 0.12 * tall * r6 + 0.18 * _TuftFan * saturate(tl));

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
        // Only a slight lean right at the blade tips (it used to flatten grass up to 2.3x the radius, which looked
        // like trampled grass well outside the blades), and none while the head is lifted off the lawn.
        float lifted = saturate((_Cutters[c].y - root.y - 0.12) / 0.25);
        float infl = active * (1.0 - lifted) * (1.0 - smoothstep(_Cutters[c].w * 0.9, _Cutters[c].w * 1.35, dist));
        push += n * infl * (0.2 + 0.3 * _Spin) * (0.6 + 0.9 * tall);
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
    // Tips curl over a little more than the base (natural blades bend most near the tip).
    float droop = _TipDroop * (0.4 + 1.2 * r5) * t * t * t * h;
    along += droop;
    up -= droop * 0.35;

    // Varied widths; taller levels have thicker blades. Tapers to a point.
    float w = _BladeWidth * (0.55 + 0.9 * r4)
            * (1.0 + _WidthGrowth * (sqrt(max(full, 0.05) / 0.2) - 1.0))
            * lerp(1.0, 1.35, ease) * _LodWidthScale;
    w *= 1.0 - pow(t, 1.25) * 0.97; // natural taper to a fine (not needle-sharp) tip
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
    // Light golden tips over the green (the classic sunny long-grass look).
    grown = lerp(grown, lerp(tip, _ColGoldenGreen.rgb * 1.3, 0.6) * (0.9 + 0.2 * r3), smoothstep(0.6, 1.0, t) * _TipGold);
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

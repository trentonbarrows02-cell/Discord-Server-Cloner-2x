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
// Fine cut mask (global, GrassCutMask): 1 = looks cut. x = 1/sizeX, y = 1/sizeZ (field metres -> 0..1), z = on.
Texture2D _CutMask;
SamplerState sampler_CutMask;
float4 _CutMaskParams;

// Per draw (chunk). Blade positions are generated here from (seed, instance index): no blade buffer exists.
float4 _ChunkRect;                  // x0, z0, sizeX, sizeZ (field-local metres)
float _ChunkSeed;

float4x4 _FieldToWorld;
float _LodWidthScale;               // unused since per-blade LOD (kept so old materials/blocks stay valid)
// Detail falloff per blade (not per chunk, which showed as square bands): x = full-detail distance, y = mid distance,
// z = draw end; _GrassLodFrac x = blade fraction kept at mid distance, y = at the draw end. _ChunkCount = this chunk's
// blades at full detail (the draw may include more than a blade's own distance keeps; the rest collapse).
float4 _GrassLod;
float4 _GrassLodFrac;
float _ChunkCount;
// Far chunk groups: one draw covers S x S chunks; instance i is blade i / (S*S) of sub-chunk i % (S*S), exactly the
// blade that sub-chunk's own draw would make (same seed, rect, count), so nothing pops when a group switches between
// one draw and per-chunk draws. x = S (0 = a single chunk), yz = first chunk (kx, kz).
float4 _SuperChunk;
float4 _PsChunkGrid;                // chunk size (m), chunks x, chunks z, blade seed
float4 _PsChunkGrid2;               // field size x, z (m), blades per m2, level density
float _GrassDebug;                  // global, diagnostics only (GrassFieldRenderer.DebugView)
float4 _PsGrassAvg;                 // global: average blade colour (the ground's uncut colour uses the same)
float _GrassShowUncut;              // global: 1 on the title screen (every blade standing; saved cuts untouched)
float4 _GrassFade;                  // global: x = blade fade start, y = blade draw end (m from camera); the ground
                                    // shader (GrassGround) shows grown grass beyond, from the same cell data
float4 _HeightClamp;                // x = min, y = max (metres)
float _GrassTime;                   // same clock the logic stamps cut times with
float _CutAnim;                     // seconds for a cut blade to shorten
float4 _Cutters[PLAYSHIFT_MAX_CUTTERS]; // xyz = head centre (world), w = radius
float4 _CutterShape[PLAYSHIFT_MAX_CUTTERS]; // x = world height of the head's underside, yzw = shaft direction (unit)
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
float4 PsProceduralBladeIn(uint i, float seed, float4 rect)
{
    uint h = PsPcg(i ^ PsPcg((uint)seed));
    uint hx = PsPcg(h), hz = PsPcg(hx), hs = PsPcg(hz);
    float2 p = rect.xy + float2(PsU01(hx), PsU01(hz)) * rect.zw;
    return float4(p.x, p.y, 0.0, PsU01(hs));
}
float4 PsProceduralBlade(uint i) { return PsProceduralBladeIn(i, _ChunkSeed, _ChunkRect); }

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
    float4 rect = _ChunkRect;
    float seed = _ChunkSeed, chunkCount = _ChunkCount;
    uint idx = instanceID;
    if (_SuperChunk.x > 1.5)
    {
        uint S = (uint)_SuperChunk.x, n = S * S;
        uint j = instanceID % n;
        idx = instanceID / n;
        uint kx = (uint)_SuperChunk.y + j % S, kz = (uint)_SuperChunk.z + j / S;
        float cm = _PsChunkGrid.x;
        float x0 = kx * cm, z0 = kz * cm;
        rect = float4(x0, z0, min(_PsChunkGrid2.x, x0 + cm) - x0, min(_PsChunkGrid2.y, z0 + cm) - z0);
        uint ci = kz * (uint)_PsChunkGrid.y + kx;
        seed = (float)(((uint)_PsChunkGrid.w * 7919u + ci * 104729u) & 0xFFFFFFu);
        chunkCount = max(1.0, round(rect.z * rect.w * _PsChunkGrid2.z)) * _PsChunkGrid2.w;
        if (kx >= (uint)_PsChunkGrid.y || kz >= (uint)_PsChunkGrid.z) chunkCount = 0.0; // outside the field
    }
    float4 blade = PsProceduralBladeIn(idx, seed, rect);
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
    float3 root = mul(_FieldToWorld, float4(blade.x, 0.0, blade.y, 1.0)).xyz;

    // Per-blade detail: the fraction of blades kept falls off smoothly with this blade's own distance, and blade i of
    // the chunk's uniform sequence is kept while i / count is below it (shrinking away over the last 20%). Kept blades
    // widen to hold the same coverage. No chunk-shaped bands or squares.
    float lodF = 1.0, lodKeep = 1.0;
    float dl = distance(root, _WorldSpaceCameraPos);
    if (_SuperChunk.x > 1.5 && chunkCount < 0.5) lodKeep = 0.0;
    else if (chunkCount > 0.5 && _GrassLod.y > 0.0)
    {
        // Full detail to x; down to the mid fraction halfway to y; the far fraction from y on (same blade budget as
        // the old per-chunk steps, without the steps).
        // Beyond y the fraction keeps falling with distance squared (about the same number of blades per screen
        // area at any distance): sparse, wide "card" blades cover the far field up to the draw end.
        float half = 0.5 * (_GrassLod.x + _GrassLod.y);
        lodF = dl <= _GrassLod.x ? 1.0
             : dl <= half ? lerp(1.0, _GrassLodFrac.x, (dl - _GrassLod.x) / max(half - _GrassLod.x, 1e-3))
             : dl <= _GrassLod.y ? lerp(_GrassLodFrac.x, _GrassLodFrac.y, saturate((dl - half) / max(_GrassLod.y - half, 1e-3)))
             : max(0.004, _GrassLodFrac.y * (_GrassLod.y * _GrassLod.y) / (dl * dl));
        float rank = ((float)idx + 0.5) / chunkCount;
        lodKeep = saturate((lodF - rank) / (0.2 * lodF));
    }
    if (lodKeep <= 0.0)
    {
        // Not kept at this distance: a zero-area blade, skipping the rest of the work.
        o.positionWS = root; o.normalWS = float3(0, 1, 0); o.color = 0; o.t = uv.y; o.cut = 1.0;
        return o;
    }
    float2 jit = float2(PsHash(blade.w * 1000.0 + 81.3), PsHash(blade.w * 1000.0 + 97.9)) - 0.5;
    float2 cell = PsCellAt(blade.xy + jit * _CutEdgeJitter / _CellGrid.zw);
    if (_CutMaskParams.z > 0.5)
    {
        // Cut or standing comes from the fine mask at this blade's exact root, so cut paths are the real blade circle
        // (round and smooth). Height and the cut animation's start time still come from the cell.
        float2 cellExact = PsCellAt(blade.xy);
        float m = _CutMask.SampleLevel(sampler_CutMask, blade.xy * _CutMaskParams.xy, 0).r;
        if (m > 0.5) cell.y = cellExact.y >= 0.0 ? cellExact.y : _GrassTime - 10.0;
        else cell.y = -1.0;
    }
    if (_GrassShowUncut > 0.5) cell.y = -1.0;
    cell.x += _HeightBonus;
    float s = blade.w * 1000.0;
    float r1 = PsHash(s + 1.7), r2 = PsHash(s + 11.3), r3 = PsHash(s + 23.9);
    float r4 = PsHash(s + 37.1), r5 = PsHash(s + 51.7), r6 = PsHash(s + 63.2);

    float t = uv.y;
    float side = uv.x * 2.0 - 1.0;

    // Grown height: cell noise x per-blade variety, inside the level's range.
    float full = clamp(cell.x * (0.82 + 0.36 * r1), _HeightClamp.x, _HeightClamp.y);
    float cutAge = cell.y < 0.0 ? -1.0 : _GrassTime - cell.y;
    float k = cutAge < 0.0 ? 0.0 : saturate(cutAge / max(_CutAnim, 1e-3));
    float ease = 1.0 - pow(1.0 - k, 3.0);
    float stub = _StubbleHeight * (0.65 + 0.7 * r5);
    float h = lerp(full, stub, ease) * lodKeep;
    // Distance fade: blades sink smoothly into the ground (which has their average colour) before the draw distance.
    // 3D distance, the same the renderer culls whole chunks by: with the flat distance, an elevated camera culled
    // chunks before their blades had faded, which left a hard, chunk-shaped edge.
    if (_GrassFade.y > 0.0)
        h *= 1.0 - smoothstep(_GrassFade.x, _GrassFade.y, dl);

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

    // Solid tool: grass never passes through the cutting head or the shaft. Blades under the blade disc (out to the tips
    // plus a narrow edge) are pressed down below its underside and lean outward; blades along the low end of the shaft
    // stay below it. Per blade in the vertex shader (shadows and depth included), stateless: the grass stands back up
    // as soon as the head moves on, and nothing is counted as cut. A lifted head clears the grass and presses nothing.
    float hCap = 1e4;
    [unroll] for (int c2 = 0; c2 < PLAYSHIFT_MAX_CUTTERS; c2++)
    {
        if (c2 >= (int)_CutterCount || _CutterShape[c2].x < -999.0) continue;
        float2 away2 = root.xz - _Cutters[c2].xz;
        float d2 = length(away2);
        float under = 1.0 - smoothstep(_Cutters[c2].w, _Cutters[c2].w + 0.08, d2);
        float below = max(0.0, _CutterShape[c2].x - root.y - 0.008);
        if (under > 0.0)
        {
            hCap = min(hCap, lerp(1e4, below, under));
            push += (d2 > 1e-3 ? away2 / d2 : bdir) * under * 0.9;
        }
        float3 sd = _CutterShape[c2].yzw;
        float lh = length(sd.xz);
        if (lh > 1e-3)
        {
            float2 u = sd.xz / lh;
            float along = dot(away2, u);
            if (along > 0.0 && along < 1.6)
            {
                float perp = abs(dot(away2, float2(-u.y, u.x)));
                float near = 1.0 - smoothstep(0.04, 0.1, perp);
                float shaftY = _Cutters[c2].y + along * sd.y / lh - root.y - 0.045; // shaft underside above the root
                if (near > 0.0) hCap = min(hCap, lerp(1e4, max(0.0, shaftY), near));
            }
        }
    }

    float2 b = bdir * arc + windDir * windArc + push;
    // Struck blades kick outward for an instant, then settle as stubble.
    b += kickDir * sin(k * 3.14159) * 0.9 * step(0.0, cutAge) * step(cutAge, _CutAnim);

    // Pressed blades: no taller than the tool above them allows (the lean above bends them outward).
    h = min(h, hCap);
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
            * lerp(1.0, 1.35, ease) * min(8.0, rsqrt(lodF));
    w *= 1.0 - pow(t, 1.25) * 0.97; // natural taper to a fine (not needle-sharp) tip
    // With no stubble (_StubbleHeight 0) a cut blade collapses completely: no sliver, no shadow, just lawn.
    w *= lerp(1.0, step(1e-4, _StubbleHeight), ease);
    w *= step(1e-4, lodKeep);

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
    float3 rootC = tip * float3(0.56, 0.63, 0.47); // not near-black: the base of a lawn is green, not a dark hole
    float3 grown = lerp(rootC, tip, smoothstep(0.0, 0.85, t)) * (0.85 + 0.3 * r2);
    // Light golden tips over the green (the classic sunny long-grass look).
    grown = lerp(grown, lerp(tip, _ColGoldenGreen.rgb * 1.3, 0.6) * (0.9 + 0.2 * r3), smoothstep(0.6, 1.0, t) * _TipGold);
    float3 stubC = lerp(_StubbleColor.rgb, _StubbleTipColor.rgb, smoothstep(0.3, 1.0, t)) * (0.85 + 0.3 * r1);
    o.color = lerp(grown, stubC, ease) * lerp(0.74, 1.0, smoothstep(0.0, 0.7, t));
    // Far blades drift toward the field's average colour: less per-blade contrast where blades are a pixel or two
    // wide (no sparkle), and a seamless hand-over to the ground beyond the draw distance.
    if (_GrassFade.y > 0.0 && _GrassLod.x > 0.0)
    {
        float farMix = smoothstep(_GrassLod.x, _GrassFade.y, dl) * 0.8;
        o.color = lerp(o.color, _PsGrassAvg.rgb * lerp(0.8, 1.05, t), farMix * (1.0 - ease));
    }
    if (_GrassDebug > 1.5)
    {
        // Diagnostics: red = full detail, green = mid, blue = far (continuous).
        float t1 = saturate((lodF - _GrassLodFrac.y) / max(_GrassLodFrac.x - _GrassLodFrac.y, 1e-3));
        float t2 = saturate((lodF - _GrassLodFrac.x) / max(1.0 - _GrassLodFrac.x, 1e-3));
        o.color = lerp(lerp(float3(0.15, 0.3, 0.95), float3(0.15, 0.8, 0.15), t1), float3(0.9, 0.15, 0.1), t2);
    }
    return o;
}

// Back-lit translucency: light shining through blades towards the viewer.
float GrassTranslucency(float3 viewDirWS, float3 lightDirWS, float t, float cut)
{
    return pow(saturate(dot(-viewDirWS, lightDirWS)), 4.0) * t * (1.0 - cut);
}

#endif

// Ground under and beyond the grass blades (Built-in render pipeline). Reads the same per-cell buffer the blades
// and the cutting logic use (set globally by GrassFieldRenderer), so:
//  - cut cells always show short bright lawn (the lawn texture), near or far;
//  - grown cells near the camera show the shadowed ground between blades, and beyond the blade draw distance they
//    take on the grass colours, so the whole field (and the title screen's aerial view) shows real progress without
//    drawing millions of distant blades.
// Receives real-time shadows, fog. Outside the lawn it is plain lawn texture.
Shader "PlayShift/GrassGround"
{
    Properties
    {
        _MainTex ("Lawn texture", 2D) = "white" {}
        _Tiling ("Lawn texture tiles per metre", Float) = 0.66
        _UnderColor ("Ground under tall grass (multiplies lawn)", Color) = (0.62, 0.72, 0.44, 1)
        _FarYellow ("Far grass: yellow-green", Color) = (0.56, 0.66, 0.2, 1)
        _FarGolden ("Far grass: golden", Color) = (0.66, 0.62, 0.24, 1)
        _FarDark ("Far grass: dark", Color) = (0.28, 0.42, 0.11, 1)
        _FarStart ("Far colour starts at (x blade fade start)", Range(0, 1)) = 0.55
        _CutTint ("Fresh-cut turf tint (multiplies lawn)", Color) = (0.9, 0.98, 0.84, 1)
        _CutDetail ("Fresh-cut turf: short-grass grain", Range(0, 1)) = 0.6
        _CutColor ("Fresh-cut lawn colour (light mown green; its own colour, not derived from the uncut grass)", Color) = (0.4, 0.6, 0.22, 1)
        _CutBright ("Fresh-cut turf: brightness", Range(0.6, 1.6)) = 1
        _CutSaturation ("Fresh-cut turf: saturation", Range(0, 1.5)) = 1
        _CutFine ("Fresh-cut turf: fine detail mix", Range(0, 1)) = 0.5
        _EdgeShade ("Shade on the cut lawn along the uncut grass", Range(0, 1)) = 0.1
        _UnderShade ("Uncut grass: shade under the blades (near)", Range(0.4, 1)) = 0.8
        _GrassMatch ("Uncut grass: brightness match to the blades", Range(0.5, 2)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }

            CGPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"
            #include "GrassBlade.hlsl" // _Cells, _CellGrid, _TilesX, _GrassTime, _CutAnim, _GrassFade, PsCellAt, PsNoise

            sampler2D _MainTex;
            float _Tiling;
            float4 _UnderColor, _FarYellow, _FarGolden, _FarDark, _CutTint, _CutColor;
            float _FarStart, _CutFine, _EdgeShade, _CutDetail, _UnderShade, _GrassMatch, _CutBright, _CutSaturation;
            float4 _PsGrassDry;       // global: rgb = dry colour, a = dry amount
            sampler2D _PsGrassTop;    // global: realtime-grass top texture (when the cards are in use)
            float _PsGrassTopOn;
            float4 _PsLodDist;        // global: full, mid, max draw, fade start (m)
            float4 _PsChunk;          // global: chunk size (m), tallest blade (m)
            float4x4 _WorldToField;   // global
            float4 _FieldSize;        // global: x, z (m)

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float4 shadowCoord : TEXCOORD1;
                UNITY_FOG_COORDS(2)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityWorldToClipPos(o.worldPos);
                o.shadowCoord = ComputeScreenPos(o.pos);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 lawn = tex2D(_MainTex, i.worldPos.xz * _Tiling).rgb;
                // Fresh-cut turf: the lawn texture mixed with a finer copy of itself (dense short turf), brighter
                // and more saturated.
                float3 fine = tex2D(_MainTex, i.worldPos.zx * (_Tiling * 3.7) + 0.37).rgb;
                float3 turf = lerp(lawn, fine, _CutFine) * _CutTint.rgb;
                // Normalise the lawn texture to the grass palette: the mown lawn is the field's own green, a little
                // lighter, at every distance (the texture's own colour used to be darker than the uncut field far away).
                float3 lawnAvg = max(tex2Dlod(_MainTex, float4(0.5, 0.5, 0, 12)).rgb, 0.02);
                // Its own light mown-lawn colour, so the cut lawn stays clearly lighter than the uncut grass (and the
                // ground under it) at every distance and on every preset; the texture only adds grain and variation.
                turf = turf / lawnAvg * _CutColor.rgb * _CutBright;
                // A natural freshly mown lawn: a little less saturated than the standing grass.
                turf = lerp(dot(turf, float3(0.299, 0.587, 0.114)).xxx, turf, _CutSaturation);
                // Short-grass grain: two fine noise layers (~3 cm and ~1 cm), faded out with distance so it never
                // shimmers far away. Gives the mown lawn a real turf texture instead of a flat sheet.
                {
                    float dcam = distance(_WorldSpaceCameraPos.xz, i.worldPos.xz);
                    float k = _CutDetail * (1.0 - smoothstep(6.0, 22.0, dcam));
                    if (k > 0.001) // near only (skipped for most of the screen)
                    {
                        float g1 = PsNoise(i.worldPos.xz * 33.0), g2 = PsNoise(i.worldPos.xz * 97.0 + 11.3);
                        float grain = g1 * 0.55 + g2 * 0.45;
                        turf *= lerp(1.0, 0.72 + 0.5 * grain, k);
                    }
                    // Broad, slow tone variation (a few metres across) so the lawn texture never repeats visibly:
                    // slightly lighter/yellower and darker/bluer patches, like a real mown lawn.
                    float broad = PsNoise(i.worldPos.xz * 0.11 + 7.7) * 0.6 + PsNoise(i.worldPos.xz * 0.37 - 2.1) * 0.4;
                    turf *= lerp(float3(0.9, 0.93, 0.97), float3(1.07, 1.06, 0.95), broad);
                }
                float3 col = turf;

                float2 f = mul(_WorldToField, float4(i.worldPos, 1.0)).xz;
                // Derivatives outside any branch: mask sampling and anti-aliasing use them.
                float2 maskUv = f * _CutMaskParams.xy;
                float2 maskDx = ddx(maskUv), maskDy = ddy(maskUv);
                float fw = length(fwidth(f));                 // metres per pixel here
                float d3 = distance(_WorldSpaceCameraPos, i.worldPos);
                if (f.x >= 0.0 && f.y >= 0.0 && f.x < _FieldSize.x && f.y < _FieldSize.y && _CellGrid.x > 0.0)
                {
                    // How cut this spot is: bilinear over the 4 nearest cell centres, then a noisy threshold, so
                    // the mown lawn meets the long grass along a soft, ragged line (no 10 cm squares).
                    float2 g = f * _CellGrid.zw - 0.5;
                    float2 g0 = floor(g), gf = g - g0;
                    float2 cs = 1.0 / _CellGrid.zw;
                    float4 cy = float4(PsCellAt((g0 + float2(0.5, 0.5)) * cs).y, PsCellAt((g0 + float2(1.5, 0.5)) * cs).y,
                                       PsCellAt((g0 + float2(0.5, 1.5)) * cs).y, PsCellAt((g0 + float2(1.5, 1.5)) * cs).y);
                    float4 isCut = step(0.0, cy) * step(_CutAnim, _GrassTime - cy);
                    float cutAmt = lerp(lerp(isCut.x, isCut.y, gf.x), lerp(isCut.z, isCut.w, gf.x), gf.y);
                    if (_GrassDebug > 2.5)
                    {
                        // Diagnostics: detail zones by 3D distance, chunk edges, and where whole chunks stop being drawn
                        // (the renderer culls a chunk when its nearest point is beyond the draw distance).
                        float d3 = distance(_WorldSpaceCameraPos, i.worldPos);
                        float3 z = d3 < _PsLodDist.x ? float3(0.85, 0.2, 0.15) : d3 < _PsLodDist.y ? float3(0.2, 0.75, 0.2)
                                 : d3 < _PsLodDist.w ? float3(0.2, 0.35, 0.9) : d3 < _PsLodDist.z ? float3(0.95, 0.55, 0.1) : float3(0.35, 0.35, 0.35);
                        float2 cm = f / _PsChunk.x;
                        float2 cf = abs(frac(cm) - 0.5);
                        float grid = step(0.47, max(cf.x, cf.y));
                        float2 c0 = floor(cm) * _PsChunk.x, c1 = c0 + _PsChunk.x;
                        float3 camF = mul(_WorldToField, float4(_WorldSpaceCameraPos, 1.0)).xyz;
                        float2 nearest = clamp(camF.xz, c0 - _PsChunk.y, c1 + _PsChunk.y);
                        float dChunk = length(float3(nearest.x - camF.x, max(0.0, camF.y - _PsChunk.y * 2.0), nearest.y - camF.z));
                        float culled = step(_PsLodDist.z, dChunk);
                        z = lerp(z, z * 0.45, culled);
                        z = lerp(z, float3(1, 1, 1), grid * 0.35);
                        return fixed4(z, 1);
                    }
                    if (_GrassDebug > 0.5 && _GrassDebug < 1.5)
                    {
                        // Diagnostics: R = the exact 10 cm cell is cut, G/B = the fine mask. White = both, red = cell
                        // only, cyan = mask only, black = grown.
                        float2 ce = PsCellAt(f);
                        float cellCut = ce.y >= 0.0 ? 1.0 : 0.0;
                        float mk = _CutMaskParams.z > 0.5 ? _CutMask.SampleLevel(sampler_CutMask, f * _CutMaskParams.xy, 0).r : cellCut;
                        return fixed4(cellCut * 0.85 + 0.1, mk * 0.85 + 0.1, mk * 0.85 + 0.1, 1);
                    }
                    // Mipmapped fine mask: smooth, round cut edges at any distance (no stair steps or shimmer far away).
                    if (_CutMaskParams.z > 0.5) cutAmt = _CutMask.SampleGrad(sampler_CutMask, maskUv, maskDx, maskDy).r;
                    if (_GrassShowUncut > 0.5) cutAmt = 0.0; // title screen: an untouched lawn
                    // Gently wavy edge near the camera only (far away it would alias), anti-aliased over the pixel size.
                    float nearK = 1.0 - smoothstep(0.03, 0.12, fw);
                    float wave = 0.0;
                    if (nearK > 0.001) wave = ((PsNoise(f * 5.0) - 0.5) * 0.35 + (PsNoise(f * 17.0) - 0.5) * 0.12) * (_CutMaskParams.z > 0.5 ? 0.25 : 1.0) * nearK;
                    float aa = max(0.15, fwidth(cutAmt) * 1.5);
                    float edge = smoothstep(0.5 - aa, 0.5 + aa, cutAmt + wave);
                    // The uncut grass wall shades the cut lawn right next to it (near only: far away it read as dark lines).
                    col *= 1.0 - _EdgeShade * edge * (1.0 - smoothstep(0.55, 1.0, cutAmt + wave * 0.5)) * (1.0 - smoothstep(6.0, 14.0, d3));
                    // Uncut grass: the blades' own average colour (same palette, same dry patches as the blades), so the
                    // blades and the ground under and beyond them read as one field at every distance: no flat band.
                    if (edge < 0.999) // fully cut pixels skip the uncut-grass work
                    {
                        float dry = smoothstep(0.66, 0.9, PsNoise(i.worldPos.xz * 0.35 + 3.1)) * 0.5 * _PsGrassDry.a;
                        float3 grass = lerp(_PsGrassAvg.rgb, _PsGrassDry.rgb, dry);
                        grass *= lerp(0.92, 1.07, PsNoise(f * 0.21 + 1.3) * 0.6 + PsNoise(f * 0.9 - 4.7) * 0.4);
                        // Blade grain, anti-aliased: each octave fades out once a pixel is bigger than its features (and
                        // isn't computed at all there).
                        float k1 = 1.0 - smoothstep(0.02, 0.07, fw), k2 = 1.0 - smoothstep(0.008, 0.025, fw);
                        if (k1 > 0.001) grass *= 1.0 + (PsNoise(f * float2(11.0, 14.0)) - 0.5) * 0.24 * k1;
                        if (k2 > 0.001) grass *= 1.0 + (PsNoise(f * float2(37.0, 31.0) + 5.1) - 0.5) * 0.2 * k2;
                        // Under standing blades a little shade (they cover it); none where the blades have faded out.
                        float bladesHere = _GrassFade.y > 0.0 ? 1.0 - smoothstep(_GrassFade.x, _GrassFade.y, d3) : 0.0;
                        grass *= lerp(1.0, _UnderShade, bladesHere);
                        if (_PsGrassTopOn > 0.5)
                        {
                            // The realtime-grass ground texture's light and dark (its own olive hue left out, so
                            // the colour stays the game's palette).
                            float3 top = tex2D(_PsGrassTop, i.worldPos.xz / 1.6).rgb;
                            grass *= saturate(dot(top, float3(0.3, 0.59, 0.11)) / 0.22) * 0.95 + 0.1;
                        }
                        col = lerp(grass * _GrassMatch, col, edge);
                    }
                }

                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float shadow = 1.0;
                #if defined(SHADOWS_SCREEN)
                shadow = unitySampleShadow(i.shadowCoord);
                #endif
                float ndl = saturate(l.y);
                float3 lit = col * (ShadeSH9(float4(0, 1, 0, 1)) + _LightColor0.rgb * shadow * ndl);
                UNITY_APPLY_FOG(i.fogCoord, lit);
                return fixed4(lit, 1);
            }
            ENDCG
        }
    }
    // ShadowCaster from VertexLit, so the ground is in the depth texture and receives screen-space shadows.
    Fallback "VertexLit"
}

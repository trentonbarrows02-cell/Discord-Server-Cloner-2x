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
        _UnderColor ("Ground under tall grass (multiplies lawn)", Color) = (0.55, 0.62, 0.38, 1)
        _FarYellow ("Far grass: yellow-green", Color) = (0.56, 0.66, 0.2, 1)
        _FarGolden ("Far grass: golden", Color) = (0.66, 0.62, 0.24, 1)
        _FarDark ("Far grass: dark", Color) = (0.28, 0.42, 0.11, 1)
        _FarStart ("Far colour starts at (x blade fade start)", Range(0, 1)) = 0.55
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
            float4 _UnderColor, _FarYellow, _FarGolden, _FarDark;
            float _FarStart;
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
                float3 col = lawn;

                float2 f = mul(_WorldToField, float4(i.worldPos, 1.0)).xz;
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
                    float edge = smoothstep(0.3, 0.7, cutAmt + (PsNoise(f * 23.0) - 0.5) * 0.45);
                    if (edge < 0.999)
                    {
                        float d = distance(_WorldSpaceCameraPos.xz, i.worldPos.xz);
                        float far = _GrassFade.y > 0.0 ? smoothstep(_GrassFade.x * _FarStart, _GrassFade.y, d) : 1.0;
                        float n1 = PsNoise(f * 0.35 + 3.1), n2 = PsNoise(f * 1.7 - 7.3);
                        float3 grass = lerp(lerp(_FarYellow.rgb, _FarGolden.rgb, smoothstep(0.55, 0.9, n1)), _FarDark.rgb, n2 * 0.45);
                        col = lerp(lerp(lawn * _UnderColor.rgb, grass, far), lawn, edge);
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

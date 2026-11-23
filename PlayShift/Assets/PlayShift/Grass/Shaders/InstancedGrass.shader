// PlayShift procedural grass.
// Each instance = one blade read from _Blades; its height comes from the logical cell it belongs to
// (_CellHeights, metres, 0 = cut). The blade is built as a constant-curvature arc so it keeps its length
// while bending: short grass stands up, tall grass arcs and droops instead of standing as a rigid spike.
// Works with the Built-in pipeline and URP (the pass has no LightMode tag, so URP draws it as SRPDefaultUnlit).
Shader "PlayShift/InstancedGrass"
{
    Properties
    {
        [Header(Colour)]
        _BaseColor ("Root Colour", Color) = (0.10, 0.24, 0.05, 1)
        _TipColor ("Tip Colour", Color) = (0.46, 0.68, 0.20, 1)
        _DryColor ("Dry Tint", Color) = (0.62, 0.62, 0.28, 1)
        _DryAmount ("Dry Tint Amount", Range(0, 1)) = 0.25
        _StubbleColor ("Cut Stubble Colour", Color) = (0.42, 0.52, 0.18, 1)
        _RootAO ("Root Darkening", Range(0, 1)) = 0.35
        _Translucency ("Back-light Translucency", Range(0, 2)) = 0.6

        [Header(Shape)]
        _BladeWidth ("Blade Width at 20cm (m)", Float) = 0.011
        _WidthGrowth ("Width Growth With Height", Range(0, 1)) = 0.45
        _HeightJitter ("Per-blade Height Jitter", Range(0, 0.4)) = 0.12
        _BendShort ("Arc Angle, short grass (rad)", Range(0, 1.5)) = 0.25
        _BendTall ("Arc Angle, 1.5m grass (rad)", Range(0, 2.2)) = 1.25
        _MaxArc ("Max Arc Angle (rad)", Range(0.5, 2.5)) = 1.6
        _StubbleHeight ("Cut Stubble Height (m)", Float) = 0.025

        [Header(Wind)]
        _WindDirection ("Wind Direction (XZ)", Vector) = (1, 0, 0.35, 0)
        _WindStrength ("Wind Strength (rad)", Range(0, 1.5)) = 0.35
        _WindSpeed ("Wind Speed", Float) = 1.6
        _WindFrequency ("Wind Wave Frequency", Float) = 0.18
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "IgnoreProjector" = "True" }

        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma exclude_renderers gles
            #include "UnityCG.cginc"

            StructuredBuffer<float4> _Blades;      // x, z (field-local metres), cell index, random seed
            StructuredBuffer<float> _CellHeights;  // metres; 0 = cut

            float4x4 _FieldToWorld;
            float _InstanceOffset;
            float _LodWidthScale;
            float4 _HeightClamp;      // x = min, y = max (metres) for the current level incl. gift bonus
            float4 _GrassLightDir;    // towards the light, world space
            float4 _GrassLightColor;
            float4 _GrassAmbient;

            float4 _BaseColor, _TipColor, _DryColor, _StubbleColor;
            float _DryAmount, _RootAO, _Translucency;
            float _BladeWidth, _WidthGrowth, _HeightJitter, _BendShort, _BendTall, _MaxArc, _StubbleHeight;
            float4 _WindDirection;
            float _WindStrength, _WindSpeed, _WindFrequency;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 color : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                float t : TEXCOORD3;
            };

            float hash11(float p)
            {
                p = frac(p * 0.1031);
                p *= p + 33.33;
                p *= p + p;
                return frac(p);
            }

            v2f vert(appdata v)
            {
                v2f o;
                uint id = v.instanceID + (uint)_InstanceOffset;
                float4 blade = _Blades[id];
                float cellH = _CellHeights[(uint)blade.z];
                float seed = blade.w * 1000.0;
                float r1 = hash11(seed + 1.7);
                float r2 = hash11(seed + 11.3);
                float r3 = hash11(seed + 23.9);
                float r4 = hash11(seed + 37.1);

                bool cut = cellH <= 0.0;
                float h = cut
                    ? _StubbleHeight * (0.6 + 0.8 * r1)
                    : clamp(cellH * (1.0 + (r1 * 2.0 - 1.0) * _HeightJitter), _HeightClamp.x, _HeightClamp.y);

                float t = v.uv.y;
                float side = v.uv.x * 2.0 - 1.0;

                float3 rootWS = mul(_FieldToWorld, float4(blade.x, 0.0, blade.y, 1.0)).xyz;

                // Blade faces a random direction; it bends along its flat normal like real grass.
                float facing = r2 * 6.2831853;
                float2 widthDir = float2(cos(facing), sin(facing));
                float2 bendDir = float2(-widthDir.y, widthDir.x);

                // 0 at ~15cm, 1 at ~150cm. Taller grass carries more weight, so it arcs more.
                float tall = saturate((h - 0.15) / 1.35);
                float arc = lerp(_BendShort, _BendTall, tall * tall * (3.0 - 2.0 * tall)) * (0.55 + 0.9 * r3);

                // Rolling wind waves across the lawn; tall grass sways more.
                float2 windDir = normalize(_WindDirection.xz + 1e-5);
                float phase = dot(rootWS.xz, windDir) * _WindFrequency * 6.2831853 - _Time.y * _WindSpeed;
                float gust = 0.6 * sin(phase) + 0.4 * sin(phase * 2.3 + r4 * 6.2831853);
                float windArc = _WindStrength * (0.25 + 0.75 * tall) * (0.65 + 0.35 * gust) * (cut ? 0.1 : 1.0);

                float2 b = bendDir * arc + windDir * windArc;
                float theta = length(b);
                float2 dir = theta > 1e-4 ? b / theta : bendDir;
                theta = min(theta, _MaxArc);

                // Constant-curvature arc of length h: the blade never stretches, it curls.
                float a = theta * t;
                float up, along;
                if (theta < 1e-3)
                {
                    up = h * t;
                    along = 0.5 * h * theta * t * t;
                }
                else
                {
                    up = h * sin(a) / theta;
                    along = h * (1.0 - cos(a)) / theta;
                }

                // Width grows slowly with height (sqrt), so 1.5m grass is not a giant flat spike.
                float width = _BladeWidth * (0.7 + 0.6 * r4)
                            * lerp(1.0, sqrt(max(h, 0.02) / 0.2), _WidthGrowth)
                            * _LodWidthScale
                            * (1.0 - 0.75 * t * t);

                float3 widthAxis = float3(widthDir.x, 0.0, widthDir.y);
                float3 posWS = rootWS
                             + float3(dir.x * along, up, dir.y * along)
                             + widthAxis * (side * 0.5 * width);

                float3 tangent = float3(dir.x * sin(a), cos(a), dir.y * sin(a));
                float3 n = normalize(cross(widthAxis, tangent));
                // Fake a rounded cross-section so blades shade softly.
                n = normalize(n + widthAxis * side * 0.4);

                o.pos = mul(UNITY_MATRIX_VP, float4(posWS, 1.0));
                o.worldPos = posWS;
                o.normalWS = n;
                o.t = t;

                float3 grown = lerp(_BaseColor.rgb, _TipColor.rgb, t);
                grown = lerp(grown, _DryColor.rgb * lerp(0.6, 1.0, t), _DryAmount * r3 * r3);
                grown *= 0.85 + 0.3 * r2;
                float3 stubble = _StubbleColor.rgb * (0.8 + 0.3 * r1) * lerp(0.75, 1.0, t);
                o.color = cut ? stubble : grown;
                return o;
            }

            fixed4 frag(v2f i, fixed facing : VFACE) : SV_Target
            {
                float3 n = normalize(i.normalWS) * (facing > 0 ? 1.0 : -1.0);
                float3 l = normalize(_GrassLightDir.xyz);
                float wrap = saturate((dot(n, l) + 0.5) / 1.5);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                float back = pow(saturate(dot(viewDir, -l)), 3.0) * _Translucency * i.t;
                float ao = lerp(1.0 - _RootAO, 1.0, i.t);

                float3 lit = i.color * (_GrassAmbient.rgb + _GrassLightColor.rgb * (wrap + back)) * ao;
                return fixed4(lit, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}

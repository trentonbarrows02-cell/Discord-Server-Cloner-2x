// PlayShift grass for the Built-in render pipeline: lit by the main directional light with real-time
// screen-space shadows (receives and casts), ambient from spherical harmonics, back-lit translucency, fog.
// For URP use PlayShift/InstancedGrassURP. Blade shape, cutting animation, interaction and colour come
// from GrassBlade.hlsl, shared by both.
Shader "PlayShift/InstancedGrass"
{
    Properties
    {
        [Header(Colour)]
        _ColYellowGreen ("Yellow-green", Color) = (0.55, 0.66, 0.18, 1)
        _ColGoldenGreen ("Golden green", Color) = (0.66, 0.62, 0.22, 1)
        _ColDarkGreen ("Dark green", Color) = (0.16, 0.32, 0.08, 1)
        _ColFreshGreen ("Fresh green", Color) = (0.33, 0.55, 0.14, 1)
        _ColDry ("Dry golden", Color) = (0.74, 0.64, 0.30, 1)
        _DryAmount ("Dry patch amount", Range(0, 1)) = 0.8
        _StubbleColor ("Cut lawn (base)", Color) = (0.16, 0.50, 0.05, 1)
        _StubbleTipColor ("Cut lawn (cut ends)", Color) = (0.42, 0.86, 0.17, 1)
        _Translucency ("Back-light translucency", Range(0, 2)) = 0.9

        [Header(Shape)]
        _BladeWidth ("Blade width (m)", Float) = 0.0062
        _WidthGrowth ("Width growth with height", Range(0, 1)) = 0.45
        _LeanAmount ("Natural lean", Range(0, 2)) = 1
        _StubbleHeight ("Cut stubble height (m), 0 = cut grass vanishes", Float) = 0
        _CutAnim ("Cut animation (s)", Range(0.05, 0.5)) = 0.18

        [Header(Wind)]
        _WindDirection ("Wind direction (XZ)", Vector) = (0.86, 0, 0.5, 0)
        _WindStrength ("Wind strength (rad)", Range(0, 1)) = 0.17
        _WindSpeed ("Wind speed", Float) = 1.5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        CGINCLUDE
        #pragma target 4.5
        #pragma exclude_renderers gles
        #include "UnityCG.cginc"
        #include "GrassBlade.hlsl"
        float _Translucency;
        ENDCG

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; uint instanceID : SV_InstanceID; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 color : TEXCOORD2;
                float2 tc : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                UNITY_FOG_COORDS(5)
            };

            v2f vert(appdata v)
            {
                GrassBlade b = BuildBlade(v.instanceID, v.uv);
                v2f o;
                o.worldPos = b.positionWS;
                o.pos = UnityWorldToClipPos(b.positionWS);
                o.normalWS = b.normalWS;
                o.color = b.color;
                o.tc = float2(b.t, b.cut);
                o.shadowCoord = ComputeScreenPos(o.pos);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i, fixed facing : VFACE) : SV_Target
            {
                float3 n = normalize(i.normalWS) * (facing > 0 ? 1.0 : -1.0);
                n = normalize(lerp(n, float3(0, 1, 0), 0.25));
                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float shadow = 1.0;
                #if defined(SHADOWS_SCREEN)
                shadow = unitySampleShadow(i.shadowCoord);
                #endif
                float wrap = saturate((dot(n, l) + 0.45) / 1.45);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                float back = GrassTranslucency(viewDir, l, i.tc.x, i.tc.y) * _Translucency;
                float3 ambient = ShadeSH9(float4(n, 1));
                float3 col = i.color * (ambient + _LightColor0.rgb * shadow * (wrap + back));
                UNITY_APPLY_FOG(i.fogCoord, col);
                return fixed4(col, 1);
            }
            ENDCG
        }

        Pass
        {
            Tags { "LightMode" = "ShadowCaster" }
            Cull Off
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; uint instanceID : SV_InstanceID; };
            float4 vert(appdata v) : SV_POSITION
            {
                GrassBlade b = BuildBlade(v.instanceID, v.uv);
                return UnityApplyLinearShadowBias(UnityWorldToClipPos(b.positionWS));
            }
            fixed4 frag() : SV_Target { return 0; }
            ENDCG
        }
    }

    Fallback Off
}

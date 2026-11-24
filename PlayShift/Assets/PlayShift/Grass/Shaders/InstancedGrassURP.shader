// PlayShift grass for the Universal Render Pipeline: lit by the main light with real-time shadows
// (receives and casts), ambient from spherical harmonics, back-lit translucency, fog, depth prepass.
// Blade shape, cutting animation, interaction and colour come from GrassBlade.hlsl.
Shader "PlayShift/InstancedGrassURP"
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
        _CutAnim ("Cut animation (s)", Range(0.05, 0.5)) = 0.12
        _CutEdgeJitter ("Ragged cut edge (cells)", Range(0, 1.5)) = 0.9

        [Header(Wind)]
        _WindDirection ("Wind direction (XZ)", Vector) = (0.86, 0, 0.5, 0)
        _WindStrength ("Wind strength (rad)", Range(0, 1)) = 0.17
        _WindSpeed ("Wind speed", Float) = 1.5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #pragma target 4.5
        #pragma exclude_renderers gles
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "GrassBlade.hlsl"
        float _Translucency;
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; uint instanceID : SV_InstanceID; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 color : TEXCOORD2;
                float2 tc : TEXCOORD3;   // x = t, y = cut
                float fog : TEXCOORD4;
            };

            Varyings vert(Attributes v)
            {
                GrassBlade b = BuildBlade(v.instanceID, v.uv);
                Varyings o;
                o.positionWS = b.positionWS;
                o.positionCS = TransformWorldToHClip(b.positionWS);
                o.normalWS = b.normalWS;
                o.color = b.color;
                o.tc = float2(b.t, b.cut);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                float3 n = normalize(i.normalWS) * (front ? 1.0 : -1.0);
                n = normalize(lerp(n, float3(0, 1, 0), 0.25));
                Light light = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float shadow = light.shadowAttenuation * light.distanceAttenuation;
                float wrap = saturate((dot(n, light.direction) + 0.45) / 1.45);
                float3 viewDir = normalize(GetWorldSpaceViewDir(i.positionWS));
                float back = GrassTranslucency(viewDir, light.direction, i.tc.x, i.tc.y) * _Translucency;
                float3 ambient = SampleSH(n);
                float3 col = i.color * (ambient + light.color * shadow * (wrap + back));
                col = MixFog(col, i.fog);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull Off
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; uint instanceID : SV_InstanceID; };
            float4 vert(Attributes v) : SV_POSITION
            {
                GrassBlade b = BuildBlade(v.instanceID, v.uv);
                float4 cs = TransformWorldToHClip(ApplyShadowBias(b.positionWS, b.normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
                #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return cs;
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull Off
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; uint instanceID : SV_InstanceID; };
            float4 vert(Attributes v) : SV_POSITION { return TransformWorldToHClip(BuildBlade(v.instanceID, v.uv).positionWS); }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }

    Fallback Off
}

// PlayShift scenery shader (Built-in pipeline): trees, bushes, fence, rocks and distant hills.
//  - Matte (soft wrapped diffuse, no specular, no metal), receives and casts real-time shadows, fog.
//  - LOD cross-fade: a dithered blend between a tree's full and lighter model, so trees never visibly pop
//    (LODGroup.fadeMode = CrossFade; per-instance fade with GPU instancing).
//  - Optional vertex colour (hills), tint, and alpha clip.
Shader "PlayShift/Foliage"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _VertexColor ("Use vertex colour", Range(0, 1)) = 0
        _Wrap ("Soft light wrap", Range(0, 1)) = 0.35
        _Naturalize ("Leaves: lime/yellow to forest green", Range(0, 1)) = 0
        _Saturation ("Leaves: saturation", Range(0, 1.5)) = 1
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha clip", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 200

        CGPROGRAM
        #pragma surface surf SoftLambert addshadow fullforwardshadows
        #pragma multi_compile_instancing
        #pragma instancing_options lodfade
        #pragma multi_compile _ LOD_FADE_CROSSFADE
        #pragma shader_feature_local _ALPHATEST_ON
        #pragma target 3.5

        sampler2D _MainTex;
        fixed4 _Color;
        half _VertexColor, _Wrap, _Cutoff, _Naturalize, _Saturation;

        struct Input
        {
            float2 uv_MainTex;
            float4 screenPos;
            float4 color : COLOR;
        };

        half4 LightingSoftLambert(SurfaceOutput s, half3 lightDir, half atten)
        {
            half diff = saturate((dot(s.Normal, lightDir) + _Wrap) / (1.0 + _Wrap));
            half4 c;
            c.rgb = s.Albedo * _LightColor0.rgb * (diff * atten);
            c.a = s.Alpha;
            return c;
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            #ifdef LOD_FADE_CROSSFADE
            float2 px = IN.screenPos.xy / max(IN.screenPos.w, 1e-5) * _ScreenParams.xy;
            UnityApplyDitherCrossFade(px);
            #endif
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            c.rgb *= lerp(fixed3(1, 1, 1), IN.color.rgb, _VertexColor);
            #ifdef _ALPHATEST_ON
            clip(c.a - _Cutoff);
            #endif
            // Leaves only (clearly green texels; bark, fence and rocks keep their colour): less yellow-lime, a deeper,
            // more natural forest green, a little less saturated.
            half3 col = c.rgb;
            half leaf = saturate((col.g - max(col.r, col.b)) * 5.0);
            col = lerp(col, half3(col.r * 0.7, col.g * 0.9, col.b * 1.08 + 0.012), leaf * _Naturalize);
            half lum = dot(col, half3(0.299, 0.587, 0.114));
            col = lerp(lum.xxx, col, lerp(1.0, _Saturation, leaf));
            o.Albedo = col;
            o.Alpha = 1;
            o.Specular = 0;
            o.Gloss = 0;
        }
        ENDCG
    }
    Fallback "Legacy Shaders/VertexLit"
}

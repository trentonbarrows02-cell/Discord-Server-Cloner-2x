// Separable 9-tap Gaussian (5 bilinear fetches) for the pause/settings background blur.
// Lives in Resources so it is included in builds; used by MenuBlurEffect (Built-in render pipeline).
Shader "Hidden/PlayShift/Blur"
{
    Properties { _MainTex ("", 2D) = "white" {} }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _BlurDir; // xy = direction * spread (texels)

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 o = _BlurDir.xy * _MainTex_TexelSize.xy;
                fixed4 c = tex2D(_MainTex, i.uv) * 0.2270270;
                c += (tex2D(_MainTex, i.uv + o * 1.3846154) + tex2D(_MainTex, i.uv - o * 1.3846154)) * 0.3162162;
                c += (tex2D(_MainTex, i.uv + o * 3.2307692) + tex2D(_MainTex, i.uv - o * 3.2307692)) * 0.0702703;
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}

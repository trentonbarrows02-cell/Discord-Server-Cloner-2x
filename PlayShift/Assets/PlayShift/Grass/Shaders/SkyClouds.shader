// Bright daytime sky: blue gradient, sun glow and drifting procedural cumulus clouds.
// Use as RenderSettings.skybox (Lighting window > Environment > Skybox Material). Works in Built-in and URP.
Shader "PlayShift/SkyClouds"
{
    Properties
    {
        _Zenith ("Zenith", Color) = (0.16, 0.38, 0.78, 1)
        _Horizon ("Horizon", Color) = (0.70, 0.82, 0.93, 1)
        _Ground ("Below horizon", Color) = (0.55, 0.62, 0.55, 1)
        _CloudLit ("Cloud (lit)", Color) = (1, 1, 1, 1)
        _CloudShade ("Cloud (shade)", Color) = (0.62, 0.67, 0.76, 1)
        _Coverage ("Cloud coverage", Range(0, 1)) = 0.5
        _CloudScale ("Cloud scale", Float) = 1.6
        _CloudSpeed ("Cloud drift", Float) = 0.004
        _SunSize ("Sun size", Range(50, 2000)) = 600
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Zenith, _Horizon, _Ground, _CloudLit, _CloudShade;
            float _Coverage, _CloudScale, _CloudSpeed, _SunSize;

            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };
            v2f vert(float4 v : POSITION) { v2f o; o.pos = UnityObjectToClipPos(v); o.dir = v.xyz; return o; }

            float h(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float n(float2 p)
            {
                float2 i = floor(p), f = frac(p); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(h(i), h(i + float2(1, 0)), f.x), lerp(h(i + float2(0, 1)), h(i + float2(1, 1)), f.x), f.y);
            }
            float fbm(float2 p) { float a = 0.5, s = 0.0; for (int i = 0; i < 6; i++) { s += a * n(p); p = p * 2.03 + 17.1; a *= 0.5; } return s; }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float y = max(d.y, 0.0);
                float3 col = lerp(_Horizon.rgb, _Zenith.rgb, pow(y, 0.5));
                float3 sunDir = normalize(_WorldSpaceLightPos0.xyz);
                float sd = saturate(dot(d, sunDir));
                col += float3(1.0, 0.9, 0.7) * (pow(sd, _SunSize) * 6.0 + pow(sd, 12.0) * 0.25);

                float2 uv = d.xz / (d.y + 0.08) * _CloudScale + _Time.y * _CloudSpeed;
                float base = fbm(uv * 0.9 + float2(3.0, 1.0));
                float detail = fbm(uv * 3.1 - 5.0);
                float c = smoothstep(1.0 - _Coverage, 1.0 - _Coverage + 0.28, base * 0.75 + detail * 0.35);
                c *= smoothstep(0.02, 0.22, d.y);
                float lit = saturate(0.55 + 0.6 * (detail - 0.45) + 0.3 * dot(normalize(d.xz + 1e-4), normalize(sunDir.xz + 1e-4)));
                col = lerp(col, lerp(_CloudShade.rgb, _CloudLit.rgb, lit), c * 0.95);
                if (d.y < 0.0) col = lerp(_Horizon.rgb, _Ground.rgb, saturate(-d.y * 4.0));
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}

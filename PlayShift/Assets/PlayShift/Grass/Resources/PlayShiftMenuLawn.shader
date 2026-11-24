// Title-screen lawn: shell-textured grass (stacked layers over the field; each layer keeps the strands that are taller
// than its height). Dense, soft, consistent at every distance, a handful of draw-time quads instead of blades:
//  - strands come from a small tiling texture (R = strand height, G = colour variety), sampled at two scales to hide
//    tiling; its mip chain averages far strands into smooth coverage (no sparkle, no speckles);
//  - edges are anti-aliased over the pixel footprint; colour from the gameplay grass palette (_PsGrassAvg) with root
//    shade, per-strand variety and broad patches; gentle wind sway; sun, ambient, tree shadows (screen-space shadow of
//    the ground just below) and fog.
// Drawn in the opaque range (after the ground, before transparents) with blending and no depth writes; the layers are
// ordered bottom to top in the mesh, which is back to front for a camera above the lawn.
Shader "PlayShift/MenuLawn"
{
    Properties
    {
        _Strands ("Strands (R height, G variety)", 2D) = "black" {}
        _Tile ("Strand texture size (m)", Float) = 24
        _Height ("Grass height (m)", Float) = 0.3
        _Layers ("Layer count", Float) = 16
        _Dark ("Root colour (x grass)", Color) = (0.3, 0.38, 0.26, 1)
        _Tip ("Tip colour (x grass)", Color) = (1.18, 1.14, 0.98, 1)
        _Variety ("Per-strand colour variety", Range(0, 1)) = 0.45
        _Patches ("Broad colour patches", Range(0, 1)) = 0.3
        _Wind ("Wind sway (m)", Float) = 0.025
    }
    SubShader
    {
        Tags { "Queue" = "Geometry+60" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _Strands;
            float _Tile, _Height, _Layers, _Variety, _Patches, _Wind;
            float4 _Dark, _Tip;
            float4 _PsGrassAvg; // global: average blade colour (GrassFieldRenderer)

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float t : TEXCOORD1;
                float4 _ShadowCoord : TEXCOORD2;
                UNITY_FOG_COORDS(3)
            };

            v2f vert(appdata v)
            {
                v2f o;
                float t = v.uv.y; // layer 0..1
                float3 w = mul(unity_ObjectToWorld, v.vertex).xyz;
                w.y += t * _Height;
                o.worldPos = w;
                o.t = t;
                o.pos = UnityWorldToClipPos(w);
                o._ShadowCoord = ComputeScreenPos(o.pos);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), f.x), f.y);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float t = i.t;
                float2 p = i.worldPos.xz;
                // Wind: upper parts of the strands sway (the sample point drifts with height).
                float gust = sin(dot(p, float2(0.31, 0.17)) - _Time.y * 1.3) * 0.6 + sin(dot(p, float2(-0.12, 0.27)) - _Time.y * 0.7) * 0.4;
                p += float2(0.8, 0.5) * gust * _Wind * t * t;
                // Two scales (one rotated) so the tiling never lines up.
                float2 uvA = p / _Tile;
                float2 uvB = float2(p.x * 0.8 - p.y * 0.6, p.x * 0.6 + p.y * 0.8) / (_Tile * 1.37) + 0.31;
                float4 a = tex2D(_Strands, uvA), b = tex2D(_Strands, uvB);
                float h = max(a.r, b.r * 0.97);
                float variety = a.r >= b.r * 0.97 ? a.g : b.g;
                // Strand edge over the pixel footprint (soft when far, crisp near); the base layer is solid.
                float aa = max(fwidth(h) * 1.2, 0.04);
                float alpha = t < 0.5 / _Layers ? 1.0 : saturate((h - t) / aa + 0.5);
                clip(alpha - 0.002);

                float3 grass = _PsGrassAvg.rgb;
                float broad = Noise(i.worldPos.xz * 0.09 + 3.7) * 0.6 + Noise(i.worldPos.xz * 0.31 - 1.9) * 0.4;
                grass *= lerp(1.0 - _Patches * 0.5, 1.0 + _Patches * 0.4, broad);
                grass *= lerp(1.0 - _Variety * 0.5, 1.0 + _Variety * 0.5, variety);
                float3 col = grass * lerp(_Dark.rgb, _Tip.rgb, smoothstep(0.0, 1.0, t));

                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float shadow = SHADOW_ATTENUATION(i);
                float wrap = saturate((l.y + 0.45) / 1.45);
                float3 lit = col * (ShadeSH9(float4(0, 1, 0, 1)) + _LightColor0.rgb * shadow * wrap);
                UNITY_APPLY_FOG(i.fogCoord, lit);
                return fixed4(lit, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}

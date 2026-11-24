// Writes the fine grass cut mask (R8, 1 = cut) used for the look of cut grass (blades and lawn edge). The logical
// grass count is unaffected: it lives in the cell grid. Drawn with GL quads in field UV space (0..1 = the lawn).
//  Pass 0: one trimmer stroke, a swept circle (capsule) from A to B, anti-aliased over one texel, max-blended.
//  Pass 1: copy cell states (point-sampled cell texture) over the whole mask (new level, load).
//  Pass 2: constant value (_Value) over a quad (cells changed by gifts).
Shader "Hidden/PlayShift/CutMask"
{
    Properties { _MainTex ("Cells", 2D) = "black" {} }
    CGINCLUDE
    #include "UnityCG.cginc"
    struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }
    ENDCG

    SubShader
    {
        ZTest Always ZWrite Off Cull Off

        Pass // 0: capsule
        {
            BlendOp Max
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 _Seg;        // A.xz, B.xz in field metres
            float4 _MaskParams; // x = radius (m), y = AA width (m), zw = field size (m)
            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = i.uv * _MaskParams.zw;
                float2 a = _Seg.xy, ab = _Seg.zw - _Seg.xy;
                float t = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-8));
                float d = length(p - (a + ab * t));
                float c = saturate((_MaskParams.x - d) / max(_MaskParams.y, 1e-5) + 0.5);
                return c.xxxx;
            }
            ENDCG
        }

        Pass // 1: copy cells
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _MainTex;
            fixed4 frag(v2f i) : SV_Target { return tex2D(_MainTex, i.uv).rrrr; }
            ENDCG
        }

        Pass // 2: constant
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float _Value;
            fixed4 frag(v2f i) : SV_Target { return _Value.xxxx; }
            ENDCG
        }
    }
}

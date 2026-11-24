// Writes the fine grass cut mask (R8, 1 = cut) used for the look of cut grass (blades and lawn edge). The logical
// grass count is unaffected: it lives in the cell grid. Drawn with GL quads in field UV space (0..1 = the lawn).
//  Pass 0: one trimmer stroke, a swept circle (capsule) from A to B, anti-aliased over one texel, max-blended.
//  Pass 1: whole mask from the cells (new level, loaded save): a smooth reconstruction, not 10 cm squares.
//  Pass 2: constant value (_Value) over a quad (kept for tools).
//  Pass 3: cells changed by gifts (cell texture G = changed): blends the smooth reconstruction over the old mask
//          (_OldMask) only where cells changed, so trimmer-cut edges elsewhere keep their exact circles.
//
// Smooth reconstruction: the cut state of the 4x4 nearest cell centres, Gaussian-weighted (sigma 0.8 cells), then
// thresholded at 0.5 with a one-texel soft edge. Stair steps of the 10 cm grid become smooth curves; diagonal and
// round edges stay where the cells put them (within half a cell).
Shader "Hidden/PlayShift/CutMask"
{
    Properties { _MainTex ("Cells (R cut, G changed)", 2D) = "black" {} _OldMask ("Previous mask", 2D) = "black" {} }
    CGINCLUDE
    #include "UnityCG.cginc"
    struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }

    sampler2D _MainTex;
    float4 _MainTex_TexelSize; // 1/cellsX, 1/cellsZ, cellsX, cellsZ

    // x = smoothed cut amount, y = smoothed "changed" amount (0..1).
    float2 SmoothCells(float2 uv)
    {
        float2 g = uv * _MainTex_TexelSize.zw - 0.5;
        float2 g0 = floor(g), fr = g - g0;
        float2 acc = 0;
        float wsum = 0;
        [unroll] for (int j = -1; j <= 2; j++)
        [unroll] for (int i = -1; i <= 2; i++)
        {
            float2 d = float2(i, j) - fr;
            float w = exp(-dot(d, d) * 0.78125); // 1 / (2 * 0.8^2)
            acc += w * tex2Dlod(_MainTex, float4((g0 + float2(i, j) + 0.5) * _MainTex_TexelSize.xy, 0, 0)).rg;
            wsum += w;
        }
        return acc / wsum;
    }

    float Edge(float v)
    {
        float aa = max(fwidth(v) * 0.75, 1e-3);
        return smoothstep(0.5 - aa, 0.5 + aa, v);
    }
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

        Pass // 1: whole mask from the cells, smooth
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            fixed4 frag(v2f i) : SV_Target { return Edge(SmoothCells(i.uv).x).xxxx; }
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

        Pass // 3: changed cells only, smooth, over the old mask
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            sampler2D _OldMask;
            fixed4 frag(v2f i) : SV_Target
            {
                float2 s = SmoothCells(i.uv);
                float old = tex2Dlod(_OldMask, float4(i.uv, 0, 0)).r;
                // Fully the new state where the changed area is solid; a soft seam where it meets untouched cells.
                float k = smoothstep(0.15, 0.6, s.y);
                return lerp(old, Edge(s.x), k).xxxx;
            }
            ENDCG
        }
    }
}

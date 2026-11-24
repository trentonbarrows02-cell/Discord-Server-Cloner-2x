using UnityEngine;
using UnityEngine.Rendering;

namespace PlayShift.Grass
{
    /// <summary>
    /// Title-screen lawn, separate from the gameplay grass: shell-textured grass over the field (PlayShift/MenuLawn).
    /// One mesh of stacked layers, one small generated strand texture, no blades, no cell data and no cutting state, so
    /// it never touches gameplay grass or saved progress. Shown while the title screen is open, hidden on Play.
    /// </summary>
    public static class MenuLawn
    {
        private const int Layers = 16;
        private const int TexSize = 512;
        private static GameObject s_go;
        private static Material s_mat;
        private static Texture2D s_tex;

        public static void Show(Vector3 origin, Vector2 size)
        {
            if (s_go == null && !Build(origin, size)) return;
            s_go.SetActive(true);
        }

        public static void Hide()
        {
            if (s_go != null) s_go.SetActive(false);
        }

        private static bool Build(Vector3 origin, Vector2 size)
        {
            var shader = Shader.Find("PlayShift/MenuLawn");
            if (shader == null) return false;
            s_tex = StrandTexture();
            s_mat = new Material(shader) { name = "MenuLawn" };
            s_mat.SetTexture("_Strands", s_tex);
            s_mat.SetFloat("_Layers", Layers);

            var v = new Vector3[Layers * 4];
            var uv = new Vector2[Layers * 4];
            var tri = new int[Layers * 6];
            for (int k = 0; k < Layers; k++)
            {
                float t = k / (float)(Layers - 1);
                int b = k * 4;
                v[b] = new Vector3(0f, 0f, 0f); v[b + 1] = new Vector3(0f, 0f, size.y);
                v[b + 2] = new Vector3(size.x, 0f, size.y); v[b + 3] = new Vector3(size.x, 0f, 0f);
                for (int j = 0; j < 4; j++) uv[b + j] = new Vector2(0f, t);
                // Bottom layer first: drawn back to front for a camera above the lawn.
                int ti = k * 6;
                tri[ti] = b; tri[ti + 1] = b + 1; tri[ti + 2] = b + 2;
                tri[ti + 3] = b; tri[ti + 4] = b + 2; tri[ti + 5] = b + 3;
            }
            var mesh = new Mesh { name = "MenuLawnShells" };
            mesh.vertices = v;
            mesh.uv = uv;
            mesh.triangles = tri;
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(new Vector3(size.x * 0.5f, 0.3f, size.y * 0.5f), new Vector3(size.x, 1f, size.y));

            s_go = new GameObject("MenuLawn");
            s_go.transform.position = origin + Vector3.up * 0.004f;
            s_go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = s_go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = s_mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = true;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return true;
        }

        /// <summary>
        /// R = strand height (0 between strands), G = colour variety per strand. Strands are small round dots on a
        /// jittered grid (~1.8 cm at 9 m per tile), wrap-around so the texture tiles; mipmaps average them into smooth
        /// coverage far away.
        /// </summary>
        private static Texture2D StrandTexture()
        {
            var px = new Color32[TexSize * TexSize];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(18, 128, 0, 255);
            var rng = new System.Random(4242);
            const float spacing = 1.35f;
            // Clumps: strands grouped into tufts with lower gaps between them (reads as grass, not felt, from the
            // title camera). Tileable value noise over the texture.
            float Clump(float x, float y)
            {
                const int cells = 24;
                float u = x / TexSize * cells, w = y / TexSize * cells;
                int ix = Mathf.FloorToInt(u), iy = Mathf.FloorToInt(w);
                float fx = u - ix, fy = w - iy;
                fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
                float H(int a, int b)
                {
                    a = ((a % cells) + cells) % cells; b = ((b % cells) + cells) % cells;
                    uint hsh = (uint)(a * 73856093 ^ b * 19349663);
                    hsh ^= hsh >> 13; hsh *= 0x5bd1e995; hsh ^= hsh >> 15;
                    return (hsh & 0xFFFF) / 65535f;
                }
                return Mathf.Lerp(Mathf.Lerp(H(ix, iy), H(ix + 1, iy), fx), Mathf.Lerp(H(ix, iy + 1), H(ix + 1, iy + 1), fx), fy);
            }
            int n = (int)(TexSize / spacing);
            for (int gy = 0; gy < n; gy++)
                for (int gx = 0; gx < n; gx++)
                {
                    float cx = (gx + (float)rng.NextDouble()) * spacing, cy = (gy + (float)rng.NextDouble()) * spacing;
                    float clump = Clump(cx, cy);
                    float h = (0.45f + 0.55f * (float)rng.NextDouble()) * (0.45f + 0.55f * Mathf.SmoothStep(0.2f, 0.8f, clump));
                    if (rng.NextDouble() < 0.12) h *= 0.55f; // a few short ones
                    byte var = (byte)rng.Next(0, 256);
                    float r = 0.8f + 0.5f * (float)rng.NextDouble();
                    int x0 = Mathf.FloorToInt(cx - r), x1 = Mathf.CeilToInt(cx + r), y0 = Mathf.FloorToInt(cy - r), y1 = Mathf.CeilToInt(cy + r);
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                            float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
                            if (d > 1f) continue;
                            float hh = h * (1f - 0.35f * d * d); // rounded tip
                            int wx = ((x % TexSize) + TexSize) % TexSize, wy = ((y % TexSize) + TexSize) % TexSize;
                            int idx = wy * TexSize + wx;
                            byte hb = (byte)Mathf.Clamp(Mathf.RoundToInt(hh * 255f), 0, 255);
                            if (hb > px[idx].r) px[idx] = new Color32(hb, var, 0, 255);
                        }
                }
            var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, true, true)
            {
                name = "MenuLawnStrands", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8,
            };
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }
    }
}

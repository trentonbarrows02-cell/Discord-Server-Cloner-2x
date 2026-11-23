using System.Collections.Generic;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Builds the outdoor surroundings for the grass test area at runtime (no art assets needed):
    /// a short green lawn under the grass (so cut grass reveals lawn, never dirt), a textured meadow around
    /// it, and natural-looking trees and bushes around the perimeter made of leaf cards (one merged foliage
    /// mesh, alpha-clipped, casting shadows). Replace with your own environment art at any time.
    /// </summary>
    public sealed class TestAreaEnvironment : MonoBehaviour
    {
        [SerializeField] private GrassLevelManager manager;
        [SerializeField] private int treeCount = 34;
        [SerializeField] private int bushCount = 26;
        [SerializeField] private int seed = 8;

        private System.Random _rng;
        private float R() => (float)_rng.NextDouble();

        private void Start()
        {
            if (manager == null) manager = GrassLevelManager.Instance;
            _rng = new System.Random(seed);
            var size = manager != null && manager.Settings != null ? manager.Settings.fieldSizeMeters : new Vector2(24f, 24f);
            Vector3 origin = manager != null ? manager.FieldTransform.position : Vector3.zero;
            BuildGround(origin, size);
            BuildVegetation(origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f), Mathf.Max(size.x, size.y));
        }

        private void BuildGround(Vector3 origin, Vector2 size)
        {
            var lawnTex = Speckle(512, new Color32(79, 127, 37, 255), new[] { "5f9a2c", "457322", "6fa834", "3c6a1c", "7cb33c", "577d2a" }, 16000, 0.6f, 2.2f);
            var meadowTex = Speckle(512, new Color32(85, 104, 44, 255), new[] { "617a30", "4a5d26", "6e7d38", "5d5a33", "73883c", "3f5222" }, 14000, 0.6f, 2.5f);

            var meadow = GameObject.CreatePrimitive(PrimitiveType.Plane);
            meadow.name = "Meadow";
            meadow.transform.SetParent(transform, false);
            meadow.transform.position = origin + new Vector3(size.x * 0.5f, -0.01f, size.y * 0.5f);
            meadow.transform.localScale = new Vector3(50f, 1f, 50f);
            var mm = PipelineMaterials.CreateLit("Meadow", Color.white, meadowTex);
            mm.mainTextureScale = new Vector2(220f, 220f);
            if (mm.HasProperty("_BaseMap")) mm.SetTextureScale("_BaseMap", new Vector2(220f, 220f));
            meadow.GetComponent<Renderer>().sharedMaterial = mm;

            var lawn = GameObject.CreatePrimitive(PrimitiveType.Plane);
            lawn.name = "LawnGround";
            lawn.transform.SetParent(transform, false);
            lawn.transform.position = origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f);
            lawn.transform.localScale = new Vector3((size.x + 1.2f) / 10f, 1f, (size.y + 1.2f) / 10f);
            var lm = PipelineMaterials.CreateLit("LawnGround", Color.white, lawnTex);
            var tiles = new Vector2(size.x / 1.5f, size.y / 1.5f);
            lm.mainTextureScale = tiles;
            if (lm.HasProperty("_BaseMap")) lm.SetTextureScale("_BaseMap", tiles);
            lawn.GetComponent<Renderer>().sharedMaterial = lm;
        }

        private void BuildVegetation(Vector3 centre, float lawnSize)
        {
            var leafTex = LeafClusterTexture(256);
            var leafMat = PipelineMaterials.CreateLit("LeafCards", Color.white, leafTex, alphaClip: true, cutoff: 0.45f, doubleSided: true);
            var barkMat = PipelineMaterials.CreateLit("Bark", new Color(0.29f, 0.23f, 0.16f));
            var cards = new List<(Vector3 p, Quaternion r, float s, Color c)>();

            for (int i = 0; i < treeCount; i++)
            {
                float a = i / (float)treeCount * Mathf.PI * 2f + R() * 0.15f;
                float r = lawnSize * 0.85f + 6f + R() * 18f;
                AddTree(centre + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r), 0.9f + R() * 0.6f, barkMat, cards);
            }
            for (int i = 0; i < bushCount; i++)
            {
                float a = R() * Mathf.PI * 2f, r = lawnSize * 0.75f + 2f + R() * 6f;
                AddBush(centre + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r), 0.9f + R() * 0.7f, cards);
            }

            // Merge all leaf cards into meshes of <= 16k cards (one draw call each).
            const int perMesh = 16000;
            for (int start = 0; start < cards.Count; start += perMesh)
            {
                int n = Mathf.Min(perMesh, cards.Count - start);
                var v = new Vector3[n * 4]; var uv = new Vector2[n * 4]; var col = new Color[n * 4]; var nrm = new Vector3[n * 4]; var tri = new int[n * 6];
                for (int k = 0; k < n; k++)
                {
                    var (p, q, s, c) = cards[start + k];
                    Vector3 rx = q * Vector3.right * (s * 0.5f), ry = q * Vector3.up * (s * 0.5f);
                    int b = k * 4;
                    v[b] = p - rx - ry; v[b + 1] = p + rx - ry; v[b + 2] = p + rx + ry; v[b + 3] = p - rx + ry;
                    uv[b] = new Vector2(0, 0); uv[b + 1] = new Vector2(1, 0); uv[b + 2] = new Vector2(1, 1); uv[b + 3] = new Vector2(0, 1);
                    // Normals pointing away from the crown centre-ish look softer than flat card normals.
                    Vector3 nn = (q * Vector3.forward + Vector3.up * 0.6f).normalized;
                    for (int j = 0; j < 4; j++) { col[b + j] = c; nrm[b + j] = nn; }
                    tri[k * 6] = b; tri[k * 6 + 1] = b + 2; tri[k * 6 + 2] = b + 1; tri[k * 6 + 3] = b; tri[k * 6 + 4] = b + 3; tri[k * 6 + 5] = b + 2;
                }
                var mesh = new Mesh { name = "Foliage", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.vertices = v; mesh.uv = uv; mesh.colors = col; mesh.normals = nrm; mesh.triangles = tri;
                mesh.RecalculateBounds();
                var go = new GameObject("Foliage");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = leafMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
            }
        }

        private void AddTree(Vector3 pos, float s, Material bark, List<(Vector3, Quaternion, float, Color)> cards)
        {
            float trunkH = (3.2f + R() * 2.2f) * s;
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(trunk.GetComponent<Collider>());
            trunk.name = "Trunk";
            trunk.transform.SetParent(transform, false);
            trunk.transform.position = pos + Vector3.up * trunkH * 0.5f;
            trunk.transform.localScale = new Vector3(0.45f * s, trunkH * 0.5f, 0.45f * s);
            trunk.GetComponent<Renderer>().sharedMaterial = bark;
            for (int b = 0; b < 4; b++)
            {
                var br = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(br.GetComponent<Collider>());
                br.name = "Branch";
                br.transform.SetParent(transform, false);
                float a = R() * Mathf.PI * 2f;
                br.transform.position = pos + new Vector3(Mathf.Cos(a) * 0.6f * s, trunkH * (0.7f + R() * 0.25f), Mathf.Sin(a) * 0.6f * s);
                br.transform.rotation = Quaternion.Euler(Mathf.Sin(a) * 50f, 0f, -Mathf.Cos(a) * 50f);
                br.transform.localScale = new Vector3(0.14f * s, 1.1f * s, 0.14f * s);
                br.GetComponent<Renderer>().sharedMaterial = bark;
            }
            int clusters = 7 + _rng.Next(5);
            for (int c = 0; c < clusters; c++)
            {
                float a = R() * Mathf.PI * 2f, r = R() * 1.9f * s;
                var cc = pos + new Vector3(Mathf.Cos(a) * r, trunkH + (R() * 2.6f - 0.4f) * s, Mathf.Sin(a) * r);
                float cr = (1.1f + R() * 0.9f) * s;
                for (int k = 0; k < 26; k++)
                {
                    var off = Random.insideUnitSphere; off.y *= 0.75f;
                    float tint = 0.75f + R() * 0.4f;
                    cards.Add((cc + off * cr, Quaternion.Euler(R() * 360f, R() * 360f, R() * 360f), (1.1f + R() * 0.8f) * s, new Color(tint, tint * 1.02f, tint * 0.9f)));
                }
            }
        }

        private void AddBush(Vector3 pos, float s, List<(Vector3, Quaternion, float, Color)> cards)
        {
            for (int k = 0; k < 22; k++)
            {
                float u = R() * Mathf.PI * 2f, rr = s * Mathf.Sqrt(R());
                float tint = 0.7f + R() * 0.4f;
                cards.Add((pos + new Vector3(Mathf.Cos(u) * rr, 0.35f * s + R() * 0.6f * s, Mathf.Sin(u) * rr),
                    Quaternion.Euler(R() * 360f, R() * 360f, R() * 360f), (0.8f + R() * 0.5f) * s, new Color(tint, tint * 1.02f, tint * 0.9f)));
            }
        }

        private Texture2D Speckle(int size, Color32 baseCol, string[] hex, int count, float rMin, float rMax)
        {
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = baseCol;
            var cols = new Color32[hex.Length];
            for (int i = 0; i < hex.Length; i++) { ColorUtility.TryParseHtmlString("#" + hex[i], out var c); cols[i] = c; }
            for (int k = 0; k < count; k++)
            {
                var c = cols[_rng.Next(cols.Length)];
                float r = rMin + R() * (rMax - rMin), cx = R() * size, cy = R() * size;
                int x0 = Mathf.FloorToInt(cx - r), x1 = Mathf.CeilToInt(cx + r), y0 = Mathf.FloorToInt(cy - r), y1 = Mathf.CeilToInt(cy + r);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = x - cx, dy = (y - cy) * 1.6f;
                        if (dx * dx + dy * dy > r * r) continue;
                        px[((y % size + size) % size) * size + (x % size + size) % size] = c;
                    }
            }
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 8 };
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }

        private Texture2D LeafClusterTexture(int size)
        {
            var px = new Color32[size * size];
            var greens = new[] { new Color32(47, 93, 31, 255), new Color32(61, 112, 38, 255), new Color32(76, 128, 48, 255), new Color32(42, 79, 27, 255), new Color32(93, 143, 53, 255), new Color32(54, 99, 42, 255) };
            for (int k = 0; k < 260; k++)
            {
                float a = R() * Mathf.PI * 2f, rad = Mathf.Sqrt(R()) * size * 0.42f;
                float cx = size / 2f + Mathf.Cos(a) * rad, cy = size / 2f + Mathf.Sin(a) * rad;
                float lw = 4f + R() * 7f, lh = 2f + R() * 3f, rot = R() * Mathf.PI;
                var c = greens[_rng.Next(greens.Length)];
                float cs = Mathf.Cos(rot), sn = Mathf.Sin(rot);
                for (int y = Mathf.FloorToInt(cy - lw); y <= cy + lw; y++)
                    for (int x = Mathf.FloorToInt(cx - lw); x <= cx + lw; x++)
                    {
                        if (x < 0 || y < 0 || x >= size || y >= size) continue;
                        float dx = x - cx, dy = y - cy, u = dx * cs + dy * sn, w = -dx * sn + dy * cs;
                        if ((u * u) / (lw * lw) + (w * w) / (lh * lh) <= 1f) px[y * size + x] = c;
                    }
            }
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }
    }
}

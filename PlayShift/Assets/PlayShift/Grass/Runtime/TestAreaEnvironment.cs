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

        private Vector3 _origin;
        private Vector2 _size;

        /// <summary>Flat around the lawn and path, gently rolling hills further out.</summary>
        private float TerrainHeight(float x, float z)
        {
            float cx = _origin.x + _size.x * 0.5f, cz = _origin.z + _size.y * 0.5f;
            float dx = Mathf.Max(Mathf.Abs(x - cx) - _size.x * 0.5f, 0f), dz = Mathf.Max(Mathf.Abs(z - cz) - _size.y * 0.5f, 0f);
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((d - 5f) / 30f));
            float n = 0f, amp = 0.5f, f = 0.012f;
            for (int i = 0; i < 4; i++) { n += amp * Mathf.PerlinNoise(x * f + 31.7f * i, z * f - 17.3f * i); amp *= 0.5f; f *= 2.1f; }
            return _origin.y + t * (n * 10f - 2.5f) - 0.03f;
        }

        private void BuildGround(Vector3 origin, Vector2 size)
        {
            _origin = origin; _size = size;
            var lawnTex = Speckle(512, new Color32(54, 112, 30, 255), new[] { "4a9426", "3a7a1e", "55a02c", "2f6619", "5fa834", "44861f" }, 16000, 0.6f, 2.2f);
            var meadowTex = Speckle(512, new Color32(85, 104, 44, 255), new[] { "617a30", "4a5d26", "6e7d38", "5d5a33", "73883c", "3f5222" }, 14000, 0.6f, 2.5f);
            var gravelTex = Speckle(256, new Color32(155, 143, 120, 255), new[] { "b3a891", "857a65", "a89a7d", "c2b9a3", "6f6555", "8c7f68" }, 7000, 0.6f, 2.2f);

            // Rolling meadow terrain.
            const int res = 200; const float extent = 700f;
            var verts = new Vector3[(res + 1) * (res + 1)]; var uvs = new Vector2[verts.Length]; var tris = new int[res * res * 6];
            float cx = origin.x + size.x * 0.5f, cz = origin.z + size.y * 0.5f;
            for (int j = 0; j <= res; j++)
                for (int i = 0; i <= res; i++)
                {
                    float x = cx - extent / 2 + extent * i / res, z = cz - extent / 2 + extent * j / res;
                    verts[j * (res + 1) + i] = new Vector3(x, TerrainHeight(x, z), z);
                    uvs[j * (res + 1) + i] = new Vector2(x / 2.3f, z / 2.3f);
                }
            int k = 0;
            for (int j = 0; j < res; j++)
                for (int i = 0; i < res; i++)
                {
                    int v0 = j * (res + 1) + i;
                    tris[k++] = v0; tris[k++] = v0 + res + 1; tris[k++] = v0 + 1;
                    tris[k++] = v0 + 1; tris[k++] = v0 + res + 1; tris[k++] = v0 + res + 2;
                }
            var terrain = new Mesh { name = "Meadow", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = verts, uv = uvs, triangles = tris };
            terrain.RecalculateNormals();
            // The meadow mesh also spans the lawn and paths, so it is the walkable ground for the player.
            AddMesh("Meadow", terrain, PipelineMaterials.CreateLit("Meadow", Color.white, meadowTex), Vector3.zero, collider: true);

            // Short green lawn under the grass (cut grass reveals lawn, never dirt).
            var lawn = GameObject.CreatePrimitive(PrimitiveType.Plane);
            lawn.name = "LawnGround";
            Destroy(lawn.GetComponent<Collider>());
            lawn.transform.SetParent(transform, false);
            lawn.transform.position = origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f);
            lawn.transform.localScale = new Vector3((size.x + 1.2f) / 10f, 1f, (size.y + 1.2f) / 10f);
            var lm = PipelineMaterials.CreateLit("LawnGround", Color.white, lawnTex);
            var tiles = new Vector2(size.x / 1.5f, size.y / 1.5f);
            lm.mainTextureScale = tiles;
            if (lm.HasProperty("_BaseMap")) lm.SetTextureScale("_BaseMap", tiles);
            lawn.GetComponent<Renderer>().sharedMaterial = lm;

            // Gravel path: a ring around the lawn and one path leading off towards the trees.
            var gm = PipelineMaterials.CreateLit("GravelPath", Color.white, gravelTex);
            float o = 2.6f, ii = 0.9f;
            var ring = new Mesh { name = "PathRing" };
            var rv = new System.Collections.Generic.List<Vector3>(); var rt = new System.Collections.Generic.List<int>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int s0 = rv.Count; rv.Add(a); rv.Add(b); rv.Add(c); rv.Add(d); rt.AddRange(new[] { s0, s0 + 2, s0 + 1, s0, s0 + 3, s0 + 2 }); }
            float y = origin.y + 0.006f, X0 = origin.x, Z0 = origin.z, X1 = origin.x + size.x, Z1 = origin.z + size.y;
            Quad(new Vector3(X0 - o, y, Z0 - o), new Vector3(X1 + o, y, Z0 - o), new Vector3(X1 + o, y, Z0 - ii), new Vector3(X0 - o, y, Z0 - ii));
            Quad(new Vector3(X0 - o, y, Z1 + ii), new Vector3(X1 + o, y, Z1 + ii), new Vector3(X1 + o, y, Z1 + o), new Vector3(X0 - o, y, Z1 + o));
            Quad(new Vector3(X0 - o, y, Z0 - ii), new Vector3(X0 - ii, y, Z0 - ii), new Vector3(X0 - ii, y, Z1 + ii), new Vector3(X0 - o, y, Z1 + ii));
            Quad(new Vector3(X1 + ii, y, Z0 - ii), new Vector3(X1 + o, y, Z0 - ii), new Vector3(X1 + o, y, Z1 + ii), new Vector3(X1 + ii, y, Z1 + ii));
            // Spur path following the terrain.
            float sx = cx + 5f;
            for (int seg = 0; seg < 20; seg++)
            {
                float za = Z1 + o + seg * 2f, zb = za + 2f;
                Quad(new Vector3(sx - 0.85f, TerrainHeight(sx, za) + 0.03f, za), new Vector3(sx + 0.85f, TerrainHeight(sx, za) + 0.03f, za),
                     new Vector3(sx + 0.85f, TerrainHeight(sx, zb) + 0.03f, zb), new Vector3(sx - 0.85f, TerrainHeight(sx, zb) + 0.03f, zb));
            }
            ring.SetVertices(rv);
            var ruv = new Vector2[rv.Count];
            for (int i = 0; i < rv.Count; i++) ruv[i] = new Vector2(rv[i].x / 1.5f, rv[i].z / 1.5f);
            ring.uv = ruv;
            ring.SetTriangles(rt, 0);
            ring.RecalculateNormals();
            AddMesh("GravelPath", ring, gm, Vector3.zero);
        }

        private void AddMesh(string name, Mesh mesh, Material mat, Vector3 pos, bool collider = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.position = pos;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.receiveShadows = true;
            if (collider) go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private void BuildVegetation(Vector3 centre, float lawnSize)
        {
            var leafTex = LeafClusterTexture(256);
            var leafMat = PipelineMaterials.CreateLit("LeafCards", Color.white, leafTex, alphaClip: true, cutoff: 0.45f, doubleSided: true);
            var barkMat = PipelineMaterials.CreateLit("Bark", new Color(0.29f, 0.23f, 0.16f));
            var cards = new List<(Vector3 p, Quaternion r, float s, Color c)>();

            var needleTex = NeedleTexture(256);
            var needleMat = PipelineMaterials.CreateLit("NeedleCards", Color.white, needleTex, alphaClip: true, cutoff: 0.45f, doubleSided: true);
            var needles = new List<(Vector3 p, Quaternion r, float s, Color c)>();
            Vector3 OnGround(float x, float z) => new Vector3(x, TerrainHeight(x, z), z);

            // Near ring: broadleaf trees of varied shape and size mixed with conifers.
            for (int i = 0; i < treeCount; i++)
            {
                float a = i / (float)treeCount * Mathf.PI * 2f + R() * 0.12f;
                float r = lawnSize * 0.72f + 9f + R() * 16f;
                var p = OnGround(centre.x + Mathf.Cos(a) * r, centre.z + Mathf.Sin(a) * r);
                if (R() < 0.3f) AddConifer(p, 0.8f + R() * 0.5f, barkMat, needles);
                else AddTree(p, 0.7f + R() * 0.75f, barkMat, cards);
            }
            // Bushes along the path.
            for (int i = 0; i < bushCount; i++)
            {
                float a = R() * Mathf.PI * 2f, r = lawnSize * 0.72f + 3f + R() * 6f;
                AddBush(OnGround(centre.x + Mathf.Cos(a) * r, centre.z + Mathf.Sin(a) * r), 0.8f + R() * 0.8f, cards);
            }
            // Distant tree line on the hills.
            for (int i = 0; i < 90; i++)
            {
                float a = R() * Mathf.PI * 2f, r = 70f + R() * 70f;
                var p = OnGround(centre.x + Mathf.Cos(a) * r, centre.z + Mathf.Sin(a) * r);
                if (R() < 0.4f) AddConifer(p, 1f + R() * 0.6f, barkMat, needles);
                else AddTree(p, 1f + R() * 0.8f, barkMat, cards);
            }
            BuildCards(needles, needleMat, "Conifers");

            BuildCards(cards, leafMat, "Foliage");
        }

        private void BuildCards(List<(Vector3 p, Quaternion r, float s, Color c)> cards, Material mat, string name)
        {
            var leafMat = mat;
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
                var go = new GameObject(name);
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

        private void AddConifer(Vector3 pos, float s, Material bark, List<(Vector3, Quaternion, float, Color)> cards)
        {
            float h = (7f + R() * 5f) * s;
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(trunk.GetComponent<Collider>());
            trunk.name = "ConiferTrunk";
            trunk.transform.SetParent(transform, false);
            trunk.transform.position = pos + Vector3.up * h * 0.45f;
            trunk.transform.localScale = new Vector3(0.34f * s, h * 0.45f, 0.34f * s);
            trunk.GetComponent<Renderer>().sharedMaterial = bark;
            const int layers = 7;
            for (int l = 0; l < layers; l++)
            {
                float t = l / (float)(layers - 1), ly = h * (0.18f + 0.8f * t), lr = (2.3f * (1f - t) + 0.35f) * s;
                int n = Mathf.RoundToInt(30f * (1f - t) + 8f);
                for (int k = 0; k < n; k++)
                {
                    float a = R() * Mathf.PI * 2f, rr = lr * Mathf.Sqrt(R());
                    float tint = 0.75f + R() * 0.4f;
                    cards.Add((pos + new Vector3(Mathf.Cos(a) * rr, ly + (R() - 0.6f) * 0.8f * s, Mathf.Sin(a) * rr),
                        Quaternion.Euler(R() * 360f, R() * 360f, R() * 360f), (1f + R() * 0.6f) * s, new Color(tint, tint * 1.02f, tint * 0.9f)));
                }
            }
        }

        private Texture2D NeedleTexture(int size)
        {
            var px = new Color32[size * size];
            var greens = new[] { new Color32(29, 61, 26, 255), new Color32(36, 73, 32, 255), new Color32(45, 85, 38, 255), new Color32(24, 53, 22, 255), new Color32(51, 94, 42, 255) };
            for (int k = 0; k < 420; k++)
            {
                float a = R() * Mathf.PI * 2f, rad = Mathf.Sqrt(R()) * size * 0.44f;
                float x = size / 2f + Mathf.Cos(a) * rad, y = size / 2f + Mathf.Sin(a) * rad * 0.7f;
                float ex = x + (R() - 0.5f) * 14f, ey = y + 4f + R() * 8f;
                var c = greens[_rng.Next(greens.Length)];
                for (int st = 0; st <= 12; st++)
                {
                    float t = st / 12f;
                    int ix = Mathf.RoundToInt(Mathf.Lerp(x, ex, t)), iy = Mathf.RoundToInt(Mathf.Lerp(y, ey, t));
                    for (int oy = 0; oy <= 1; oy++)
                        if (ix >= 0 && iy + oy >= 0 && ix < size && iy + oy < size) px[(iy + oy) * size + ix] = c;
                }
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
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

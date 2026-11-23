using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PlayShift.Grass
{
    /// <summary>
    /// Open-field map around the lawn, built at runtime (no imported art): completely flat ground, the lawn shaded by
    /// PlayShift/GrassGround (shows grown/cut grass from the cell data, so distant progress is visible without drawing
    /// blades), a dense continuous bush hedge on all four sides, a few flowers, trees and rocks around the outside,
    /// and a ring of distant mountains. No buildings, roads or paths. Everything is merged into a handful of static
    /// meshes (one per material), so the scenery costs a few draw calls whatever the map size.
    /// </summary>
    public sealed class OpenFieldEnvironment : MonoBehaviour
    {
        [SerializeField] private GrassLevelManager manager;
        [Tooltip("Material using PlayShift/GrassGround (assigned by the setup menu so the shader ships in builds).")]
        [SerializeField] private Material lawnMaterial;
        [SerializeField] private int seed = 4242;

        [Header("Hedge")]
        [Tooltip("Distance from the lawn edge to the hedge centre line (m).")]
        [SerializeField] private float hedgeOffset = 1.8f;
        [SerializeField] private float hedgeHeight = 1.9f;

        [Header("Scenery")]
        [SerializeField] private int treeCount = 14;
        [SerializeField] private int rockCount = 18;
        [SerializeField] private int flowerClusters = 48;
        [SerializeField] private int mountainCount = 18;
        [SerializeField] private float mountainDistance = 780f;

        private System.Random _rng;
        private float R() => (float)_rng.NextDouble();
        private float R(float a, float b) => a + R() * (b - a);

        // Merge buckets, one combined mesh per material.
        private readonly Dictionary<Material, List<CombineInstance>> _buckets = new Dictionary<Material, List<CombineInstance>>();
        private Mesh _blob, _cone6, _cone9, _cylinder;

        private void Start()
        {
            if (manager == null) manager = GrassLevelManager.Instance;
            _rng = new System.Random(seed);
            Vector2 size = manager != null && manager.Settings != null ? manager.Settings.fieldSizeMeters : new Vector2(100f, 100f);
            Vector3 origin = manager != null ? manager.FieldTransform.position : Vector3.zero;

            _blob = Icosphere(1, 0.12f);
            _cone6 = Cone(6);
            _cone9 = Cone(9);
            _cylinder = Cylinder(7);

            BuildGround(origin, size);
            BuildHedge(origin, size);
            BuildFlowers(origin, size);
            BuildTrees(origin, size);
            BuildRocks(origin, size);
            BuildMountains(origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f));
            FlushBuckets();
        }

        // ------------------------------------------------------------------ pieces

        private void BuildGround(Vector3 origin, Vector2 size)
        {
            var lawnTex = Speckle(512, new Color32(63, 138, 28, 255), new[] { "4fa224", "3a7d18", "5cb52b", "33721a", "6cc236", "468f20" }, 16000, 0.6f, 2.2f);
            var meadowTex = Speckle(512, new Color32(86, 130, 40, 255), new[] { "6b9a34", "5a8a2c", "78a83c", "4d7a26", "86b444" }, 12000, 0.7f, 2.4f);

            // The lawn: GrassGround reads the cell buffer (grown vs cut) set globally by GrassFieldRenderer.
            var groundShader = Shader.Find("PlayShift/GrassGround");
            Material lawnMat = lawnMaterial != null ? new Material(lawnMaterial)
                : groundShader != null ? new Material(groundShader) { name = "OpenFieldLawn" }
                : PipelineMaterials.CreateLit("OpenFieldLawn", Color.white, lawnTex);
            lawnMat.mainTexture = lawnTex;
            var lawn = Quad("Lawn", origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f), size, lawnMat);
            lawn.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;

            // Flat meadow out to the horizon (slightly below the lawn so they never z-fight).
            var meadowMat = PipelineMaterials.CreateLit("Meadow", Color.white, meadowTex);
            float extent = 2600f;
            meadowMat.mainTextureScale = new Vector2(extent / 2.5f, extent / 2.5f);
            if (meadowMat.HasProperty("_BaseMap")) meadowMat.SetTextureScale("_BaseMap", meadowMat.mainTextureScale);
            var meadow = Quad("Meadow", origin + new Vector3(size.x * 0.5f, -0.012f, size.y * 0.5f), new Vector2(extent, extent), meadowMat);
            meadow.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;

            // One flat collider for everything walkable (completely flat map).
            var floor = new GameObject("Floor");
            floor.transform.SetParent(transform, false);
            floor.transform.position = origin + new Vector3(size.x * 0.5f, -0.5f, size.y * 0.5f);
            floor.AddComponent<BoxCollider>().size = new Vector3(extent, 1f, extent);

            // Keep the player inside the hedge.
            AddWall(origin + new Vector3(size.x * 0.5f, 1f, -hedgeOffset), new Vector3(size.x + 2f * hedgeOffset, 2f, 0.6f));
            AddWall(origin + new Vector3(size.x * 0.5f, 1f, size.y + hedgeOffset), new Vector3(size.x + 2f * hedgeOffset, 2f, 0.6f));
            AddWall(origin + new Vector3(-hedgeOffset, 1f, size.y * 0.5f), new Vector3(0.6f, 2f, size.y + 2f * hedgeOffset));
            AddWall(origin + new Vector3(size.x + hedgeOffset, 1f, size.y * 0.5f), new Vector3(0.6f, 2f, size.y + 2f * hedgeOffset));
        }

        private void BuildHedge(Vector3 origin, Vector2 size)
        {
            var greens = new[]
            {
                Mat("HedgeDark", new Color(0.13f, 0.33f, 0.09f)),
                Mat("HedgeMid", new Color(0.2f, 0.44f, 0.12f)),
                Mat("HedgeLight", new Color(0.3f, 0.55f, 0.16f)),
            };
            float o = hedgeOffset;
            // Perimeter as a rectangle path; two staggered rows of puffy blobs, overlapping so it reads continuous.
            var corners = new[]
            {
                origin + new Vector3(-o, 0f, -o), origin + new Vector3(size.x + o, 0f, -o),
                origin + new Vector3(size.x + o, 0f, size.y + o), origin + new Vector3(-o, 0f, size.y + o),
            };
            for (int side = 0; side < 4; side++)
            {
                Vector3 a = corners[side], b = corners[(side + 1) % 4];
                Vector3 dir = (b - a).normalized, outward = Vector3.Cross(Vector3.up, dir); // corners run anticlockwise
                float len = Vector3.Distance(a, b);
                for (int row = 0; row < 2; row++)
                {
                    float spacing = 1.05f, start = row * spacing * 0.5f;
                    for (float d = start; d <= len; d += spacing)
                    {
                        Vector3 p = a + dir * d + outward * (row * 0.9f + R(-0.15f, 0.15f));
                        float r = R(0.85f, 1.25f), h = hedgeHeight * R(0.75f, 1.05f) * (row == 0 ? 1f : 1.12f);
                        AddBlob(greens[_rng.Next(greens.Length)], p + Vector3.up * (h * 0.48f), new Vector3(r * 1.25f, h * 0.55f, r * 1.25f));
                    }
                }
            }
        }

        private void BuildFlowers(Vector3 origin, Vector2 size)
        {
            var colours = new[]
            {
                Mat("FlowerWhite", new Color(0.95f, 0.95f, 0.92f)), Mat("FlowerYellow", new Color(1f, 0.85f, 0.2f)),
                Mat("FlowerPink", new Color(1f, 0.5f, 0.7f)), Mat("FlowerPurple", new Color(0.62f, 0.42f, 0.95f)),
            };
            for (int k = 0; k < flowerClusters; k++)
            {
                // On the strip between the lawn edge and the hedge, all around.
                Vector3 p = PerimeterPoint(origin, size, R(0.35f, hedgeOffset - 0.9f));
                var mat = colours[_rng.Next(colours.Length)];
                int n = 5 + _rng.Next(6);
                for (int i = 0; i < n; i++)
                {
                    Vector3 q = p + new Vector3(R(-0.35f, 0.35f), 0f, R(-0.35f, 0.35f));
                    AddBlob(mat, q + Vector3.up * R(0.05f, 0.14f), Vector3.one * R(0.05f, 0.08f));
                }
            }
        }

        private void BuildTrees(Vector3 origin, Vector2 size)
        {
            var bark = Mat("Bark", new Color(0.36f, 0.26f, 0.17f));
            var leaves = new[] { Mat("LeavesA", new Color(0.18f, 0.42f, 0.13f)), Mat("LeavesB", new Color(0.26f, 0.5f, 0.16f)) };
            for (int k = 0; k < treeCount; k++)
            {
                Vector3 p = PerimeterPoint(origin, size, hedgeOffset + R(5f, 26f));
                float h = R(5f, 9f);
                Add(bark, _cylinder, p, Quaternion.identity, new Vector3(0.35f, h * 0.62f, 0.35f));
                var leaf = leaves[_rng.Next(leaves.Length)];
                int blobs = 3 + _rng.Next(3);
                for (int i = 0; i < blobs; i++)
                {
                    float r = R(1.6f, 2.6f) * h / 7f;
                    Vector3 c = p + new Vector3(R(-1.2f, 1.2f), h * R(0.62f, 0.9f), R(-1.2f, 1.2f));
                    AddBlob(leaf, c, new Vector3(r * 1.2f, r, r * 1.2f));
                }
            }
        }

        private void BuildRocks(Vector3 origin, Vector2 size)
        {
            var rock = new[] { Mat("RockA", new Color(0.55f, 0.55f, 0.53f)), Mat("RockB", new Color(0.45f, 0.46f, 0.45f)) };
            for (int k = 0; k < rockCount; k++)
            {
                Vector3 p = PerimeterPoint(origin, size, hedgeOffset + R(2.5f, 18f));
                float s = R(0.35f, 1.1f);
                AddBlob(rock[_rng.Next(rock.Length)], p + Vector3.up * s * 0.25f, new Vector3(s * R(1f, 1.6f), s * 0.6f, s * R(1f, 1.5f)));
            }
        }

        private void BuildMountains(Vector3 centre)
        {
            var body = Mat("Mountain", new Color(0.42f, 0.5f, 0.62f));
            var bodyFar = Mat("MountainFar", new Color(0.55f, 0.63f, 0.74f));
            var snow = Mat("Snow", new Color(0.94f, 0.96f, 1f));
            for (int k = 0; k < mountainCount; k++)
            {
                float ang = (k + R(-0.3f, 0.3f)) / mountainCount * Mathf.PI * 2f;
                bool far = k % 2 == 1;
                float dist = mountainDistance * (far ? R(1.15f, 1.35f) : R(0.9f, 1.05f));
                Vector3 p = centre + new Vector3(Mathf.Cos(ang) * dist, -2f, Mathf.Sin(ang) * dist);
                float h = R(140f, 300f) * (far ? 1.25f : 1f), r = h * R(1.1f, 1.6f);
                var rot = Quaternion.Euler(0f, R(0f, 360f), 0f);
                Add(far ? bodyFar : body, _cone9, p, rot, new Vector3(r, h, r));
                // Snow cap: same apex, smaller cone.
                float capH = h * 0.24f;
                Add(snow, _cone9, p + Vector3.up * (h - capH + 0.5f), rot, new Vector3(r * 0.24f * 1.04f, capH, r * 0.24f * 1.04f));
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Random point around the lawn at the given distance outside its edge.</summary>
        private Vector3 PerimeterPoint(Vector3 origin, Vector2 size, float outside)
        {
            float perim = 2f * (size.x + size.y), t = R() * perim;
            if (t < size.x) return origin + new Vector3(t, 0f, -outside);
            t -= size.x;
            if (t < size.y) return origin + new Vector3(size.x + outside, 0f, t);
            t -= size.y;
            if (t < size.x) return origin + new Vector3(size.x - t, 0f, size.y + outside);
            t -= size.x;
            return origin + new Vector3(-outside, 0f, size.y - t);
        }

        private readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();
        private Material Mat(string name, Color c)
        {
            if (!_mats.TryGetValue(name, out var m)) _mats[name] = m = PipelineMaterials.CreateLit(name, c);
            return m;
        }

        private void AddBlob(Material m, Vector3 pos, Vector3 scale) =>
            Add(m, _blob, pos, Quaternion.Euler(0f, R(0f, 360f), 0f), scale);

        private void Add(Material m, Mesh mesh, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            if (!_buckets.TryGetValue(m, out var list)) _buckets[m] = list = new List<CombineInstance>();
            list.Add(new CombineInstance { mesh = mesh, transform = Matrix4x4.TRS(pos, rot, scale) });
        }

        private void FlushBuckets()
        {
            foreach (var kv in _buckets)
            {
                var combined = new Mesh { name = kv.Key.name + " (combined)", indexFormat = IndexFormat.UInt32 };
                combined.CombineMeshes(kv.Value.ToArray(), true, true);
                combined.RecalculateBounds();
                var go = new GameObject(kv.Key.name);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = combined;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = kv.Key;
                // Mountains are far beyond the shadow distance; small things don't need to cast.
                bool casts = kv.Key.name.StartsWith("Hedge") || kv.Key.name.StartsWith("Leaves") || kv.Key.name == "Bark";
                mr.shadowCastingMode = casts ? ShadowCastingMode.On : ShadowCastingMode.Off;
            }
            _buckets.Clear();
        }

        private GameObject Quad(string name, Vector3 centre, Vector2 size, Material mat)
        {
            var mesh = new Mesh { name = name };
            float hx = size.x * 0.5f, hz = size.y * 0.5f;
            mesh.vertices = new[] { new Vector3(-hx, 0, -hz), new Vector3(-hx, 0, hz), new Vector3(hx, 0, hz), new Vector3(hx, 0, -hz) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.position = centre;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        private void AddWall(Vector3 centre, Vector3 size)
        {
            var go = new GameObject("HedgeWall");
            go.transform.SetParent(transform, false);
            go.transform.position = centre;
            go.AddComponent<BoxCollider>().size = size;
        }

        /// <summary>Icosahedron subdivided once (80 tris), vertices jittered for a puffy organic blob.</summary>
        private Mesh Icosphere(int subdivisions, float jitter)
        {
            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            var v = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            var f = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };
            for (int s = 0; s < subdivisions; s++)
            {
                var cache = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (cache.TryGetValue(key, out int idx)) return idx;
                    v.Add((v[a] + v[b]) * 0.5f);
                    return cache[key] = v.Count - 1;
                }
                var nf = new List<int>(f.Count * 4);
                for (int i = 0; i < f.Count; i += 3)
                {
                    int a = f[i], b = f[i + 1], c = f[i + 2], ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    nf.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                f = nf;
            }
            var rng = new System.Random(77);
            for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized * (1f + ((float)rng.NextDouble() - 0.5f) * 2f * jitter);
            var m = new Mesh { name = "Blob" };
            m.SetVertices(v);
            m.SetTriangles(f, 0);
            m.RecalculateNormals();
            return m;
        }

        /// <summary>Unit cone: base radius 1 at y = 0, apex at y = 1.</summary>
        private static Mesh Cone(int sides)
        {
            var v = new List<Vector3> { Vector3.up };
            for (int i = 0; i < sides; i++)
            {
                float a = i / (float)sides * Mathf.PI * 2f;
                v.Add(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
            }
            var f = new List<int>();
            for (int i = 0; i < sides; i++) { f.Add(0); f.Add(1 + (i + 1) % sides); f.Add(1 + i); }
            var m = new Mesh { name = "Cone" + sides };
            m.SetVertices(v);
            m.SetTriangles(f, 0);
            m.RecalculateNormals();
            return m;
        }

        /// <summary>Unit cylinder: radius 1, y 0..1, no caps.</summary>
        private static Mesh Cylinder(int sides)
        {
            var v = new List<Vector3>();
            var f = new List<int>();
            for (int i = 0; i < sides; i++)
            {
                float a = i / (float)sides * Mathf.PI * 2f;
                v.Add(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
                v.Add(new Vector3(Mathf.Cos(a) * 0.7f, 1f, Mathf.Sin(a) * 0.7f));
            }
            for (int i = 0; i < sides; i++)
            {
                int a = i * 2, b = ((i + 1) % sides) * 2;
                f.AddRange(new[] { a, b + 1, a + 1, a, b, b + 1 });
            }
            var m = new Mesh { name = "Cylinder" };
            m.SetVertices(v);
            m.SetTriangles(f, 0);
            m.RecalculateNormals();
            return m;
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
    }
}

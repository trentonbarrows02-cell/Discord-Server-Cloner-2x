using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PlayShift.Grass
{
    /// <summary>
    /// Open-field map around the lawn, built at runtime: flat ground, the lawn shaded by PlayShift/GrassGround (shows
    /// grown/cut grass from the cell data, so distant progress is visible without drawing blades), a wooden privacy
    /// fence (posts, rails, boards, top cap) on all four sides, a natural forest beyond it (see
    /// OpenFieldEnvironment.Forest; models in Resources/OpenField), bushes, rocks and flowers, rolling wooded hills
    /// to the horizon, soft haze and sunlight. Small things are merged into a handful of static meshes (one per
    /// material and direction, so the ones behind the camera are culled), trees and bushes included (full models near,
    /// light models far, no runtime LOD switching). Scenery uses the matte PlayShift/Foliage shader.
    /// </summary>
    public sealed partial class OpenFieldEnvironment : MonoBehaviour
    {
        [SerializeField] private GrassLevelManager manager;
        [Tooltip("Material using PlayShift/GrassGround (assigned by the setup menu so the shader ships in builds).")]
        [SerializeField] private Material lawnMaterial;
        [SerializeField] private int seed = 4242;

        [Header("Fence")]
        [Tooltip("Distance from the lawn edge to the fence line (m). The invisible wall keeps the player 0.6 m out.")]
        [SerializeField] private float fenceOffset = 1.0f;
        [SerializeField] private float fenceHeight = 1.5f;

        [Header("Scenery")]
        [Tooltip("Average distance between trees along each of the four forest rows (m).")]
        [SerializeField] private float treeSpacing = 4.6f;
        [Tooltip("Average distance between bushes in the row right behind the fence (m).")]
        [SerializeField] private float bushSpacing = 1.9f;
        [SerializeField] private int rockCount = 44;
        [SerializeField] private int flowerClusters = 60;

        [Header("Fence detail")]
        [Tooltip("Distance between fence posts (m). Each side gets a whole number of equal bays.")]
        [SerializeField] private float postSpacing = 2.4f;

        [Header("Hills and sky")]
        [Tooltip("How far the rolling hills reach (m from the yard centre).")]
        [SerializeField] private float hillsRadius = 950f;
        [SerializeField] private Color hillGrass = new Color(0.36f, 0.5f, 0.22f);
        [SerializeField] private Color hillForest = new Color(0.15f, 0.29f, 0.12f);
        [Tooltip("Colour far hills fade to (aerial haze); also the fog colour.")]
        [SerializeField] private Color hazeColour = new Color(0.68f, 0.78f, 0.88f);
        [Tooltip("Linear fog: starts and ends (m from the camera). Light, so the yard stays crisp.")]
        [SerializeField] private float fogStart = 110f;
        [SerializeField] private float fogEnd = 900f;

        [Header("Sunlight")]
        [SerializeField] private bool applyLighting = true;
        [SerializeField] private Color sunColour = new Color(1f, 0.965f, 0.9f);
        [SerializeField] private float sunIntensity = 1.25f;
        [Tooltip("Shadow darkness (lower = softer, more light in the shade).")]
        [SerializeField, Range(0f, 1f)] private float shadowStrength = 0.6f;

        private System.Random _rng;
        private float R() => (float)_rng.NextDouble();
        private float R(float a, float b) => a + R() * (b - a);

        // Merge buckets, one combined mesh per material and group (group = side of the field, so it can be culled).
        private readonly Dictionary<(Material, int), List<CombineInstance>> _buckets = new Dictionary<(Material, int), List<CombineInstance>>();
        private Mesh _blob, _cone9;

        private void Start()
        {
            if (manager == null) manager = GrassLevelManager.Instance;
            _rng = new System.Random(seed);
            Vector2 size = manager != null && manager.Settings != null ? manager.Settings.fieldSizeMeters : new Vector2(100f, 100f);
            Vector3 origin = manager != null ? manager.FieldTransform.position : Vector3.zero;

            _blob = Icosphere(1, 0.12f);
            _cone9 = Cone(9);

            BuildGround(origin, size);
            BuildFence(origin, size);
            BuildFlowers(origin, size);
            BuildForest(origin, size);
            BuildRocks(origin, size);
            BuildHills(origin, size);
            FlushBuckets();
            ApplyAtmosphere();
        }

        // ------------------------------------------------------------------ pieces

        private void BuildGround(Vector3 origin, Vector2 size)
        {
            var lawnTex = Speckle(512, new Color32(54, 112, 30, 255), new[] { "4a9426", "3a7a1e", "55a02c", "2f6619", "5fa834", "44861f" }, 16000, 0.6f, 2.2f);
            // Ground outside the lawn (the strip by the fence, under the hedge): the same turf greens as the lawn.
            var meadowTex = Speckle(512, new Color32(56, 106, 32, 255), new[] { "4a8a26", "3a721e", "52962c", "2f6019", "5a9c32" }, 12000, 0.7f, 2.4f);

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

            // Keep the player off the fence: the walls' inner faces sit 0.6 m outside the lawn, at the front of the
            // foliage (they used to be in the middle of it, so the camera could walk 1.5 m into the leaves). The
            // trimmer is stopped by these walls too, and from 0.6 m out its blades still reach every edge cell.
            const float wallFace = 0.6f, wallThick = 0.6f;
            float w = wallFace + wallThick * 0.5f, span = 2f * (fenceOffset + 1f);
            AddWall(origin + new Vector3(size.x * 0.5f, 1f, -w), new Vector3(size.x + span, 2f, wallThick));
            AddWall(origin + new Vector3(size.x * 0.5f, 1f, size.y + w), new Vector3(size.x + span, 2f, wallThick));
            AddWall(origin + new Vector3(-w, 1f, size.y * 0.5f), new Vector3(wallThick, 2f, size.y + span));
            AddWall(origin + new Vector3(size.x + w, 1f, size.y * 0.5f), new Vector3(wallThick, 2f, size.y + span));
        }

        private static Mesh LoadMesh(string file, string name)
        {
            var all = Resources.LoadAll<Mesh>("OpenField/" + file);
            foreach (var m in all) if (m.name == name) return m;
            Debug.LogWarning($"[PlayShift Grass] Mesh '{name}' not found in Resources/OpenField/{file} ({all.Length} meshes).");
            return null;
        }

        private Material TexturedMat(string name, string texture)
        {
            if (_mats.TryGetValue(name, out var m)) return m;
            var tex = Resources.Load<Texture2D>("OpenField/" + texture);
            _mats[name] = m = PipelineMaterials.CreateLit(name, Color.white, tex);
            return m;
        }

        /// <summary>
        /// Wooden privacy fence all around the lawn, built from modular pieces with world-continuous wood UVs (no seams):
        /// square posts at equal bays (one at every corner), a continuous wall of boards on the outer side of the posts,
        /// two horizontal rails on the yard side, and a top cap over everything. Corners overlap so they close. One
        /// mesh per side, sharing one wood material. Height is the same everywhere.
        /// </summary>
        private void BuildFence(Vector3 origin, Vector2 size)
        {
            var mat = FoliageMat("Fence", WoodTexture(256), Color.white);
            float o = fenceOffset, H = fenceHeight;
            Vector3 centre = origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f);
            var corners = new[]
            {
                origin + new Vector3(-o, 0f, -o), origin + new Vector3(size.x + o, 0f, -o),
                origin + new Vector3(size.x + o, 0f, size.y + o), origin + new Vector3(-o, 0f, size.y + o),
            };
            for (int side = 0; side < 4; side++)
            {
                Vector3 a = corners[side], c = corners[(side + 1) % 4];
                Vector3 dir = (c - a).normalized, inward = Vector3.Cross(Vector3.up, dir);
                if (Vector3.Dot(inward, centre - a) < 0f) inward = -inward;
                float len = Vector3.Distance(a, c);
                int bays = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.5f, postSpacing)));
                float bay = len / bays;
                var mb = new BoxBuilder(a, dir, inward, H);
                // Boards: one continuous wall just outside the post line, a little longer than the side so corners close.
                mb.Box(len * 0.5f, 0.03f + (H - 0.03f) * 0.5f, -0.05f, len * 0.5f + 0.07f, (H - 0.03f) * 0.5f, 0.012f);
                // Posts at every bay end that starts this side (the next side adds the far corner post).
                for (int i = 0; i < bays; i++)
                    mb.Box(bay * i, (H + 0.08f) * 0.5f, 0f, 0.06f, (H + 0.08f) * 0.5f, 0.06f);
                // Two rails on the yard side of the boards, and the top cap.
                mb.Box(len * 0.5f, 0.32f, -0.018f, len * 0.5f, 0.045f, 0.02f);
                mb.Box(len * 0.5f, H - 0.26f, -0.018f, len * 0.5f, 0.045f, 0.02f);
                mb.Box(len * 0.5f, H + 0.095f, -0.02f, len * 0.5f + 0.09f, 0.015f, 0.085f);
                var go = new GameObject("Fence " + side);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mb.ToMesh("Fence " + side);
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
        }

        /// <summary>
        /// Boxes along one fence side, in that side's frame (x along the side from its start, y up, z towards the yard),
        /// with wood UVs continuous across pieces: board faces map 0.6 m of length and the fence height to the texture.
        /// </summary>
        private sealed class BoxBuilder
        {
            private readonly Vector3 _a, _x, _z;
            private readonly float _h;
            private readonly List<Vector3> _v = new List<Vector3>();
            private readonly List<Vector3> _n = new List<Vector3>();
            private readonly List<Vector2> _uv = new List<Vector2>();
            private readonly List<int> _t = new List<int>();
            private const float PlankRepeat = 0.6f; // metres of fence per texture width (4 boards)

            public BoxBuilder(Vector3 start, Vector3 along, Vector3 inward, float height) { _a = start; _x = along; _z = inward; _h = height; }

            public void Box(float cx, float cy, float cz, float hx, float hy, float hz)
            {
                Vector3 P(float x, float y, float z) => _a + _x * x + Vector3.up * y + _z * z;
                float x0 = cx - hx, x1 = cx + hx, y0 = cy - hy, y1 = cy + hy, z0 = cz - hz, z1 = cz + hz;
                // Yard side / outer side (boards): u along the side, v up the fence.
                Face(P(x0, y0, z1), P(x0, y1, z1), P(x1, y1, z1), P(x1, y0, z1), _z, x0, x1, y0, y1, true);
                Face(P(x1, y0, z0), P(x1, y1, z0), P(x0, y1, z0), P(x0, y0, z0), -_z, x1, x0, y0, y1, true);
                // Top / bottom: u along, v across (thin grain strip).
                Face(P(x0, y1, z1), P(x0, y1, z0), P(x1, y1, z0), P(x1, y1, z1), Vector3.up, x0, x1, z1, z0, false);
                Face(P(x0, y0, z0), P(x0, y0, z1), P(x1, y0, z1), P(x1, y0, z0), Vector3.down, x0, x1, z0, z1, false);
                // Ends: u across, v up.
                Face(P(x0, y0, z0), P(x0, y1, z0), P(x0, y1, z1), P(x0, y0, z1), -_x, z0, z1, y0, y1, true);
                Face(P(x1, y0, z1), P(x1, y1, z1), P(x1, y1, z0), P(x1, y0, z0), _x, z1, z0, y0, y1, true);
            }

            private void Face(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal, float u0, float u1, float v0, float v1, bool upright)
            {
                int i = _v.Count;
                _v.Add(p0); _v.Add(p1); _v.Add(p2); _v.Add(p3);
                for (int k = 0; k < 4; k++) _n.Add(normal);
                float su = 1f / PlankRepeat, sv = upright ? 1f / _h : su;
                _uv.Add(new Vector2(u0 * su, v0 * sv)); _uv.Add(new Vector2(u0 * su, v1 * sv));
                _uv.Add(new Vector2(u1 * su, v1 * sv)); _uv.Add(new Vector2(u1 * su, v0 * sv));
                // Front face = the side the normal points to (Unity's face normal is Cross(b - a, c - a)).
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), normal) >= 0f)
                { _t.Add(i); _t.Add(i + 1); _t.Add(i + 2); _t.Add(i); _t.Add(i + 2); _t.Add(i + 3); }
                else
                { _t.Add(i); _t.Add(i + 2); _t.Add(i + 1); _t.Add(i); _t.Add(i + 3); _t.Add(i + 2); }
            }

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(_v);
                m.SetNormals(_n);
                m.SetUVs(0, _uv);
                m.SetTriangles(_t, 0);
                m.RecalculateBounds();
                m.RecalculateTangents();
                return m;
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
                // Behind the fence, in front of the bushes (nothing grows inside the yard).
                Vector3 p = PerimeterPoint(origin, size, fenceOffset + R(0.35f, 0.9f));
                var mat = colours[_rng.Next(colours.Length)];
                int n = 5 + _rng.Next(6);
                for (int i = 0; i < n; i++)
                {
                    Vector3 q = p + new Vector3(R(-0.35f, 0.35f), 0f, R(-0.35f, 0.35f));
                    AddBlob(mat, q + Vector3.up * R(0.05f, 0.14f), Vector3.one * R(0.05f, 0.08f));
                }
            }
        }

        private void BuildRocks(Vector3 origin, Vector2 size)
        {
            var rock = new[] { FoliageMat("RockA", null, new Color(0.55f, 0.55f, 0.52f)), FoliageMat("RockB", null, new Color(0.46f, 0.47f, 0.45f)) };
            for (int k = 0; k < rockCount; k++)
            {
                if (!BandPoint(size, fenceOffset + 1.2f, fenceOffset + 22f, out float x, out float z)) continue;
                Vector3 p = origin + new Vector3(x, 0f, z);
                float s = R(0.3f, 1.2f);
                AddBlob(rock[_rng.Next(rock.Length)], p + Vector3.up * s * 0.22f, new Vector3(s * R(1f, 1.6f), s * R(0.5f, 0.75f), s * R(1f, 1.5f)));
            }
        }

        /// <summary>
        /// Rolling hills from just past the forest out to the horizon: one ring mesh (a few thousand triangles) with
        /// smooth heights, darker wooded patches (the forest continues up the near slopes) and aerial haze baked into the
        /// vertex colours. Replaces the old cone mountains. Receives shadows, casts none.
        /// </summary>
        private void BuildHills(Vector3 origin, Vector2 size)
        {
            Vector3 centre = origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f);
            float r0 = Mathf.Max(size.x, size.y) * 0.5f * 1.41f + fenceOffset + forestDepth + 4f, r1 = hillsRadius;
            const int seg = 180, rings = 34;
            var v = new List<Vector3>(seg * rings);
            var col = new List<Color>(seg * rings);
            var uv = new List<Vector2>(seg * rings);
            float ph1 = R(0f, 6.28f), ph2 = R(0f, 6.28f), ph3 = R(0f, 6.28f);
            for (int j = 0; j < rings; j++)
            {
                float t = j / (float)(rings - 1), r = r0 + (r1 - r0) * t * t;
                for (int i = 0; i < seg; i++)
                {
                    float a = i / (float)seg * Mathf.PI * 2f;
                    // Rolling hills: layered 2D value noise over the ground plane (rounded hills behind one another,
                    // no ridged walls), low near the forest and only gently higher far away.
                    float wx = Mathf.Cos(a) * r, wz = Mathf.Sin(a) * r;
                    float n1 = HillNoise(wx / 170f + ph1, wz / 170f) * 0.62f + HillNoise(wx / 75f, wz / 75f + ph2) * 0.28f
                               + HillNoise(wx / 32f + ph3, wz / 32f) * 0.10f;
                    float n2 = HillNoise(wx / 420f + ph2, wz / 420f + ph1);
                    float n3 = HillNoise(wx / 60f + 11f, wz / 60f - 7f);
                    float rise = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(r0, r0 + 140f, r));
                    float far = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(r0 + 160f, r1, r));
                    float h = rise * (3f + 22f * n1 * n1) + far * (8f + 42f * n2 * n1);
                    v.Add(centre + new Vector3(Mathf.Cos(a) * r, h - 0.4f, Mathf.Sin(a) * r));
                    float wooded = Mathf.Max(Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.42f, 0.6f, n3)), 1f - Mathf.InverseLerp(r0, r0 + 80f, r));
                    Color c = Color.Lerp(hillGrass, hillForest, wooded);
                    float haze = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(r0 + 60f, r1, r)) * 0.82f;
                    col.Add(Color.Lerp(c, hazeColour, haze));
                    uv.Add(new Vector2(a, r * 0.01f));
                }
            }
            var tri = new List<int>(seg * (rings - 1) * 6);
            for (int j = 0; j < rings - 1; j++)
                for (int i = 0; i < seg; i++)
                {
                    int a0 = j * seg + i, a1 = j * seg + (i + 1) % seg, b0 = a0 + seg, b1 = a1 + seg;
                    // Rings grow outwards and angles anticlockwise seen from above: this winding faces up.
                    tri.Add(a0); tri.Add(a1); tri.Add(b0);
                    tri.Add(a1); tri.Add(b1); tri.Add(b0);
                }
            var mesh = new Mesh { name = "Hills", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(v);
            mesh.SetColors(col);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var mat = FoliageMat("Hills", null, Color.white);
            mat.SetFloat("_VertexColor", 1f);
            mat.SetFloat("_Wrap", 0.55f);
            var go = new GameObject("Hills");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        /// <summary>Soft natural sunlight and a light distance haze (scene-wide; set once at start).</summary>
        private void ApplyAtmosphere()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = hazeColour;
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogEnd;
            if (!applyLighting) return;
            Light sun = RenderSettings.sun;
            if (sun == null)
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { sun = l; break; }
            if (sun == null) return;
            sun.color = sunColour;
            sun.intensity = sunIntensity;
            sun.shadowStrength = shadowStrength;
        }

        /// <summary>Matte scenery material (PlayShift/Foliage: instanced, LOD cross-fade, fog). Falls back to the
        /// pipeline's lit material when the shader is missing.</summary>
        /// <summary>Smooth 2D value noise (0..1) for the hills.</summary>
        private static float HillNoise(float x, float z)
        {
            float ix = Mathf.Floor(x), iz = Mathf.Floor(z);
            float fx = x - ix, fz = z - iz;
            fx = fx * fx * (3f - 2f * fx); fz = fz * fz * (3f - 2f * fz);
            float H(float a, float b) { float h = Mathf.Sin(a * 127.1f + b * 311.7f) * 43758.5453f; return h - Mathf.Floor(h); }
            float v00 = H(ix, iz), v10 = H(ix + 1f, iz), v01 = H(ix, iz + 1f), v11 = H(ix + 1f, iz + 1f);
            return Mathf.Lerp(Mathf.Lerp(v00, v10, fx), Mathf.Lerp(v01, v11, fx), fz);
        }

        private Material FoliageMat(string name, Texture tex, Color tint)
        {
            if (_mats.TryGetValue(name, out var m)) return m;
            var sh = Shader.Find("PlayShift/Foliage");
            if (sh != null)
            {
                m = new Material(sh) { name = name, enableInstancing = true };
                if (tex != null) m.mainTexture = tex;
                m.color = tint;
            }
            else
            {
                m = PipelineMaterials.CreateLit(name, tint, tex as Texture2D);
                m.enableInstancing = true;
            }
            _mats[name] = m;
            return m;
        }

        /// <summary>Weathered wood planks: four vertical boards with thin dark gaps, per-board colour, long grain and
        /// a few knots. Tiles seamlessly.</summary>
        private Texture2D WoodTexture(int size)
        {
            var px = new Color32[size * size];
            int planks = 4, pw = size / planks;
            var tones = new Color[planks];
            for (int k = 0; k < planks; k++)
            {
                float v = R(-0.035f, 0.035f);
                tones[k] = new Color(0.47f + v, 0.36f + v * 0.9f, 0.25f + v * 0.7f); // warm brown, slightly weathered
            }
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int k = x / pw, lx = x - k * pw;
                    float g = 0.06f * Mathf.Sin(lx * 0.9f + 3f * Mathf.Sin(y * 0.045f + k * 1.7f)) + 0.035f * Mathf.Sin(lx * 2.3f + y * 0.02f + k);
                    Color c = tones[k] * (1f + g);
                    if (lx < 2 || lx >= pw - 1) c = new Color(0.16f, 0.11f, 0.07f); // gap between boards
                    else if (lx < 4 || lx >= pw - 3) c *= 0.86f;                     // board edge
                    px[y * size + x] = c;
                }
            // Knots.
            for (int n = 0; n < 6; n++)
            {
                float cx = R(0f, size), cy = R(0f, size), rx = R(2f, 4f), ry = rx * R(1.6f, 2.4f);
                for (int y = Mathf.FloorToInt(cy - ry); y <= cy + ry; y++)
                    for (int x = Mathf.FloorToInt(cx - rx); x <= cx + rx; x++)
                    {
                        float dx = (x - cx) / rx, dy = (y - cy) / ry, d = dx * dx + dy * dy;
                        if (d > 1f) continue;
                        int ix = (x % size + size) % size, iy = (y % size + size) % size;
                        if ((ix % pw) < 3 || (ix % pw) >= pw - 2) continue;
                        Color c = px[iy * size + ix];
                        px[iy * size + ix] = Color.Lerp(c, c * 0.55f, 1f - d);
                    }
            }
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "FenceWood", wrapMode = TextureWrapMode.Repeat, anisoLevel = 8 };
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Random point around the lawn at the given distance outside its edge.</summary>
        private Vector3 PerimeterPoint(Vector3 origin, Vector2 size, float outside) => PerimeterPoint(origin, size, outside, out _);

        /// <summary>Random point <paramref name="outside"/> metres outside the lawn edge; side 0..3 = which edge.</summary>
        private Vector3 PerimeterPoint(Vector3 origin, Vector2 size, float outside, out int side)
        {
            float perim = 2f * (size.x + size.y), t = R() * perim;
            side = 0;
            if (t < size.x) return origin + new Vector3(t, 0f, -outside);
            t -= size.x;
            side = 1;
            if (t < size.y) return origin + new Vector3(size.x + outside, 0f, t);
            t -= size.y;
            side = 2;
            if (t < size.x) return origin + new Vector3(size.x - t, 0f, size.y + outside);
            t -= size.x;
            side = 3;
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

        private void Add(Material m, Mesh mesh, Vector3 pos, Quaternion rot, Vector3 scale, int group = 0)
        {
            if (!_buckets.TryGetValue((m, group), out var list)) _buckets[(m, group)] = list = new List<CombineInstance>();
            list.Add(new CombineInstance { mesh = mesh, transform = Matrix4x4.TRS(pos, rot, scale) });
        }

        private void FlushBuckets()
        {
            foreach (var kv in _buckets)
            {
                var mat = kv.Key.Item1;
                var combined = new Mesh { name = $"{mat.name} {kv.Key.Item2} (combined)", indexFormat = IndexFormat.UInt32 };
                combined.CombineMeshes(kv.Value.ToArray(), true, true);
                combined.RecalculateBounds();
                var go = new GameObject($"{mat.name} {kv.Key.Item2}");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = combined;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                // Mountains are far beyond the shadow distance; small things don't need to cast.
                bool casts = mat.name == "Fence" || mat.name == "ForestNear";
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
            var go = new GameObject("FenceWall");
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

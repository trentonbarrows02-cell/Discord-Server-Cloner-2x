using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PlayShift.Grass
{
    /// <summary>
    /// The forest round the Open Field: the yard reads as a big private backyard closed in by woods.
    ///  - Four staggered rows of trees (8-15 m) all round the fence, on paths that run parallel to the fence and turn
    ///    round each corner in a quarter circle, so corners are exactly as dense as the sides. Spacing, row
    ///    distance, kind, height, width and turn all vary, so nothing lines up.
    ///  - Bushes in two rows right behind the fence, and undergrowth between the tree rows.
    ///  - A backdrop of flat tree cards two rows deep behind the last trees, and a darker forest floor, so no open
    ///    meadow shows through the trunks.
    /// Performance: each tree is a LODGroup (full model near, lighter model mid-distance, a cross-shaped card far
    /// away) drawn with GPU-instanced materials (Resources/OpenField/TreesLit, TreeBillboards); bushes drop to a
    /// lighter model then disappear; cards and backdrop cast no shadows; no colliders, light or reflection probes.
    /// Nothing is placed inside the fence.
    /// </summary>
    public sealed partial class OpenFieldEnvironment
    {
        private static readonly string[] TreeKinds = { "TreeRound", "TreePine", "TreeSmall", "TreeTall" };
        private static readonly Vector2[] TreeHeights =
            { new Vector2(9f, 12.5f), new Vector2(10.5f, 15f), new Vector2(8f, 10.5f), new Vector2(11f, 15f) };
        private static readonly float[] TreeWeights = { 0.28f, 0.30f, 0.17f, 0.25f };

        private struct Plant
        {
            public Mesh Lod0, Lod1, Billboard;
            public Bounds Bounds;
            public string Name;
        }

        private Material _litMat, _billboardMat;
        private int _treeCount, _bushCount;
        private Transform _forest;

        private void BuildForest(Vector3 origin, Vector2 size)
        {
            _litMat = Resources.Load<Material>("OpenField/TreesLit");
            if (_litMat == null) { _litMat = TexturedMat("TreesLitFallback", "Plants_BaseColor"); _litMat.enableInstancing = true; }
            _billboardMat = Resources.Load<Material>("OpenField/TreeBillboards");
            if (_billboardMat == null)
            {
                _billboardMat = PipelineMaterials.CreateLit("TreeBillboardsFallback", Color.white,
                    Resources.Load<Texture2D>("OpenField/TreeBillboards"), alphaClip: true, cutoff: 0.5f);
                _billboardMat.enableInstancing = true;
            }

            var trees = new Plant[TreeKinds.Length];
            for (int i = 0; i < trees.Length; i++)
            {
                trees[i] = LoadPlant(TreeKinds[i]);
                if (trees[i].Lod0 == null) return;
                trees[i].Billboard = TreeBillboardMesh(trees[i].Bounds, i, true);
            }
            var bushes = new[] { LoadPlant("BushCluster"), LoadPlant("BushLong") };
            if (bushes[0].Lod0 == null || bushes[1].Lod0 == null) return;

            _forest = new GameObject("Forest").transform;
            _forest.SetParent(transform, false);
            float f = fenceOffset;

            // Trees: four staggered rows, the rows offset along the fence so trunks never line up front to back.
            float[] rows = { f + 5.0f, f + 8.6f, f + 12.2f, f + 15.8f };
            for (int r = 0; r < rows.Length; r++)
            {
                float d = rows[r], len = PathLength(size, d);
                for (float t = R(0f, treeSpacing) + r * treeSpacing * 0.5f, end = t + len - treeSpacing * 0.4f; t < end;
                     t += treeSpacing * R(0.7f, 1.3f))
                {
                    Vector3 p = PathPoint(origin, size, d + R(-1.3f, 1.3f), t, out _);
                    int k = PickTree();
                    float s = R(TreeHeights[k].x, TreeHeights[k].y) / trees[k].Bounds.size.y, w = R(0.88f, 1.12f);
                    PlacePlant(trees[k], p, Quaternion.Euler(0f, R(0f, 360f), 0f), new Vector3(s * w, s, s * w * R(0.92f, 1.08f)), true);
                }
            }

            // Bushes: two close rows right behind the fence, then undergrowth between the tree rows. The garden bushes
            // are low and wide, so each lies along the fence with its depth capped (it never reaches through the
            // fence) and is stretched up a little (at most 2.4x) towards its height.
            void BushRow(float d, float spacing, float jitter, float hMin, float hMax, float maxDepth)
            {
                float len = PathLength(size, d);
                for (float t = R(0f, spacing), end = t + len - spacing * 0.4f; t < end; t += spacing * R(0.7f, 1.3f))
                {
                    Vector3 p = PathPoint(origin, size, d + R(-jitter, jitter), t, out Vector3 n);
                    var bush = bushes[_rng.Next(bushes.Length)];
                    Bounds b = bush.Bounds;
                    float sh = Mathf.Min(R(hMin, hMax) / b.size.y, maxDepth / b.size.z), sv = Mathf.Min(R(hMin, hMax) / b.size.y, sh * 2.4f);
                    var rot = Quaternion.LookRotation(n, Vector3.up) * Quaternion.Euler(0f, R(-15f, 15f) + (R() < 0.5f ? 180f : 0f), 0f);
                    PlacePlant(bush, p, rot, new Vector3(sh * R(0.85f, 1.15f), sv, sh), false);
                }
            }
            BushRow(f + 1.3f, bushSpacing, 0.2f, 1.4f, 2.0f, 1.5f);
            BushRow(f + 3.0f, bushSpacing * 1.4f, 0.4f, 1.3f, 2.2f, 2.0f);
            BushRow(f + 6.8f, bushSpacing * 2.6f, 1.0f, 1.4f, 2.6f, 3.0f);
            BushRow(f + 10.4f, bushSpacing * 2.6f, 1.0f, 1.4f, 2.6f, 3.0f);
            BushRow(f + 14.0f, bushSpacing * 2.6f, 1.0f, 1.4f, 2.6f, 3.0f);

            // Backdrop: two rows of tree cards behind the last trees (crossed, with a canopy card on top so they also
            // read from the title screen's aerial view), merged, one mesh per side.
            for (int r = 0; r < 2; r++)
            {
                float d = f + 19.6f + r * 3.4f, len = PathLength(size, d), spacing = treeSpacing * 0.8f;
                for (float t = R(0f, spacing) + r * spacing * 0.5f, end = t + len - spacing * 0.4f; t < end; t += spacing * R(0.75f, 1.25f))
                {
                    Vector3 p = PathPoint(origin, size, d + R(-1f, 1f), t, out Vector3 n);
                    int k = PickTree();
                    var b = trees[k].Bounds;
                    float s = R(TreeHeights[k].x, TreeHeights[k].y) * 1.1f / b.size.y;
                    var rot = Quaternion.LookRotation(-n, Vector3.up) * Quaternion.Euler(0f, R(-25f, 25f), 0f);
                    Vector3 off = new Vector3(b.center.x, b.min.y, b.center.z) * s;
                    Add(_billboardMat, trees[k].Billboard, p - rot * off, rot, Vector3.one * s, SideOf(n));
                }
            }

            BuildForestFloor(origin, size, f + 0.4f, f + 27f);
            Debug.Log($"[PlayShift Grass] Forest: {_treeCount} trees (4 rows), {_bushCount} bushes, backdrop cards behind.");
        }

        private int PickTree()
        {
            float x = R(), acc = 0f;
            for (int i = 0; i < TreeWeights.Length; i++) { acc += TreeWeights[i]; if (x < acc) return i; }
            return TreeWeights.Length - 1;
        }

        private static Plant LoadPlant(string name)
        {
            var p = new Plant { Name = name, Lod0 = LoadMesh("Plants", name), Lod1 = LoadMesh("Plants", name + "_LOD1") };
            if (p.Lod1 == null) p.Lod1 = p.Lod0;
            if (p.Lod0 != null) p.Bounds = p.Lod0.bounds;
            return p;
        }

        /// <summary>
        /// One tree or bush at p (base centre, sunk a little into the ground), turned and scaled. Trees: full model
        /// near, lighter model mid-distance, card far away. Bushes: full, lighter, then nothing.
        /// </summary>
        private void PlacePlant(in Plant plant, Vector3 p, Quaternion rot, Vector3 scale, bool tree)
        {
            Bounds b = plant.Bounds;
            var go = new GameObject(plant.Name);
            var t = go.transform;
            t.SetParent(_forest, false);
            t.SetPositionAndRotation(p - Vector3.up * (0.006f * b.size.y * scale.y), rot);
            t.localScale = scale;
            Vector3 off = -new Vector3(b.center.x, b.min.y, b.center.z);
            // Shadows only from near trees (the lighter models are mostly past the shadow distance; bushes sit in the
            // trees' shade anyway).
            var lod0 = PlantPart(t, "LOD0", plant.Lod0, _litMat, off, tree ? ShadowCastingMode.On : ShadowCastingMode.Off);
            var lod1 = PlantPart(t, "LOD1", plant.Lod1, _litMat, off, ShadowCastingMode.Off);
            if (tree) _treeCount++; else _bushCount++;
            var lg = go.AddComponent<LODGroup>();
            if (tree)
            {
                var card = PlantPart(t, "Card", plant.Billboard, _billboardMat, off, ShadowCastingMode.Off);
                // Screen height share: full model within ~40 m, lighter to ~75 m, card beyond (never culled).
                lg.SetLODs(new[] { new LOD(0.22f, new[] { lod0 }), new LOD(0.12f, new[] { lod1 }), new LOD(0.0015f, new[] { card }) });
            }
            else
                lg.SetLODs(new[] { new LOD(0.05f, new[] { lod0 }), new LOD(0.012f, new[] { lod1 }) });
            lg.RecalculateBounds();
        }

        private static Renderer PlantPart(Transform parent, string name, Mesh mesh, Material mat, Vector3 offset, ShadowCastingMode shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = offset;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = shadows;
            mr.receiveShadows = true;
            mr.lightProbeUsage = LightProbeUsage.Off;       // per-object probes would split the instanced batches
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
            return mr;
        }

        /// <summary>
        /// Tree card from the billboard atlas (Resources/OpenField/TreeBillboards: top half side views, bottom half top
        /// views, one quarter-wide column per kind; rendered from the same models with the framing used here). In the
        /// model's own space so it swaps in for the full model. Cross: two crossed double-sided cards plus a canopy
        /// card seen from above. Single: one card facing +z (backdrop). Normals point up so every card is lit evenly.
        /// </summary>
        private static Mesh TreeBillboardMesh(Bounds b, int kind, bool cross)
        {
            float H = b.size.y, S = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) * 1.02f;
            float St = Mathf.Max(b.size.x, b.size.z) * 1.02f;
            Vector3 c = new Vector3(b.center.x, b.min.y + H * 0.5f, b.center.z);
            float u0 = kind * 0.25f, h = S * 0.5f;
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();
            void Quad(Vector3 a, Vector3 bb, Vector3 cc, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, bool twoSided)
            {
                int i = v.Count;
                v.Add(a); v.Add(bb); v.Add(cc); v.Add(d);
                uv.Add(ua); uv.Add(ub); uv.Add(uc); uv.Add(ud);
                tri.Add(i); tri.Add(i + 1); tri.Add(i + 2); tri.Add(i); tri.Add(i + 2); tri.Add(i + 3);
                if (twoSided) { tri.Add(i); tri.Add(i + 2); tri.Add(i + 1); tri.Add(i); tri.Add(i + 3); tri.Add(i + 2); }
            }
            // Side picture: left/right edges of the tile, bottom/top half of the atlas' upper half.
            Vector2 sl0 = new Vector2(u0, 0.5f), sl1 = new Vector2(u0, 1f), sr1 = new Vector2(u0 + 0.25f, 1f), sr0 = new Vector2(u0 + 0.25f, 0.5f);
            // Card in the x/y plane, front facing +z: the picture was rendered looking along -z (Unity flips the model's
            // x on import), where +x is on the left, so the tile's left edge goes on +x.
            Quad(c + new Vector3(h, -h, 0), c + new Vector3(h, h, 0), c + new Vector3(-h, h, 0), c + new Vector3(-h, -h, 0),
                 sl0, sl1, sr1, sr0, cross);
            if (cross)
            {
                Quad(c + new Vector3(0, -h, -h), c + new Vector3(0, h, -h), c + new Vector3(0, h, h), c + new Vector3(0, -h, h),
                     sl0, sl1, sr1, sr0, true);
                // Canopy from above, at the upper canopy (image up = -z, image right = -x).
                float y = b.min.y + H * 0.78f, ht = St * 0.5f;
                Quad(new Vector3(c.x + ht, y, c.z + ht), new Vector3(c.x + ht, y, c.z - ht), new Vector3(c.x - ht, y, c.z - ht), new Vector3(c.x - ht, y, c.z + ht),
                     new Vector2(u0, 0f), new Vector2(u0, 0.5f), new Vector2(u0 + 0.25f, 0.5f), new Vector2(u0 + 0.25f, 0f), false);
            }
            var n = new List<Vector3>(v.Count);
            for (int i = 0; i < v.Count; i++) n.Add(Vector3.up);
            var m = new Mesh { name = TreeKinds[kind] + (cross ? "_Billboard" : "_Card") };
            m.SetVertices(v);
            m.SetNormals(n);
            m.SetUVs(0, uv);
            m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }

        // ---- path round the yard: parallel to the fence at distance d from the lawn edge, quarter circles at corners

        private static float PathLength(Vector2 size, float d) => 2f * (size.x + size.y) + 2f * Mathf.PI * d;

        private static Vector3 PathPoint(Vector3 origin, Vector2 size, float d, float t, out Vector3 outward)
        {
            float sx = size.x, sz = size.y, arc = Mathf.PI * 0.5f * d;
            t = Mathf.Repeat(t, 2f * (sx + sz) + 4f * arc);
            Vector3 Corner(float cx, float cz, float a0, float u, out Vector3 n)
            {
                float a = (a0 + 90f * u) * Mathf.Deg2Rad;
                n = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                return origin + new Vector3(cx, 0f, cz) + n * d;
            }
            if (t < sx) { outward = Vector3.back; return origin + new Vector3(t, 0f, -d); }
            t -= sx;
            if (t < arc) return Corner(sx, 0f, -90f, t / arc, out outward);
            t -= arc;
            if (t < sz) { outward = Vector3.right; return origin + new Vector3(sx + d, 0f, t); }
            t -= sz;
            if (t < arc) return Corner(sx, sz, 0f, t / arc, out outward);
            t -= arc;
            if (t < sx) { outward = Vector3.forward; return origin + new Vector3(sx - t, 0f, sz + d); }
            t -= sx;
            if (t < arc) return Corner(0f, sz, 90f, t / arc, out outward);
            t -= arc;
            if (t < sz) { outward = Vector3.left; return origin + new Vector3(-d, 0f, sz - t); }
            t -= sz;
            return Corner(0f, 0f, 180f, Mathf.Clamp01(t / arc), out outward);
        }

        private static int SideOf(Vector3 outward) =>
            Mathf.Abs(outward.x) > Mathf.Abs(outward.z) ? (outward.x > 0f ? 1 : 3) : (outward.z < 0f ? 0 : 2);

        /// <summary>Darker forest floor in a square ring from <paramref name="inner"/> to <paramref name="outer"/> metres out.</summary>
        private void BuildForestFloor(Vector3 origin, Vector2 size, float inner, float outer)
        {
            var tex = Speckle(256, new Color32(54, 88, 30, 255), new[] { "3e6a22", "2f5419", "5a4a2a", "466e26", "35602a", "4b3d22" }, 5000, 0.8f, 2.6f);
            var mat = PipelineMaterials.CreateLit("ForestFloor", Color.white, tex);
            float y = origin.y - 0.006f; // between the lawn and the meadow
            Vector3 P(float x, float z) => new Vector3(origin.x + x, y, origin.z + z);
            var ins = new[] { P(-inner, -inner), P(size.x + inner, -inner), P(size.x + inner, size.y + inner), P(-inner, size.y + inner) };
            var outs = new[] { P(-outer, -outer), P(size.x + outer, -outer), P(size.x + outer, size.y + outer), P(-outer, size.y + outer) };
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var n = new List<Vector3>();
            var tri = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4, k = v.Count;
                v.Add(ins[i]); v.Add(outs[i]); v.Add(outs[j]); v.Add(ins[j]);
                // Corners run anticlockwise seen from above; this winding faces up in Unity.
                tri.Add(k); tri.Add(k + 2); tri.Add(k + 1); tri.Add(k); tri.Add(k + 3); tri.Add(k + 2);
            }
            foreach (var p in v) { uv.Add(new Vector2(p.x, p.z) * 0.4f); n.Add(Vector3.up); }
            var mesh = new Mesh { name = "ForestFloor" };
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            var go = new GameObject("ForestFloor");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
        }
    }
}

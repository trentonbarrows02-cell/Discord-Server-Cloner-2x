using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PlayShift.Grass
{
    /// <summary>
    /// The forest round the Open Field: the yard reads as a big private backyard in a natural wood.
    ///  - Trees are scattered (not in rows) through a band beyond the fence, with spacing checks so trunks never
    ///    overlap and canopies only touch a little; kind, height, width and turn all vary. Corners round off naturally.
    ///  - Detail by distance band, fixed at build time: the near band (what the player sees up close) uses the full
    ///    models, the far band the lighter ones. Both are merged into static meshes, one per direction round the yard
    ///    (12), so whole directions behind the camera are culled. Nothing switches level or culls by distance at
    ///    runtime, so no tree or bush can ever pop in or out. Near trees cast shadows; far trees and bushes don't.
    ///  - Bushes, rocks and flowers scattered behind the fence and under the trees. Nothing inside the fence.
    /// Materials: one shared matte foliage material (PlayShift/Foliage, GPU instanced, LOD cross-fade).
    /// </summary>
    public sealed partial class OpenFieldEnvironment
    {
        private static readonly string[] TreeKinds = { "TreeRound", "TreePine", "TreeSmall", "TreeTall" };
        private static readonly Vector2[] TreeHeights =
            { new Vector2(9f, 12.5f), new Vector2(10.5f, 15f), new Vector2(8f, 10.5f), new Vector2(11f, 15f) };
        private static readonly float[] TreeWeights = { 0.30f, 0.28f, 0.18f, 0.24f };

        [Header("Forest")]
        [Tooltip("Depth of the near forest band beyond the fence (m): full trees with LOD cross-fade.")]
        [SerializeField] private float nearForestDepth = 24f;
        [Tooltip("Total forest depth beyond the fence (m); past the near band the trees are merged light models.")]
        [SerializeField] private float forestDepth = 72f;
        [Tooltip("Trees per 100 m2 in the near band and in the far band.")]
        [SerializeField] private float nearTreesPer100m2 = 6.2f;
        [SerializeField] private float farTreesPer100m2 = 3.4f;
        [Tooltip("How much neighbouring canopies may overlap (0.75 = up to a quarter of their width).")]
        [SerializeField, Range(0.4f, 1f)] private float canopySpacing = 0.75f;
        [Tooltip("Tint on the trees' texture (slightly under 1 calms the greens).")]
        [SerializeField] private Color foliageTint = new Color(0.9f, 0.93f, 0.88f, 1f);
        [Tooltip("Leaves: 0 = the texture's bright lime/yellow greens, 1 = natural forest green.")]
        [SerializeField, Range(0f, 1f)] private float leafNaturalize = 1f;
        [Tooltip("Leaf colour saturation (1 = as the texture).")]
        [SerializeField, Range(0f, 1.5f)] private float leafSaturation = 0.85f;

        private struct Plant
        {
            public Mesh Lod0, Lod1;
            public Bounds Bounds;
            public string Name;
        }

        private struct Placed { public float X, Z, Canopy, Core; public bool Tree; }

        private Material _litMat, _nearMat, _bushMat;
        private int _treeCount, _farTreeCount, _bushCount;
        private Transform _forest;
        private readonly List<Placed> _placed = new List<Placed>();
        private readonly Dictionary<long, List<int>> _placedGrid = new Dictionary<long, List<int>>();
        private const float GridCell = 4f;

        private void BuildForest(Vector3 origin, Vector2 size)
        {
            var src = Resources.Load<Material>("OpenField/TreesLit");
            var tex = src != null ? src.mainTexture : Resources.Load<Texture2D>("OpenField/Plants_BaseColor");
            _litMat = FoliageMat("ForestFar", tex, foliageTint);
            _nearMat = FoliageMat("ForestNear", tex, foliageTint);   // casts shadows (see FlushBuckets)
            _bushMat = FoliageMat("ForestBushes", tex, foliageTint);
            foreach (var m in new[] { _litMat, _nearMat, _bushMat })
            {
                m.SetFloat("_Naturalize", leafNaturalize);
                m.SetFloat("_Saturation", leafSaturation);
            }

            var trees = new Plant[TreeKinds.Length];
            for (int i = 0; i < trees.Length; i++)
            {
                trees[i] = LoadPlant(TreeKinds[i]);
                if (trees[i].Lod0 == null) return;
            }
            var bushes = new[] { LoadPlant("BushCluster"), LoadPlant("BushLong") };
            if (bushes[0].Lod0 == null || bushes[1].Lod0 == null) return;

            _forest = new GameObject("Forest").transform;
            _forest.SetParent(transform, false);
            float f = fenceOffset;

            // Near trees first (they matter most), then far trees, then bushes fill the gaps.
            ScatterTrees(origin, size, trees, f + 4.2f, f + nearForestDepth, nearTreesPer100m2, near: true);
            ScatterTrees(origin, size, trees, f + nearForestDepth, f + forestDepth, farTreesPer100m2, near: false);

            // Bushes: a dense, irregular hedge just behind the fence, then undergrowth under the trees.
            ScatterBushes(origin, size, bushes, f + 0.9f, f + 4.5f, 22f, 1.3f, 2.2f);
            ScatterBushes(origin, size, bushes, f + 4.5f, f + nearForestDepth, 2.2f, 1.2f, 2.4f);

            BuildForestFloor(origin, size, f + 0.4f, f + forestDepth + 6f);
            Debug.Log($"[PlayShift Grass] Forest: {_treeCount} near trees (full models), {_farTreeCount} far trees (light models), " +
                      $"{_bushCount} bushes; all merged per direction, no runtime LOD switching.");
        }

        /// <summary>Distance from a point (relative to the field origin) to the lawn rectangle; 0 inside.</summary>
        private static float DistToYard(float x, float z, Vector2 size)
        {
            float dx = Mathf.Max(0f, Mathf.Max(-x, x - size.x)), dz = Mathf.Max(0f, Mathf.Max(-z, z - size.y));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Random point (relative to the origin) whose distance from the yard lies in [inner, outer).</summary>
        private bool BandPoint(Vector2 size, float inner, float outer, out float x, out float z)
        {
            for (int tries = 0; tries < 30; tries++)
            {
                x = R(-outer, size.x + outer);
                z = R(-outer, size.y + outer);
                float d = DistToYard(x, z, size);
                if (d >= inner && d < outer) return true;
            }
            x = z = 0f;
            return false;
        }

        private static float BandArea(Vector2 size, float inner, float outer) =>
            2f * (size.x + size.y) * (outer - inner) + Mathf.PI * (outer * outer - inner * inner);

        private bool Fits(float x, float z, float canopy, float core, bool tree)
        {
            int cx = Mathf.FloorToInt(x / GridCell), cz = Mathf.FloorToInt(z / GridCell);
            int reach = Mathf.CeilToInt((canopy * 2f + 1f) / GridCell);
            for (int gz = cz - reach; gz <= cz + reach; gz++)
                for (int gx = cx - reach; gx <= cx + reach; gx++)
                {
                    if (!_placedGrid.TryGetValue(((long)gx << 32) ^ (uint)gz, out var list)) continue;
                    foreach (int i in list)
                    {
                        var o = _placed[i];
                        float dx = o.X - x, dz = o.Z - z, d2 = dx * dx + dz * dz;
                        // Trees keep their canopies apart; anything else only needs clear trunks / bush bodies.
                        float need = tree && o.Tree ? canopySpacing * (canopy + o.Canopy) : core + o.Core;
                        if (d2 < need * need) return false;
                    }
                }
            return true;
        }

        private void Remember(float x, float z, float canopy, float core, bool tree)
        {
            _placed.Add(new Placed { X = x, Z = z, Canopy = canopy, Core = core, Tree = tree });
            long key = ((long)Mathf.FloorToInt(x / GridCell) << 32) ^ (uint)Mathf.FloorToInt(z / GridCell);
            if (!_placedGrid.TryGetValue(key, out var list)) _placedGrid[key] = list = new List<int>();
            list.Add(_placed.Count - 1);
        }

        private void ScatterTrees(Vector3 origin, Vector2 size, Plant[] trees, float inner, float outer, float per100, bool near)
        {
            int target = Mathf.RoundToInt(BandArea(size, inner, outer) * per100 / 100f);
            int placed = 0;
            for (int attempt = 0; attempt < target * 12 && placed < target; attempt++)
            {
                if (!BandPoint(size, inner, outer, out float x, out float z)) continue;
                int k = PickTree();
                Bounds b = trees[k].Bounds;
                float s = R(TreeHeights[k].x, TreeHeights[k].y) / b.size.y, w = R(0.86f, 1.14f);
                float canopy = Mathf.Max(b.extents.x, b.extents.z) * s * w;
                if (!Fits(x, z, canopy, 0.7f, true)) continue;
                Remember(x, z, canopy, 0.7f, true);
                placed++;
                var rot = Quaternion.Euler(0f, R(0f, 360f), 0f);
                var scale = new Vector3(s * w, s, s * w * R(0.92f, 1.08f));
                Vector3 p = origin + new Vector3(x, 0f, z);
                Vector3 off = Vector3.Scale(new Vector3(b.center.x, b.min.y, b.center.z), scale);
                Vector3 at = p - rot * off - Vector3.up * (0.006f * b.size.y * s);
                if (near) { Add(_nearMat, trees[k].Lod0, at, rot, scale, 200 + Sector(origin, size, p)); _treeCount++; }
                else { Add(_litMat, trees[k].Lod1, at, rot, scale, 100 + Sector(origin, size, p)); _farTreeCount++; }
            }
        }

        private void ScatterBushes(Vector3 origin, Vector2 size, Plant[] bushes, float inner, float outer, float per100, float hMin, float hMax)
        {
            int target = Mathf.RoundToInt(BandArea(size, inner, outer) * per100 / 100f);
            int placed = 0;
            for (int attempt = 0; attempt < target * 5 && placed < target; attempt++)
            {
                if (!BandPoint(size, inner, outer, out float x, out float z)) continue;
                var bush = bushes[_rng.Next(bushes.Length)];
                Bounds b = bush.Bounds;
                float h = R(hMin, hMax), sv = h / b.size.y, sh = Mathf.Min(sv * R(0.8f, 1.2f), 2.6f / Mathf.Max(b.size.x, b.size.z));
                // Close to the fence, keep the bush from reaching through it.
                float d = DistToYard(x, z, size), maxHalf = Mathf.Max(0.4f, d - fenceOffset - 0.25f);
                float half = Mathf.Max(b.extents.x, b.extents.z) * sh;
                if (half > maxHalf) { sh *= maxHalf / half; half = maxHalf; }
                float core = half * 0.8f;
                if (!Fits(x, z, half, core, false)) continue;
                Remember(x, z, half, core, false);
                placed++;
                var rot = Quaternion.Euler(0f, R(0f, 360f), 0f);
                var scale = new Vector3(sh, Mathf.Min(sv, sh * 2.4f), sh * R(0.85f, 1.15f));
                Vector3 p = origin + new Vector3(x, 0f, z);
                Vector3 off = Vector3.Scale(new Vector3(b.center.x, b.min.y, b.center.z), scale);
                // Full model in the hedge by the fence, the lighter one for undergrowth under the trees.
                Add(_bushMat, d < fenceOffset + 5f ? bush.Lod0 : bush.Lod1, p - rot * off - Vector3.up * (0.006f * b.size.y * scale.y), rot, scale,
                    300 + Sector(origin, size, p));
                _bushCount++;
            }
        }

        /// <summary>Which of 12 directions round the yard centre a point lies in (one merged mesh each, so the ones behind
        /// the camera are culled).</summary>
        private static int Sector(Vector3 origin, Vector2 size, Vector3 p)
        {
            Vector3 c = origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f);
            float a = Mathf.Atan2(p.z - c.z, p.x - c.x) + Mathf.PI;
            return Mathf.Clamp(Mathf.FloorToInt(a / (Mathf.PI * 2f) * 12f), 0, 11);
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

        /// <summary>Darker forest floor in a square ring from <paramref name="inner"/> to <paramref name="outer"/> metres out.</summary>
        private void BuildForestFloor(Vector3 origin, Vector2 size, float inner, float outer)
        {
            var tex = Speckle(256, new Color32(52, 82, 32, 255), new[] { "3e6424", "2f501c", "54482c", "456a28", "355a2a", "4a3e26" }, 5000, 0.8f, 2.6f);
            var mat = FoliageMat("ForestFloor", tex, Color.white);
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

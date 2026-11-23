using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// Editor-time surgery on the trimmer model (the .glb stays untouched). The body is one mesh (shaft, motor,
    /// loop handle); this splits it into connected pieces ("islands", vertices welded by position so UV seams don't
    /// split pieces), so unwanted parts can be dropped into a separate mesh asset with zero runtime cost.
    ///  - AnalyzeForBatch: logs every island and renders them in distinct colours to Captures/trimmer_islands.png.
    ///  - BuildForBatch: writes Assets/PlayShift/Models/TrimmerBody_NoHandle.asset without the islands listed in
    ///    <see cref="RemovedIslands"/> (via -removeIslands 3,5 on the command line or the default list).
    /// </summary>
    public static class TrimmerMeshTools
    {
        public const string ModelPath = "Assets/PlayShift/Models/Trimmer.glb";
        public const string BodyMeshName = "TrimmerBody";
        public const string OutputPath = "Assets/PlayShift/Grass/Resources/TrimmerBody_NoHandle.asset"; // loaded by TrimmerViewmodel

        private static readonly Color[] Palette =
        {
            Color.red, Color.green, Color.blue, Color.yellow, Color.magenta, Color.cyan, new Color(1f, 0.5f, 0f),
            new Color(0.5f, 0f, 1f), Color.white, new Color(0.5f, 0.3f, 0.1f), new Color(1f, 0.6f, 0.8f), Color.gray,
        };

        public static Mesh LoadBodyMesh() =>
            AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Mesh>().FirstOrDefault(m => m.name == BodyMeshName);

        /// <summary>Triangle index lists per island, largest first.</summary>
        public static List<List<int>> Islands(Mesh mesh)
        {
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            // Weld by position (glTF duplicates vertices along UV/normal seams).
            var weld = new Dictionary<Vector3Int, int>();
            var id = new int[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var key = Vector3Int.RoundToInt(verts[i] * 100000f);
                if (!weld.TryGetValue(key, out int w)) { w = weld.Count; weld[key] = w; }
                id[i] = w;
            }
            var parent = Enumerable.Range(0, weld.Count).ToArray();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = Find(id[tris[t]]), b = Find(id[tris[t + 1]]), c = Find(id[tris[t + 2]]);
                parent[b] = a; parent[Find(c)] = a;
            }
            var groups = new Dictionary<int, List<int>>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                int root = Find(id[tris[t]]);
                if (!groups.TryGetValue(root, out var list)) groups[root] = list = new List<int>();
                list.Add(tris[t]); list.Add(tris[t + 1]); list.Add(tris[t + 2]);
            }
            return groups.Values.OrderByDescending(g => g.Count).ToList();
        }

        public static void AnalyzeForBatch()
        {
            var mesh = LoadBodyMesh();
            if (mesh == null) { Debug.LogError("[PlayShift Trimmer] Body mesh not found in " + ModelPath); Exit(1); return; }
            var verts = mesh.vertices;
            var islands = Islands(mesh);
            var sb = new StringBuilder("[PlayShift Trimmer] " + islands.Count + " islands in " + mesh.name + " (" + mesh.triangles.Length / 3 + " tris)\n");
            var colored = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            var cv = new List<Vector3>(); var cc = new List<Color>(); var ct = new List<int>();
            for (int i = 0; i < islands.Count; i++)
            {
                var isl = islands[i];
                var b = new Bounds(verts[isl[0]], Vector3.zero);
                foreach (int v in isl) b.Encapsulate(verts[v]);
                var col = Palette[i % Palette.Length];
                sb.AppendLine($"  island {i}: {isl.Count / 3} tris, colour {ColorName(i)}, centre {b.center:F3}, size {b.size:F3}");
                foreach (int v in isl) { ct.Add(cv.Count); cv.Add(verts[v]); cc.Add(col); }
            }
            Debug.Log(sb.ToString());
            colored.SetVertices(cv); colored.SetColors(cc); colored.SetTriangles(ct, 0); colored.RecalculateNormals();
            RenderViews(colored, Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Captures", "trimmer_islands.png"));
            Exit(0);
        }

        public static void BuildForBatch()
        {
            var remove = new HashSet<int>(RemovedIslands);
            var box = HandleBox;
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-removeIslands") remove = new HashSet<int>(args[i + 1].Split(',').Select(int.Parse));
                if (args[i] == "-handleBox")
                {
                    var f = args[i + 1].Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    box = new Vector3(f[0], f[1], f[2]);
                }
            }
            var mesh = Build(remove, box);
            if (mesh != null)
                RenderViews(Tint(mesh), Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Captures", "trimmer_no_handle.png"));
            Debug.Log(mesh != null ? "[PlayShift Trimmer] NO-HANDLE MESH OK" : "[PlayShift Trimmer] NO-HANDLE MESH FAILED");
            Exit(mesh != null ? 0 : 1);
        }

        /// <summary>Small separate pieces to drop (index in <see cref="Islands"/>, largest first): 1 = the loose blade
        /// fragment that floated next to the cutting head.</summary>
        public static readonly int[] RemovedIslands = { 1 };

        /// <summary>
        /// The loop handle is fused to the shaft, so it is cut by position (body mesh space; the shaft runs along X):
        /// triangles whose centre lies in x = [box.x, box.y] and above y = box.z (the top of the shaft) are removed.
        /// </summary>
        public static readonly Vector3 HandleBox = new Vector3(-0.27f, -0.09f, 0.372f);

        public static Mesh Build(HashSet<int> remove, Vector3 handleBox)
        {
            var src = LoadBodyMesh();
            if (src == null) return null;
            var verts = src.vertices;
            var islands = Islands(src);
            var keep = new List<int>();
            int cut = 0;
            for (int i = 0; i < islands.Count; i++)
            {
                if (remove.Contains(i)) continue;
                var isl = islands[i];
                for (int t = 0; t < isl.Count; t += 3)
                {
                    var c = (verts[isl[t]] + verts[isl[t + 1]] + verts[isl[t + 2]]) / 3f;
                    if (c.x >= handleBox.x && c.x <= handleBox.y && c.y > handleBox.z) { cut++; continue; }
                    keep.Add(isl[t]); keep.Add(isl[t + 1]); keep.Add(isl[t + 2]);
                }
            }
            Debug.Log($"[PlayShift Trimmer] handle box cut {cut} tris");

            var mesh = Object.Instantiate(src);
            mesh.name = "TrimmerBody_NoHandle";
            mesh.subMeshCount = 1;
            mesh.SetTriangles(keep, 0);
            mesh.RecalculateBounds();
            MeshUtility.Optimize(mesh); // drops the now-unused vertices
            AssetDatabase.DeleteAsset(OutputPath);
            AssetDatabase.CreateAsset(mesh, OutputPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PlayShift Trimmer] removed islands {string.Join(",", remove)}: {src.triangles.Length / 3} -> {keep.Count / 3} tris");
            return mesh;
        }

        /// <summary>Copy with flat vertex colour for the preview render.</summary>
        private static Mesh Tint(Mesh src)
        {
            var m = Object.Instantiate(src);
            var cols = new Color[m.vertexCount];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color(1f, 0.8f, 0.1f);
            m.colors = cols;
            return m;
        }

        private static void RenderViews(Mesh mesh, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var go = new GameObject("IslandPreview", typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mat = new Material(Shader.Find("Hidden/Internal-Colored")); // unlit, uses vertex colours
            mat.SetInt("_Cull", 0);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            var cam = new GameObject("IslandCam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.15f, 0.15f, 0.15f);
            cam.orthographic = true;
            var b = mesh.bounds;
            cam.orthographicSize = Mathf.Max(b.size.x, b.size.y, b.size.z) * 0.6f;
            var rt = new RenderTexture(1600, 800, 24);
            cam.targetTexture = rt;
            var outTex = new Texture2D(1600, 800, TextureFormat.RGB24, false);
            // Two views side by side: from the side (+X) and from above.
            var views = new[] { (b.center + Vector3.right * 3f, Vector3.up), (b.center + Vector3.up * 3f, Vector3.forward) };
            var full = new Texture2D(3200, 800, TextureFormat.RGB24, false);
            for (int v = 0; v < views.Length; v++)
            {
                cam.transform.position = views[v].Item1;
                cam.transform.LookAt(b.center, views[v].Item2);
                cam.nearClipPlane = 0.01f; cam.farClipPlane = 10f;
                cam.Render();
                RenderTexture.active = rt;
                outTex.ReadPixels(new Rect(0, 0, 1600, 800), 0, 0);
                full.SetPixels(v * 1600, 0, 1600, 800, outTex.GetPixels());
            }
            RenderTexture.active = null;
            File.WriteAllBytes(path, full.EncodeToPNG());
            Debug.Log("[PlayShift Trimmer] island render: " + path);
            Object.DestroyImmediate(go); Object.DestroyImmediate(cam.gameObject);
        }

        private static string ColorName(int i) =>
            new[] { "red", "green", "blue", "yellow", "magenta", "cyan", "orange", "purple", "white", "brown", "pink", "gray" }[i % Palette.Length];

        private static void Exit(int code) { if (Application.isBatchMode) EditorApplication.Exit(code); }
    }
}

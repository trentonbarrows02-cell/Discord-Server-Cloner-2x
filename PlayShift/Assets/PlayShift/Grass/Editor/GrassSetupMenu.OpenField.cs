using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// Open-field map scene: a flat square lawn of configurable size with N removable grass targets (one per cell of
    /// the logical grid), dense hedge border, sparse scenery, distant mountains. Same player, trimmer, HUD, menus,
    /// upgrades and TikTok gift receiver as the test area. Built as its own scene (the test area is untouched).
    /// </summary>
    public static partial class GrassSetupMenu
    {
        public const string OpenFieldScenePath = "Assets/PlayShift/Scenes/OpenField100.unity";
        public const string OpenField2MScenePath = "Assets/PlayShift/Scenes/OpenField100_2M.unity";

        [MenuItem("PlayShift/Grass/Create Open Field (100 m, 1M grass)", priority = 2)]
        public static void CreateOpenField1M() => CreateOpenField(100f, 1_000_000, OpenFieldScenePath, "OpenField100Settings");

        [MenuItem("PlayShift/Grass/Create Open Field (100 m, 2M grass, stress test)", priority = 3)]
        public static void CreateOpenField2M() => CreateOpenField(100f, 2_000_000, OpenField2MScenePath, "OpenField100_2MSettings");

        /// <summary>Command line: builds both open-field scenes (1M and 2M targets).</summary>
        public static void BuildOpenFieldsForBatch()
        {
            bool ok = CreateOpenField1M_Saved() && CreateOpenField(100f, 2_000_000, OpenField2MScenePath, "OpenField100_2MSettings");
            Debug.Log(ok ? "[PlayShift Grass] OPEN FIELD OK" : "[PlayShift Grass] OPEN FIELD FAILED");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool CreateOpenField1M_Saved() => CreateOpenField(100f, 1_000_000, OpenFieldScenePath, "OpenField100Settings");

        /// <summary>
        /// Builds and saves the scene. Targets = cells of the logical grid (1 unit each), so cells = sqrt(targets) per side;
        /// 1M on 100 m = 10 cm cells (100 targets per m2), 2M = ~7 cm cells.
        /// </summary>
        public static bool CreateOpenField(float sizeMeters, long targets, string scenePath, string settingsName)
        {
            EnsureTmpEssentials();
            HudFontBuilder.Ensure();
            Directory.CreateDirectory(AssetFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(scenePath));
            // New scene FIRST: opening a scene unloads assets nothing references yet, which would turn freshly created
            // settings/materials into null references in the saved scene.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            int side = Mathf.CeilToInt(Mathf.Sqrt(targets));
            string settingsPath = AssetFolder + "/" + settingsName + ".asset";
            AssetDatabase.DeleteAsset(settingsPath);
            var settings = LoadOrCreate<GrassProgressionSettings>(settingsPath, s =>
            {
                s.fieldSizeMeters = new Vector2(sizeMeters, sizeMeters);
                s.cellCount = new Vector2Int(side, side);
                // Natural tall/short patches about 4 m across, whatever the cell size.
                s.heightNoiseCells = 4f / (sizeMeters / side);
                foreach (var level in s.levels) level.startingUnits = targets;
            });
            var grassMat = GrassMaterial();
            var sky = LoadOrCreateMaterial(AssetFolder + "/SkyClouds.mat", "PlayShift/SkyClouds");
            var lawnMat = LoadOrCreateMaterial(AssetFolder + "/OpenFieldLawn.mat", "PlayShift/GrassGround");
            var clipMat = AssetDatabase.LoadAssetAtPath<Material>(AssetFolder + "/GrassClippings.mat");
            if (clipMat == null) { clipMat = PipelineMaterials.CreateParticleUnlit("GrassClippings"); AssetDatabase.CreateAsset(clipMat, AssetFolder + "/GrassClippings.mat"); }
            AssetDatabase.SaveAssets();
            if (grassMat == null) return false;

            // Long haze so the distant mountains read as hazy blue, not as fog colour.
            SetUpLighting(sky, fogStart: 140f, fogEnd: 1500f);

            var root = new GameObject("OpenField");
            var renderer = root.AddComponent<GrassFieldRenderer>();
            var manager = root.AddComponent<GrassLevelManager>();
            root.AddComponent<TikTokGiftReceiver>();
            var env = new GameObject("Environment").AddComponent<OpenFieldEnvironment>();
            env.transform.SetParent(root.transform, false);
            Set(manager, "settings", settings);
            Set(manager, "fieldRenderer", renderer);
            Set(renderer, "grassMaterial", grassMat);
            SetFloat(renderer, "chunkMeters", 8f); // ~150 blade draws at most on 100 m (data tiles are separate, 3.2 m)
            Set(env, "manager", manager);
            Set(env, "lawnMaterial", lawnMat);

            // Start just inside the hedge on the south edge, facing across the field.
            CreatePlayer(new Vector3(sizeMeters * 0.5f, 0f, 1.2f), 0f, clipMat, farClip: 2600f);

            EditorSceneManager.SaveScene(scene, scenePath);
            var list = EditorBuildSettings.scenes.ToList();
            if (!list.Any(s => s.path == scenePath)) { list.Add(new EditorBuildSettingsScene(scenePath, true)); EditorBuildSettings.scenes = list.ToArray(); }
            AssetDatabase.SaveAssets();
            Debug.Log($"[PlayShift Grass] Open field saved: {scenePath} ({sizeMeters} m, {side}x{side} cells = {(long)side * side:N0} targets).");
            return true;
        }

        private static void SetFloat(Object target, string field, float value)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null) return;
            p.floatValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}

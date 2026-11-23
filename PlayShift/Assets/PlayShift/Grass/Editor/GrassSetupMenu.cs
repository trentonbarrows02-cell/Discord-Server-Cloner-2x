using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// One-click scene setup.
    ///  - Create Test Area: a polished first-person test lawn (dense grass, trimmer viewmodel, clippings,
    ///    audio, sky, sun with shadows, perimeter trees) to tune the cutting feel before the full game.
    ///  - Create Level Setup: just the grass system, for an existing scene.
    /// </summary>
    public static class GrassSetupMenu
    {
        private const string AssetFolder = "Assets/PlayShift/Grass/Generated";
        private const string TrimmerModelPath = "Assets/PlayShift/Models/Trimmer.glb";

        [MenuItem("PlayShift/Grass/Create Test Area (first-person)", priority = 0)]
        public static void CreateTestArea()
        {
            EnsureTmpEssentials();
            Directory.CreateDirectory(AssetFolder);

            var settings = LoadOrCreate<GrassProgressionSettings>(AssetFolder + "/GrassTestAreaSettings.asset", s =>
            {
                s.fieldSizeMeters = new Vector2(24f, 24f);
                s.cellCount = new Vector2Int(480, 480);
                s.heightNoiseCells = 44f;
            });
            var grassMat = GrassMaterial();
            var sky = LoadOrCreateMaterial(AssetFolder + "/SkyClouds.mat", "PlayShift/SkyClouds");
            var clipMat = AssetDatabase.LoadAssetAtPath<Material>(AssetFolder + "/GrassClippings.mat");
            if (clipMat == null) { clipMat = PipelineMaterials.CreateParticleUnlit("GrassClippings"); AssetDatabase.CreateAsset(clipMat, AssetFolder + "/GrassClippings.mat"); }
            AssetDatabase.SaveAssets();
            if (grassMat == null) return;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();

            // Lighting: bright daytime sun with soft shadows, cloud sky, light haze.
            var sun = UnityEngine.Object.FindObjectsOfType<Light>().FirstOrDefault(l => l.type == LightType.Directional);
            if (sun == null)
            {
                sun = new GameObject("Sun").AddComponent<Light>();
                sun.type = LightType.Directional;
                Undo.RegisterCreatedObjectUndo(sun.gameObject, "Create Sun");
            }
            Undo.RecordObject(sun.transform, "Sun");
            sun.transform.rotation = Quaternion.Euler(38f, 135f, 0f);
            sun.intensity = PipelineMaterials.IsUrp ? 2.2f : 1.25f;
            sun.color = new Color(1f, 0.95f, 0.86f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;
            RenderSettings.sun = sun;
            if (sky != null) RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1.1f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.81f, 0.88f, 0.94f);
            RenderSettings.fogStartDistance = 60f;
            RenderSettings.fogEndDistance = 260f;
            DynamicGI.UpdateEnvironment();
            QualitySettings.shadowDistance = Mathf.Max(QualitySettings.shadowDistance, 40f);

            // Grass system.
            var root = new GameObject("GrassTestArea");
            Undo.RegisterCreatedObjectUndo(root, "Create Grass Test Area");
            var renderer = root.AddComponent<GrassFieldRenderer>();
            var manager = root.AddComponent<GrassLevelManager>();
            root.AddComponent<TikTokGiftReceiver>();
            var env = new GameObject("Environment").AddComponent<TestAreaEnvironment>();
            env.transform.SetParent(root.transform, false);
            Set(manager, "settings", settings);
            Set(manager, "fieldRenderer", renderer);
            SetInt(manager, "startLevel", 3);
            Set(renderer, "grassMaterial", grassMat);
            Set(env, "manager", manager);

            // First-person player: camera + floating trimmer, no body or hands.
            foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>())
                if (cam.CompareTag("MainCamera")) { Undo.RecordObject(cam.gameObject, "Disable camera"); cam.gameObject.SetActive(false); }

            var player = new GameObject("Player");
            Undo.RegisterCreatedObjectUndo(player, "Create Player");
            player.transform.position = new Vector3(12f, 0f, -1.75f);
            var cc = player.AddComponent<CharacterController>();
            cc.height = 1.8f; cc.radius = 0.3f; cc.center = new Vector3(0f, 0.9f, 0f);
            var camGo = new GameObject("PlayerCamera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(player.transform, false);
            var camera = camGo.GetComponent<Camera>();
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 900f;
            camera.fieldOfView = 68f;

            var blades = player.AddComponent<TrimmerBlades>();
            var cutter = player.AddComponent<GrassCutter>();
            var fp = player.AddComponent<FirstPersonController>();
            var vm = player.AddComponent<TrimmerViewmodel>();
            Set(cutter, "blades", blades);
            Set(fp, "playerCamera", camera);
            Set(fp, "cutter", cutter);
            Set(fp, "blades", blades);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(TrimmerModelPath);
            if (model == null)
                Debug.LogWarning($"[PlayShift Grass] Could not import {TrimmerModelPath}. Install glTFast (Package Manager > Add package by name > com.unity.cloud.gltfast), then assign it to TrimmerViewmodel.");
            Set(vm, "trimmerModel", model);
            Set(vm, "controller", fp);
            Set(vm, "cutter", cutter);
            Set(vm, "blades", blades);

            var clipGo = new GameObject("GrassClippings", typeof(ParticleSystem));
            clipGo.transform.SetParent(player.transform, false);
            var clips = clipGo.AddComponent<GrassClippings>();
            Set(clips, "cutter", cutter);
            Set(clips, "clippingMaterial", clipMat);

            var audioGo = new GameObject("TrimmerAudio", typeof(AudioSource));
            audioGo.transform.SetParent(player.transform, false);
            var audio = audioGo.AddComponent<TrimmerAudio>();
            Set(audio, "blades", blades);
            Set(audio, "cutter", cutter);

            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = root;
            Debug.Log("[PlayShift Grass] Test area created. Press Play: hold Left Mouse (or press Space) to run the trimmer, WASD to walk, mouse to look. " +
                      "Change the level on GrassTestArea > GrassLevelManager > Start Level; quality on GrassFieldRenderer > Quality.", root);
        }

        [MenuItem("PlayShift/Grass/Create Level Setup (existing scene)", priority = 1)]
        public static void CreateLevelSetup()
        {
            EnsureTmpEssentials();
            Directory.CreateDirectory(AssetFolder);
            var settings = LoadOrCreate<GrassProgressionSettings>(AssetFolder + "/GrassProgressionSettings.asset", null);
            var grassMat = GrassMaterial();
            AssetDatabase.SaveAssets();
            if (grassMat == null) return;

            var root = new GameObject("GrassLevel");
            Undo.RegisterCreatedObjectUndo(root, "Create Grass Level");
            var renderer = root.AddComponent<GrassFieldRenderer>();
            var manager = root.AddComponent<GrassLevelManager>();
            root.AddComponent<TikTokGiftReceiver>();
            Set(manager, "settings", settings);
            Set(manager, "fieldRenderer", renderer);
            Set(renderer, "grassMaterial", grassMat);
            Selection.activeGameObject = root;
            Debug.Log("[PlayShift Grass] Level setup created. Move 'GrassLevel' so its origin is the lawn's corner, and add a " +
                      "GrassCutter (plus TrimmerBlades/TrimmerViewmodel for first-person) to the player.", root);
        }

        private static Material GrassMaterial()
        {
            string shaderName = GrassFieldRenderer.DefaultShaderName;
            string path = AssetFolder + (PipelineMaterials.IsUrp ? "/InstancedGrassURP.mat" : "/InstancedGrass.mat");
            var mat = LoadOrCreateMaterial(path, shaderName);
            if (mat == null)
                EditorUtility.DisplayDialog("PlayShift Grass", $"Shader '{shaderName}' not found or failed to compile. Check the Console for shader errors.", "OK");
            return mat;
        }

        private static Material LoadOrCreateMaterial(string path, string shaderName)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            var shader = Shader.Find(shaderName);
            if (shader == null) return null;
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        private static T LoadOrCreate<T>(string path, Action<T> init) where T : ScriptableObject
        {
            var a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a != null) return a;
            a = ScriptableObject.CreateInstance<T>();
            init?.Invoke(a);
            AssetDatabase.CreateAsset(a, path);
            return a;
        }

        private static void Set(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"[PlayShift Grass] {target.GetType().Name} has no field '{field}'."); return; }
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetInt(UnityEngine.Object target, string field, int value)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null) return;
            p.intValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Imports TMP Essential Resources non-interactively if the project lacks them.</summary>
        private static void EnsureTmpEssentials()
        {
            if (TMPro.TMP_Settings.defaultFontAsset != null) return;
            var importer = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("TMPro.TMP_PackageResourceImporter", false))
                .FirstOrDefault(t => t != null);
            var method = importer?.GetMethod("ImportResources", BindingFlags.Public | BindingFlags.Static);
            if (method != null && method.GetParameters().Length == 3)
            {
                method.Invoke(null, new object[] { true, false, false });
                Debug.Log("[PlayShift Grass] Imported TextMeshPro Essential Resources.");
            }
            else
            {
                Debug.LogWarning("[PlayShift Grass] TextMeshPro Essential Resources are missing. Import them via Window > TextMeshPro > Import TMP Essential Resources.");
            }
        }
    }
}

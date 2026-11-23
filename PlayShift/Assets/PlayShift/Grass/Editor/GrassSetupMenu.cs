using System;
using System.IO;
using System.Linq;
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
    public static partial class GrassSetupMenu
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

            SetUpLighting(sky, fogStart: 60f, fogEnd: 260f);

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

            CreatePlayer(new Vector3(12f, 0f, -1.75f), 0f, clipMat, farClip: 900f);

            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = root;
            Debug.Log("[PlayShift Grass] Test area created. Press Play: hold Left Mouse (or press Space) to run the trimmer, WASD to walk, mouse to look. " +
                      "Change the level on GrassTestArea > GrassLevelManager > Start Level; quality on GrassFieldRenderer > Quality.", root);
        }

        /// <summary>Bright daytime sun with soft shadows, cloud sky, linear haze.</summary>
        private static void SetUpLighting(Material sky, float fogStart, float fogEnd)
        {
            var sun = SceneComponents<Light>().FirstOrDefault(l => l.type == LightType.Directional);
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
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogEnd;
            DynamicGI.UpdateEnvironment();
            QualitySettings.shadowDistance = Mathf.Max(QualitySettings.shadowDistance, 40f);
        }

        /// <summary>First-person player: camera + floating trimmer (no body or hands), clippings, audio. Shared by the scenes.</summary>
        private static GameObject CreatePlayer(Vector3 position, float yaw, Material clipMat, float farClip)
        {
            foreach (var cam in SceneComponents<Camera>())
                if (cam.CompareTag("MainCamera")) { Undo.RecordObject(cam.gameObject, "Disable camera"); cam.gameObject.SetActive(false); }

            var player = new GameObject("Player");
            Undo.RegisterCreatedObjectUndo(player, "Create Player");
            player.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            var cc = player.AddComponent<CharacterController>();
            cc.height = 1.8f; cc.radius = 0.3f; cc.center = new Vector3(0f, 0.9f, 0f);
            var camGo = new GameObject("PlayerCamera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(player.transform, false);
            var camera = camGo.GetComponent<Camera>();
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = farClip;
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
            return player;
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

        /// <summary>Components in the active scene (avoids Find* APIs, whose signatures keep changing across Unity versions).</summary>
        private static T[] SceneComponents<T>() where T : Component =>
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(go => go.GetComponentsInChildren<T>(true)).ToArray();

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
            // Don't touch TMP_Settings.defaultFontAsset here: with no settings asset it throws (Unity 6 / uGUI 2.x).
            if (Resources.Load<TMPro.TMP_Settings>("TMP Settings") != null) return;
            // The project ships TMP Essential Resources in Assets/TextMesh Pro; this is only a fallback if they were deleted.
            // (In Unity 6 this import is asynchronous, so it completes after this call returns.)
            TMPro.TMP_PackageResourceImporter.ImportResources(true, false, false);
            AssetDatabase.Refresh();
            if (Resources.Load<TMPro.TMP_Settings>("TMP Settings") != null)
                Debug.Log("[PlayShift Grass] Imported TextMeshPro Essential Resources.");
            else
                Debug.LogWarning("[PlayShift Grass] TextMeshPro Essential Resources are missing. Import them via Window > TextMeshPro > Import TMP Essential Resources.");
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PlayShift.Grass.Editor
{
    /// <summary>One-click scene setup: settings asset, grass material, lawn, HUD, gift receiver and (optionally) a test mower.</summary>
    public static class GrassSetupMenu
    {
        private const string AssetFolder = "Assets/PlayShift/Grass/Generated";
        private const string SettingsPath = AssetFolder + "/GrassProgressionSettings.asset";
        private const string MaterialPath = AssetFolder + "/InstancedGrass.mat";

        [MenuItem("PlayShift/Grass/Create Level Setup", priority = 0)]
        public static void CreateLevelSetup() => CreateSetup(withTestMower: false);

        [MenuItem("PlayShift/Grass/Create Level Setup + Test Trimmer", priority = 1)]
        public static void CreateLevelSetupWithMower() => CreateSetup(withTestMower: true);

        private static void CreateSetup(bool withTestMower)
        {
            EnsureTmpEssentials();
            Directory.CreateDirectory(AssetFolder);

            var settings = AssetDatabase.LoadAssetAtPath<GrassProgressionSettings>(SettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<GrassProgressionSettings>();
                AssetDatabase.CreateAsset(settings, SettingsPath);
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                var shader = Shader.Find("PlayShift/InstancedGrass");
                if (shader == null)
                {
                    EditorUtility.DisplayDialog("PlayShift Grass", "Shader 'PlayShift/InstancedGrass' not found. Make sure InstancedGrass.shader compiled.", "OK");
                    return;
                }
                material = new Material(shader) { enableInstancing = true };
                AssetDatabase.CreateAsset(material, MaterialPath);
            }
            AssetDatabase.SaveAssets();

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();

            var root = new GameObject("GrassLevel");
            Undo.RegisterCreatedObjectUndo(root, "Create Grass Level");
            var renderer = root.AddComponent<GrassFieldRenderer>();
            var manager = root.AddComponent<GrassLevelManager>();
            root.AddComponent<TikTokGiftReceiver>();

            var so = new SerializedObject(manager);
            so.FindProperty("settings").objectReferenceValue = settings;
            so.FindProperty("fieldRenderer").objectReferenceValue = renderer;
            so.ApplyModifiedPropertiesWithoutUndo();
            var rso = new SerializedObject(renderer);
            rso.FindProperty("grassMaterial").objectReferenceValue = material;
            rso.ApplyModifiedPropertiesWithoutUndo();

            // Ground under the lawn so cut stubble has soil to sit on.
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "LawnGround";
            ground.transform.SetParent(root.transform, false);
            Vector2 size = settings.fieldSizeMeters;
            ground.transform.localPosition = new Vector3(size.x * 0.5f, -0.005f, size.y * 0.5f);
            ground.transform.localScale = new Vector3(size.x / 10f, 1f, size.y / 10f);
            var groundRenderer = ground.GetComponent<Renderer>();
            var groundMat = new Material(groundRenderer.sharedMaterial) { color = new Color(0.22f, 0.17f, 0.09f) };
            AssetDatabase.CreateAsset(groundMat, AssetDatabase.GenerateUniqueAssetPath(AssetFolder + "/LawnGround.mat"));
            groundRenderer.sharedMaterial = groundMat;

            // Attach a cutter to whatever was selected (your mower), or create a test mower.
            var selected = Selection.activeGameObject;
            if (withTestMower)
            {
                var mower = CreateTrimmer(root.transform.TransformPoint(new Vector3(2f, 0f, 2f)));
                var ctrl = mower.AddComponent<DebugMowerController>();
                var cam = Camera.main;
                if (cam != null)
                {
                    var cso = new SerializedObject(ctrl);
                    cso.FindProperty("followCamera").objectReferenceValue = cam.transform;
                    cso.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            else if (selected != null && selected != root && selected.GetComponent<GrassCutter>() == null)
            {
                Undo.AddComponent<GrassCutter>(selected);
                Debug.Log($"[PlayShift Grass] Added GrassCutter to '{selected.name}'.", selected);
            }

            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = root;
            Debug.Log("[PlayShift Grass] Level setup created. Move 'GrassLevel' so its origin is the lawn's corner; " +
                      "add a GrassCutter to your mower if it does not have one.", root);
        }

        private const string TrimmerModelPath = "Assets/PlayShift/Models/Trimmer.glb";
        private const float TrimmerScale = 2.2f;

        [MenuItem("PlayShift/Grass/Add Trimmer To Scene", priority = 20)]
        public static void AddTrimmerToScene()
        {
            var t = CreateTrimmer(Vector3.zero);
            Selection.activeGameObject = t;
        }

        /// <summary>
        /// Trimmer root (cutter + visuals) with the model as a child. The model's origin is the bottom centre of
        /// the cutting head and it faces +Z, so the root's position is exactly where grass gets cut.
        /// Needs a glTF importer for .glb (Unity's glTFast package: com.unity.cloud.gltfast); falls back to a cube.
        /// </summary>
        private static GameObject CreateTrimmer(Vector3 position)
        {
            var root = new GameObject("Trimmer");
            Undo.RegisterCreatedObjectUndo(root, "Create Trimmer");
            root.transform.position = position;

            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(TrimmerModelPath);
            GameObject model;
            if (modelAsset != null)
            {
                model = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
                model.transform.localScale = Vector3.one * TrimmerScale;
            }
            else
            {
                Debug.LogWarning($"[PlayShift Grass] Could not import {TrimmerModelPath}. Install glTFast (Package Manager > Add package by name > " +
                                 "com.unity.cloud.gltfast) to use the trimmer model; using a placeholder cube for now.");
                model = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.DestroyImmediate(model.GetComponent<Collider>());
                model.transform.localScale = new Vector3(0.4f, 0.15f, 1.6f);
                model.transform.localPosition = new Vector3(0f, 0.08f, -0.7f);
            }
            model.name = "TrimmerModel";
            model.transform.SetParent(root.transform, false);

            var cutter = root.AddComponent<GrassCutter>();
            cutter.CutRadius = 0.65f;
            var visuals = root.AddComponent<TrimmerVisuals>();
            var vso = new SerializedObject(visuals);
            vso.FindProperty("sweepRoot").objectReferenceValue = model.transform;
            vso.ApplyModifiedPropertiesWithoutUndo();
            return root;
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

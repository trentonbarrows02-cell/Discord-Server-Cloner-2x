using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// The first time this project is opened, builds and saves the first-person grass test scene so it is
    /// ready to press Play: imports TMP Essential Resources, runs Create Test Area, saves
    /// Assets/PlayShift/Scenes/GrassTestArea.unity, adds it to Build Settings and opens it.
    /// Runs once (skipped when the scene already exists). Rebuild any time via the menu.
    /// </summary>
    [InitializeOnLoad]
    public static class FirstOpenSetup
    {
        public const string ScenePath = "Assets/PlayShift/Scenes/GrassTestArea.unity";
        private const string SessionKey = "PlayShift.Grass.FirstOpenSetup";

        static FirstOpenSetup()
        {
            // Re-checked after every domain reload (package installs reload the domain), until the scene exists.
            if (Application.isBatchMode || SessionState.GetBool(SessionKey, false) || File.Exists(ScenePath)) return;
            EditorApplication.delayCall += WaitUntilReady;
        }

        private static int s_waits;

        private static void WaitUntilReady()
        {
            // Packages (glTFast) and the first asset import must finish before the trimmer model exists.
            bool modelMissing = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/PlayShift/Models/Trimmer.glb") == null;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || (modelMissing && s_waits < 300))
            {
                s_waits++;
                if (modelMissing && s_waits == 100) AssetDatabase.ImportAsset("Assets/PlayShift/Models/Trimmer.glb", ImportAssetOptions.ForceUpdate);
                EditorApplication.delayCall += WaitUntilReady;
                return;
            }
            BuildTestScene(askToSave: false);
        }

        /// <summary>
        /// For command-line setup: Unity.exe -batchmode -quit -projectPath PlayShift
        ///   -executeMethod PlayShift.Grass.Editor.FirstOpenSetup.BuildForBatch -logFile setup.log
        /// Builds and saves the test scene; exits with code 1 (and logs why) if the trimmer model or shaders are missing.
        /// </summary>
        public static void BuildForBatch()
        {
            bool ok = true;
            if (AssetDatabase.LoadAssetAtPath<GameObject>("Assets/PlayShift/Models/Trimmer.glb") == null)
            { Debug.LogError("[PlayShift Grass] Trimmer.glb did not import (is glTFast installed?)."); ok = false; }
            foreach (var name in new[] { GrassFieldRenderer.DefaultShaderName, "PlayShift/SkyClouds" })
                if (Shader.Find(name) == null) { Debug.LogError($"[PlayShift Grass] Shader '{name}' missing or failed to compile."); ok = false; }
            BuildTestScene(askToSave: false);
            if (!File.Exists(ScenePath)) { Debug.LogError("[PlayShift Grass] Test scene was not saved."); ok = false; }
            if (Resources.Load<TMPro.TMP_Settings>("TMP Settings") == null)
            { Debug.LogError("[PlayShift Grass] TextMeshPro Essential Resources were not imported (HUD text would be missing)."); ok = false; }
            Debug.Log(ok ? "[PlayShift Grass] BATCH SETUP OK" : "[PlayShift Grass] BATCH SETUP FAILED");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        [MenuItem("PlayShift/Grass/Rebuild Test Scene", priority = 10)]
        public static void RebuildFromMenu() => BuildTestScene(askToSave: true);

        private static void BuildTestScene(bool askToSave)
        {
            if (askToSave && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            HudFontBuilder.Ensure();
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            GrassSetupMenu.CreateTestArea();
            EditorSceneManager.SaveScene(scene, ScenePath);

            var scenes = EditorBuildSettings.scenes.ToList();
            if (!scenes.Any(s => s.path == ScenePath))
            {
                scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }
            AssetDatabase.SaveAssets();
            SessionState.SetBool(SessionKey, true);
            Debug.Log("[PlayShift Grass] Test scene ready: " + ScenePath + ". Press Play, then hold Left Mouse (or press Space) to run the trimmer.");
        }
    }
}

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// Replaces the player in the existing Cut the Grass scenes with one built on the reusable PlayShift first-person
    /// prefab (keeps each scene's spawn point, view distance and clippings material; nothing else in the scene changes).
    /// </summary>
    public static partial class GrassSetupMenu
    {
        [MenuItem("PlayShift/Grass/Update Player In All Scenes (First-Person Prefab)", priority = 40)]
        public static void UpdatePlayerInAllScenesMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            UpdatePlayerInAllScenes();
        }

        /// <summary>Command line / tools: updates every Cut the Grass scene, then reopens the scene that was open.</summary>
        public static int UpdatePlayerInAllScenes()
        {
            PlayShift.FirstPerson.Editor.PlayShiftFirstPersonBuilder.BuildPrefab(); // fresh tuning every time
            string original = EditorSceneManager.GetActiveScene().path;
            int done = 0;
            foreach (var path in new[] { FirstOpenSetup.ScenePath, OpenFieldScenePath, OpenField2MScenePath })
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null) continue;
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                var old = Object.FindAnyObjectByType<FirstPersonController>(FindObjectsInactive.Include);
                if (old == null) { Debug.LogWarning($"[PlayShift Grass] No player in {path}; skipped."); continue; }

                var go = old.gameObject;
                Vector3 pos = go.transform.position;
                float yaw = go.transform.eulerAngles.y;
                var oldCam = old.Camera != null ? old.Camera : go.GetComponentInChildren<Camera>(true);
                float farClip = oldCam != null ? oldCam.farClipPlane : 1000f;
                var clips = go.GetComponentInChildren<GrassClippings>(true);
                var clipMat = clips != null ? new SerializedObject(clips).FindProperty("clippingMaterial").objectReferenceValue as Material : null;
                Object.DestroyImmediate(go);

                CreatePlayer(pos, yaw, clipMat, farClip);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                done++;
                Debug.Log($"[PlayShift Grass] Player updated in {path}.");
            }
            if (!string.IsNullOrEmpty(original) && AssetDatabase.LoadAssetAtPath<SceneAsset>(original) != null)
                EditorSceneManager.OpenScene(original, OpenSceneMode.Single);
            return done;
        }

        /// <summary>Command line: Unity -batchmode -executeMethod PlayShift.Grass.Editor.GrassSetupMenu.UpdatePlayerForBatch</summary>
        public static void UpdatePlayerForBatch()
        {
            int n = UpdatePlayerInAllScenes();
            Debug.Log(n > 0 ? $"[PlayShift Grass] PLAYER UPDATE OK ({n} scenes)" : "[PlayShift Grass] PLAYER UPDATE FAILED");
            if (Application.isBatchMode) EditorApplication.Exit(n > 0 ? 0 : 1);
        }
    }
}

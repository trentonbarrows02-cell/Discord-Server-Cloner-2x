using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// Scripted Play-mode capture for progress screenshots and a short clip (see <see cref="PlayShift.Grass.Dev.GrassCaptureDriver"/>).
    /// Menu: PlayShift > Grass > Capture Screenshots + Clip, or from the command line. Batch mode is the reliable
    /// way (a background/minimised editor throttles Play mode); it exits when done. Do NOT pass -quit or -nographics:
    ///   Unity.exe -batchmode -projectPath PlayShift -executeMethod PlayShift.Grass.Editor.GrassCaptureTool.Run -logFile capture.log
    /// Output goes to &lt;project&gt;/Captures/&lt;timestamp&gt;/ (JPGs, clip.mp4, report.txt).
    /// </summary>
    [InitializeOnLoad]
    public static class GrassCaptureTool
    {
        private const string PendingKey = "PlayShift.Grass.CapturePending";

        static GrassCaptureTool()
        {
            // Entering Play mode reloads the domain, so the request survives in SessionState.
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(PendingKey, false)) return;
                SessionState.SetBool(PendingKey, false);
                new GameObject("CaptureDriver").AddComponent<PlayShift.Grass.Dev.GrassCaptureDriver>();
            };
        }

        [MenuItem("PlayShift/Grass/Capture Screenshots + Clip", priority = 20)]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { new GameObject("CaptureDriver").AddComponent<PlayShift.Grass.Dev.GrassCaptureDriver>(); return; }
            EditorApplication.delayCall += () =>
            {
                if (EditorSceneManager.GetActiveScene().path != FirstOpenSetup.ScenePath)
                    EditorSceneManager.OpenScene(FirstOpenSetup.ScenePath);
                var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameViewType != null && !Application.isBatchMode)
                {
                    var gameView = EditorWindow.GetWindow(gameViewType);
                    gameView.maximized = true;
                    gameView.Focus();
                }
                SessionState.SetBool(PendingKey, true);
                EditorApplication.isPlaying = true;
            };
        }
    }
}

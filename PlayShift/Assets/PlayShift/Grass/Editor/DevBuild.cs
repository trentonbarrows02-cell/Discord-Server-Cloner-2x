using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// Windows development build of the main field (OpenField100) for play-testing outside the editor, starting in
    /// fullscreen at the monitor's resolution. Development build: the Ctrl + 1-5 level shortcut works. The project's
    /// own player settings are restored afterwards.
    /// Menu: PlayShift > Build > Windows Dev Build (Fullscreen). Batch: -executeMethod PlayShift.Grass.Editor.DevBuild.Run
    /// Output: &lt;repo&gt;/Builds/CutTheGrass_Dev/CutTheGrass.exe
    /// </summary>
    public static class DevBuild
    {
        [MenuItem("PlayShift/Build/Windows Dev Build (Fullscreen)")]
        public static void Run()
        {
            string root = Directory.GetParent(Directory.GetParent(Application.dataPath).FullName).FullName;
            string outDir = Path.Combine(root, "Builds", "CutTheGrass_Dev");
            Directory.CreateDirectory(outDir);

            var prevMode = PlayerSettings.fullScreenMode;
            bool prevNative = PlayerSettings.defaultIsNativeResolution;
            try
            {
                PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
                PlayerSettings.defaultIsNativeResolution = true;
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/PlayShift/Scenes/OpenField100.unity" },
                    locationPathName = Path.Combine(outDir, "CutTheGrass.exe"),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development,
                };
                BuildReport report = BuildPipeline.BuildPlayer(options);
                Debug.Log($"[PlayShift Build] {report.summary.result}: {options.locationPathName} " +
                          $"({report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalTime.TotalSeconds:F0} s, {report.summary.totalErrors} errors)");
                if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
            }
            finally
            {
                PlayerSettings.fullScreenMode = prevMode;
                PlayerSettings.defaultIsNativeResolution = prevNative;
                AssetDatabase.SaveAssets(); // the build saved the temporary values to ProjectSettings; write ours back
            }
        }
    }
}

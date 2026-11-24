using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// <see cref="GraphicsProfile"/> changes QualitySettings at runtime; in the editor those changes would otherwise stay
    /// on the project's quality level after Play mode ends. Snapshot on entering Play mode, put back on leaving it.
    /// </summary>
    [InitializeOnLoad]
    internal static class GraphicsProfileRestore
    {
        private const string Key = "PlayShift.GraphicsProfileRestore";

        static GraphicsProfileRestore() => EditorApplication.playModeStateChanged += OnChange;

        private static void OnChange(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.ExitingEditMode)
            {
                SessionState.SetString(Key, string.Join("|",
                    (int)QualitySettings.shadows, (int)QualitySettings.shadowResolution, QualitySettings.shadowCascades,
                    QualitySettings.shadowDistance.ToString("R", CultureInfo.InvariantCulture), QualitySettings.lodBias.ToString("R", CultureInfo.InvariantCulture),
                    QualitySettings.pixelLightCount));
            }
            else if (s == PlayModeStateChange.EnteredEditMode)
            {
                var v = SessionState.GetString(Key, "").Split('|');
                if (v.Length != 6) return;
                QualitySettings.shadows = (ShadowQuality)int.Parse(v[0]);
                QualitySettings.shadowResolution = (ShadowResolution)int.Parse(v[1]);
                QualitySettings.shadowCascades = int.Parse(v[2]);
                QualitySettings.shadowDistance = float.Parse(v[3], CultureInfo.InvariantCulture);
                QualitySettings.lodBias = float.Parse(v[4], CultureInfo.InvariantCulture);
                QualitySettings.pixelLightCount = int.Parse(v[5]);
                SessionState.EraseString(Key);
            }
        }
    }
}

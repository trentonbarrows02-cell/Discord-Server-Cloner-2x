using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Scene-wide render settings that follow the game's quality preset (the grass itself scales in
    /// <see cref="GrassFieldRenderer"/>). Without this every preset ran Unity's Ultra level: 4 shadow cascades to 150 m and
    /// LOD bias 2, so a low-end PC on "Low" still paid for full sun shadows (shadow maps plus the camera depth pass that
    /// screen-space shadows need, which draws the grass a second time) and kept the forest on its full models far out.
    ///  - Low: no sun shadows, trees switch to lighter models / cards sooner.
    ///  - Medium: hard shadows, 1 cascade, 30 m.  High: soft, 2 cascades, 50 m, high-res maps.  Ultra: soft, 4 cascades, 100 m.
    /// </summary>
    public static class GraphicsProfile
    {
        public static GrassQuality? Applied { get; private set; }

        public static void Apply(GrassQuality q)
        {
            switch (q)
            {
                case GrassQuality.Low:
                    Set(ShadowQuality.Disable, ShadowResolution.Low, 1, 20f, 0.75f, 1);
                    break;
                case GrassQuality.Medium:
                    Set(ShadowQuality.HardOnly, ShadowResolution.Medium, 1, 30f, 1f, 1);
                    break;
                case GrassQuality.High:
                    Set(ShadowQuality.All, ShadowResolution.High, 2, 50f, 1.25f, 2);
                    break;
                default:
                    Set(ShadowQuality.All, ShadowResolution.High, 4, 100f, 2f, 4);
                    break;
            }
            Applied = q;
        }

        private static void Set(ShadowQuality shadows, ShadowResolution res, int cascades, float distance, float lodBias, int pixelLights)
        {
            QualitySettings.shadows = shadows;
            QualitySettings.shadowResolution = res;
            QualitySettings.shadowCascades = cascades;
            QualitySettings.shadowDistance = distance;
            QualitySettings.lodBias = lodBias;
            QualitySettings.pixelLightCount = pixelLights;
        }
    }
}

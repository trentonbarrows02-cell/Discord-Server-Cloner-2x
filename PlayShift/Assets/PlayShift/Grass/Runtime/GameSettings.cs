using System.Collections.Generic;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Player settings (resolution, display mode, VSync, field of view, grass quality). Saved in PlayerPrefs (per
    /// Windows user, tiny) and applied at startup. Edited through <see cref="GameSettingsMenu"/>.
    /// </summary>
    public static class GameSettings
    {
        private const string Key = "PlayShift.Settings.";

        // FOV is set as the vertical FOV of a 16:9 (landscape) screen; other aspect ratios keep the same horizontal view,
        // so a vertical (TikTok) window shows more above and below instead of zooming in.
        public const float MinFov = 50f, MaxFov = 100f, DefaultFov = 62f;
        public const float MinMouse = 0.2f, MaxMouse = 3f, DefaultMouse = 1f;
        private const float ReferenceAspect = 16f / 9f;

        public static int Width { get; private set; }
        public static int Height { get; private set; }
        public static FullScreenMode Mode { get; private set; } = FullScreenMode.Windowed;
        public static bool VSync { get; private set; } = true;
        /// <summary>Field of view setting: vertical degrees on a 16:9 screen (65 by default).</summary>
        public static float Fov { get; private set; } = DefaultFov;
        /// <summary>Mouse look speed multiplier (1 = default).</summary>
        public static float MouseSensitivity { get; private set; } = DefaultMouse;
        public static GrassQuality GrassQuality { get; private set; } = GrassQuality.High;
        /// <summary>True once the player has picked a grass quality (otherwise the scene's own setting is kept).</summary>
        public static bool HasGrassQuality { get; private set; }

        private static bool s_loaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void ApplyAtStartup()
        {
            Load();
            ApplyVSync();
            ApplyGrassQuality();
#if !UNITY_EDITOR
            // In builds, restore the saved window; first launch keeps the player-settings default (720x1280 windowed).
            if (PlayerPrefs.HasKey(Key + "Width")) ApplyDisplay();
#endif
        }

        public static void Load()
        {
            if (s_loaded) return;
            s_loaded = true;
            Width = PlayerPrefs.GetInt(Key + "Width", Screen.width);
            Height = PlayerPrefs.GetInt(Key + "Height", Screen.height);
            Mode = (FullScreenMode)PlayerPrefs.GetInt(Key + "Mode", (int)Screen.fullScreenMode);
            VSync = PlayerPrefs.GetInt(Key + "VSync", 1) == 1;
            // "FovV": the earlier "Fov" key stored a horizontal angle; it is not reused.
            Fov = Mathf.Clamp(PlayerPrefs.GetFloat(Key + "FovV", DefaultFov), MinFov, MaxFov);
            MouseSensitivity = Mathf.Clamp(PlayerPrefs.GetFloat(Key + "Mouse", DefaultMouse), MinMouse, MaxMouse);
            HasGrassQuality = PlayerPrefs.HasKey(Key + "GrassQuality");
            GrassQuality = (GrassQuality)PlayerPrefs.GetInt(Key + "GrassQuality", (int)GrassQuality.High);
        }

        public static void SetResolution(int width, int height)
        {
            Width = width; Height = height;
            PlayerPrefs.SetInt(Key + "Width", width);
            PlayerPrefs.SetInt(Key + "Height", height);
            ApplyDisplay();
        }

        public static void SetMode(FullScreenMode mode)
        {
            Mode = mode;
            PlayerPrefs.SetInt(Key + "Mode", (int)mode);
            ApplyDisplay();
        }

        public static void SetVSync(bool on)
        {
            VSync = on;
            PlayerPrefs.SetInt(Key + "VSync", on ? 1 : 0);
            ApplyVSync();
        }

        public static void SetFov(float verticalDegrees)
        {
            Fov = Mathf.Clamp(Mathf.Round(verticalDegrees), MinFov, MaxFov);
            PlayerPrefs.SetFloat(Key + "FovV", Fov);
        }

        public static void SetMouseSensitivity(float value)
        {
            MouseSensitivity = Mathf.Clamp(Mathf.Round(value * 20f) / 20f, MinMouse, MaxMouse);
            PlayerPrefs.SetFloat(Key + "Mouse", MouseSensitivity);
        }

        public static void SetGrassQuality(GrassQuality quality)
        {
            GrassQuality = quality;
            HasGrassQuality = true;
            PlayerPrefs.SetInt(Key + "GrassQuality", (int)quality);
            ApplyGrassQuality();
        }

        public static void Save() => PlayerPrefs.Save();

        private static void ApplyDisplay()
        {
            if (Width > 0 && Height > 0) Screen.SetResolution(Width, Height, Mode); // no effect inside the editor
        }

        private static void ApplyVSync()
        {
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            Application.targetFrameRate = -1; // VSync off = uncapped
        }

        private static void ApplyGrassQuality()
        {
            if (!HasGrassQuality) return;
            var r = Object.FindAnyObjectByType<GrassFieldRenderer>();
            if (r != null) r.Quality = GrassQuality; // procedural blades: switching quality costs nothing
        }

        /// <summary>Monitor modes plus vertical (TikTok) and common landscape presets; portrait first, then by size.</summary>
        public static List<Vector2Int> AvailableResolutions()
        {
            var set = new HashSet<Vector2Int>
            {
                new Vector2Int(540, 960), new Vector2Int(720, 1280), new Vector2Int(1080, 1920),
                new Vector2Int(1280, 720), new Vector2Int(1920, 1080),
            };
            foreach (var r in Screen.resolutions)
                if (r.width >= 640 && r.height >= 480) set.Add(new Vector2Int(r.width, r.height));
            var list = new List<Vector2Int>(set);
            list.Sort((a, b) =>
            {
                bool pa = a.y > a.x, pb = b.y > b.x;
                if (pa != pb) return pa ? -1 : 1;
                return (a.x * a.y).CompareTo(b.x * b.y);
            });
            return list;
        }

        /// <summary>Vertical FOV (what Unity's camera uses) for this aspect ratio: exactly the setting on 16:9, the same
        /// horizontal view on other shapes.</summary>
        public static float VerticalFov(float aspect, float maxVertical = 130f)
        {
            float halfH = Mathf.Atan(Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad) * ReferenceAspect);
            return Mathf.Min(maxVertical, 2f * Mathf.Atan(Mathf.Tan(halfH) / Mathf.Max(0.1f, aspect)) * Mathf.Rad2Deg);
        }
    }
}

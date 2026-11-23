using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// Builds the TextMeshPro font asset for the HUD counter from <see cref="SourceFontPath"/> (licences next to the fonts in
    /// Assets/PlayShift/Fonts). Printable ASCII is baked into a static atlas, so the HUD never
    /// rasterises glyphs at runtime (no CPU spikes while the counter changes every frame). Generous SDF padding
    /// leaves room for the thick outline.
    /// </summary>
    public static class HudFontBuilder
    {
        // Change this one path to swap the HUD font (then PlayShift > Grass > Rebuild HUD Font). Fonts in Assets/PlayShift/Fonts:
        //   LuckiestGuy-Regular.ttf (Apache 2.0, current), LilitaOne-Regular.ttf (SIL OFL 1.1)
        public const string SourceFontPath = "Assets/PlayShift/Fonts/LuckiestGuy-Regular.ttf";
        public const string AssetPath = "Assets/PlayShift/Grass/Resources/PlayShiftFonts/HudFont SDF.asset";

        [MenuItem("PlayShift/Grass/Rebuild HUD Font", priority = 30)]
        public static void RebuildFromMenu() => Ensure(force: true);

        /// <summary>Command line: Unity.exe -batchmode -quit -projectPath PlayShift -executeMethod PlayShift.Grass.Editor.HudFontBuilder.BuildForBatch</summary>
        public static void BuildForBatch()
        {
            bool ok = Ensure(force: true) != null;
            Debug.Log(ok ? "[PlayShift Grass] HUD FONT OK" : "[PlayShift Grass] HUD FONT FAILED");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        public static TMP_FontAsset Ensure(bool force = false)
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetPath);
            if (existing != null && !force) return existing;

            var font = AssetDatabase.LoadAssetAtPath<Font>(SourceFontPath);
            if (font == null)
            {
                Debug.LogError("[PlayShift Grass] HUD font source missing: " + SourceFontPath);
                return null;
            }
            if (existing != null) AssetDatabase.DeleteAsset(AssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));

            // 80 pt with 20 px padding: a wide distance field so the thick outline and the deep "3D" drop shadow stay
            // clean (padding/size 0.25); printable ASCII still fits one 1024x1024 Alpha8 atlas (1 MB).
            var fa = TMP_FontAsset.CreateFontAsset(font, 80, 20, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, false);
            fa.name = Path.GetFileNameWithoutExtension(AssetPath);
            var chars = new System.Text.StringBuilder();
            for (char c = ' '; c <= '~'; c++) chars.Append(c);
            if (!fa.TryAddCharacters(chars.ToString(), out string missing) && !string.IsNullOrEmpty(missing))
                Debug.LogWarning("[PlayShift Grass] HUD font is missing glyphs: " + missing);

            AssetDatabase.CreateAsset(fa, AssetPath);
            fa.atlasTexture.name = fa.name + " Atlas";
            AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa);
            fa.material.name = fa.name + " Material";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
            // Anything outside ASCII (e.g. gift usernames) falls back to TMP's default font.
            fa.atlasPopulationMode = AtlasPopulationMode.Static;
            EditorUtility.SetDirty(fa);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(AssetPath);
            fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetPath);

            // Material presets: thick outline + drop shadow (underlay) baked in. Shared by the texts that use them,
            // so no per-text material instances and no duplicate shadow text objects.
            // TikTok-game style: dark indigo outline and a solid indigo "extruded" shadow straight below.
            // Widths are in SDF units (relative to the 20 px padding): ~0.15 is a thick outline that keeps letters apart.
            CreatePreset(fa, LabelMaterialPath, outline: HudInk, outlineWidth: 0.17f, shadow: HudInk, shadowOffsetY: -0.45f);
            CreatePreset(fa, CounterMaterialPath, outline: HudInk, outlineWidth: 0.14f, shadow: HudInk, shadowOffsetY: -0.5f);
            AssetDatabase.SaveAssets();
            return fa;
        }

        /// <summary>Outline + shadow colour: dark indigo, softer than pure black and reads well over sky and grass.</summary>
        public static readonly Color HudInk = new Color(0.16f, 0.11f, 0.29f, 1f);

        public const string LabelMaterialPath = "Assets/PlayShift/Grass/Resources/PlayShiftFonts/HudFont SDF - Label.mat";
        public const string CounterMaterialPath = "Assets/PlayShift/Grass/Resources/PlayShiftFonts/HudFont SDF - Counter.mat";

        private static void CreatePreset(TMP_FontAsset fa, string path, Color outline, float outlineWidth, Color shadow, float shadowOffsetY)
        {
            AssetDatabase.DeleteAsset(path);
            var m = new Material(fa.material) { name = Path.GetFileNameWithoutExtension(path) };
            var sdf = Shader.Find("TextMeshPro/Distance Field");
            if (sdf != null) m.shader = sdf; // full SDF shader: outline + underlay
            // TMP centres the outline on the glyph edge; dilating the face by the same amount puts the whole outline
            // outside the letter, so the fill stays fat (chunky game-UI look) instead of being eaten by the outline.
            m.SetFloat("_FaceDilate", outlineWidth);
            m.SetColor("_OutlineColor", outline);
            m.SetFloat("_OutlineWidth", outlineWidth);
            m.SetFloat("_OutlineSoftness", 0f);
            // Crisp drop shadow straight below, the same shape as the outlined letter.
            m.EnableKeyword("UNDERLAY_ON");
            m.SetColor("_UnderlayColor", shadow);
            m.SetFloat("_UnderlayOffsetX", 0f);
            m.SetFloat("_UnderlayOffsetY", shadowOffsetY);
            m.SetFloat("_UnderlayDilate", outlineWidth * 2f); // same footprint as face + outline
            m.SetFloat("_UnderlaySoftness", 0f);
            AssetDatabase.CreateAsset(m, path);
        }
    }
}

using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// Builds the two TextMeshPro font assets for the HUD (licences next to the fonts in Assets/PlayShift/Fonts):
    ///  - HudFont SDF (<see cref="SourceFontPath"/>): GRASS LEFT, titles, buttons, names; presets Label (white text,
    ///    indigo outline) and Green (the GRASS title).
    ///  - NumberFont SDF (<see cref="NumberSourcePath"/>): the counter and gift amounts; preset Counter (green-on-green
    ///    edge, indigo shadow).
    /// Printable ASCII is baked into static atlases, so the HUD never rasterises glyphs at runtime (no CPU spikes while
    /// the counter changes every frame). Generous SDF padding leaves room for the thick outline and shadow.
    /// </summary>
    public static class HudFontBuilder
    {
        // Change these paths to swap fonts (then PlayShift > Grass > Rebuild HUD Font). Fonts in Assets/PlayShift/Fonts:
        //   LuckiestGuy (Apache 2.0), LilitaOne, TitanOne, Sniglet-ExtraBold, Coiny (SIL OFL 1.1)
        public const string SourceFontPath = "Assets/PlayShift/Fonts/LuckiestGuy-Regular.ttf";
        public const string NumberSourcePath = "Assets/PlayShift/Fonts/LilitaOne-Regular.ttf"; // owner's pick for the numbers

        public const string AssetPath = "Assets/PlayShift/Grass/Resources/PlayShiftFonts/HudFont SDF.asset";
        public const string NumberAssetPath = "Assets/PlayShift/Grass/Resources/PlayShiftFonts/NumberFont SDF.asset";
        public const string LabelMaterialPath = "Assets/PlayShift/Grass/Resources/PlayShiftFonts/HudFont SDF - Label.mat";
        public const string GreenMaterialPath = "Assets/PlayShift/Grass/Resources/PlayShiftFonts/HudFont SDF - Green.mat";
        public const string CounterMaterialPath = "Assets/PlayShift/Grass/Resources/PlayShiftFonts/NumberFont SDF - Counter.mat";

        /// <summary>Outline + shadow colour: dark indigo, softer than pure black and reads well over sky and grass.</summary>
        public static readonly Color HudInk = new Color(0.16f, 0.11f, 0.29f, 1f);

        /// <summary>Thin edge on the green numbers: a deeper green of the same hue.</summary>
        public static readonly Color CounterEdge = new Color(0.07f, 0.5f, 0.18f, 1f);

        private static string s_sourceOverride, s_numberOverride;

        [MenuItem("PlayShift/Grass/Rebuild HUD Font", priority = 30)]
        public static void RebuildFromMenu() => Ensure(force: true);

        /// <summary>
        /// Command line: Unity.exe -batchmode -quit -projectPath PlayShift -executeMethod PlayShift.Grass.Editor.HudFontBuilder.BuildForBatch
        /// Optional: -hudFont / -numberFont Assets/PlayShift/Fonts/Other.ttf to try fonts without editing code.
        /// </summary>
        public static void BuildForBatch()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-hudFont") s_sourceOverride = args[i + 1];
                if (args[i] == "-numberFont") s_numberOverride = args[i + 1];
            }
            bool ok = Ensure(force: true) != null;
            Debug.Log(ok ? "[PlayShift Grass] HUD FONT OK" : "[PlayShift Grass] HUD FONT FAILED");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        public static TMP_FontAsset Ensure(bool force = false)
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetPath);
            var existingNumbers = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(NumberAssetPath);
            if (existing != null && existingNumbers != null && !force) return existing;

            var hud = BuildFontAsset(s_sourceOverride ?? SourceFontPath, AssetPath);
            var numbers = BuildFontAsset(s_numberOverride ?? NumberSourcePath, NumberAssetPath);
            if (hud == null || numbers == null) return null;
            MakeTabularDigits(numbers);

            // Material presets: outline + drop shadow (underlay) baked in. Shared by the texts that use them, so no
            // per-text material instances and no duplicate shadow text objects. Widths are in SDF units (relative to
            // the 20 px padding): ~0.15 is a thick outline that keeps letters apart.
            CreatePreset(hud, LabelMaterialPath, outline: HudInk, outlineWidth: 0.17f, shadow: HudInk, shadowOffsetY: -0.45f);
            // GRASS title: green fill (vertex colour) with the numbers' green-on-green edge and indigo shadow.
            CreatePreset(hud, GreenMaterialPath, outline: CounterEdge, outlineWidth: 0.07f, shadow: HudInk, shadowOffsetY: -0.55f,
                         shadowDilate: 0.12f);
            // Numbers: no dark ring around each digit, just a thin deeper-green edge; the dark indigo only shows as the
            // solid shadow underneath.
            CreatePreset(numbers, CounterMaterialPath, outline: CounterEdge, outlineWidth: 0.07f, shadow: HudInk, shadowOffsetY: -0.55f,
                         shadowDilate: 0.12f);
            AssetDatabase.SaveAssets();
            return hud;
        }

        /// <summary>
        /// Gives the ten digits one shared advance width (tabular figures), each glyph centred in it. The counter is
        /// centred, so with proportional digits (a "1" is narrower than a "0") the whole number slid sideways every
        /// time a digit changed while cutting. Done once in the font asset: no runtime cost.
        /// </summary>
        private static void MakeTabularDigits(TMP_FontAsset fa)
        {
            fa.ReadFontAssetDefinition(); // builds characterLookupTable for a freshly loaded asset
            float widest = 0f;
            for (char c = '0'; c <= '9'; c++)
                if (fa.characterLookupTable.TryGetValue(c, out var ch)) widest = Mathf.Max(widest, ch.glyph.metrics.horizontalAdvance);
            if (widest <= 0f) return;
            for (char c = '0'; c <= '9'; c++)
            {
                if (!fa.characterLookupTable.TryGetValue(c, out var ch)) continue;
                var m = ch.glyph.metrics;
                m.horizontalBearingX += (widest - m.horizontalAdvance) * 0.5f;
                m.horizontalAdvance = widest;
                ch.glyph.metrics = m;
            }
            // Kerning pairs between digits would undo the equal widths.
            var table = fa.fontFeatureTable;
            if (table != null && table.glyphPairAdjustmentRecords != null)
            {
                var digitGlyphs = new System.Collections.Generic.HashSet<uint>();
                for (char c = '0'; c <= '9'; c++)
                    if (fa.characterLookupTable.TryGetValue(c, out var ch)) digitGlyphs.Add(ch.glyphIndex);
                table.glyphPairAdjustmentRecords.RemoveAll(r =>
                    digitGlyphs.Contains(r.firstAdjustmentRecord.glyphIndex) || digitGlyphs.Contains(r.secondAdjustmentRecord.glyphIndex));
            }
            fa.ReadFontAssetDefinition();
            EditorUtility.SetDirty(fa);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PlayShift Grass] {fa.name}: digits set to one width ({widest:F1}).");
        }

        private static TMP_FontAsset BuildFontAsset(string source, string path)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(source);
            if (font == null)
            {
                Debug.LogError("[PlayShift Grass] HUD font source missing: " + source);
                return null;
            }
            AssetDatabase.DeleteAsset(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // 80 pt with 20 px padding: a wide distance field so the thick outline and the deep "3D" drop shadow stay
            // clean (padding/size 0.25); printable ASCII still fits one 1024x1024 Alpha8 atlas (1 MB).
            var fa = TMP_FontAsset.CreateFontAsset(font, 80, 20, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, false);
            fa.name = Path.GetFileNameWithoutExtension(path);
            var chars = new System.Text.StringBuilder();
            for (char c = ' '; c <= '~'; c++) chars.Append(c);
            if (!fa.TryAddCharacters(chars.ToString(), out string missing) && !string.IsNullOrEmpty(missing))
                Debug.LogWarning($"[PlayShift Grass] {fa.name} is missing glyphs: {missing}");

            AssetDatabase.CreateAsset(fa, path);
            fa.atlasTexture.name = fa.name + " Atlas";
            AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa);
            fa.material.name = fa.name + " Material";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
            // Anything outside ASCII (e.g. gift usernames) falls back to TMP's default font.
            fa.atlasPopulationMode = AtlasPopulationMode.Static;
            EditorUtility.SetDirty(fa);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
            Debug.Log($"[PlayShift Grass] {fa.name} built from {source}");
            return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        }

        private static void CreatePreset(TMP_FontAsset fa, string path, Color outline, float outlineWidth, Color shadow, float shadowOffsetY,
                                         float shadowDilate = -1f)
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
            // Default: same footprint as face + outline. Smaller = the shadow peeks out mostly below the letter.
            m.SetFloat("_UnderlayDilate", shadowDilate >= 0f ? shadowDilate : outlineWidth * 2f);
            m.SetFloat("_UnderlaySoftness", 0f);
            AssetDatabase.CreateAsset(m, path);
        }
    }
}

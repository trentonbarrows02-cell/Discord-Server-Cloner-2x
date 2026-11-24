using UnityEditor;
using UnityEngine;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// Material assets for the Open Field forest (Resources/OpenField), with GPU instancing on:
    ///  - TreesLit: trees and bushes (full and lighter models), lit, Plants_BaseColor.
    ///  - TreeBillboards: distant tree cards, cut-out, TreeBillboards atlas.
    /// Saved as assets (not made in code) so builds keep the cut-out shader variant and the instancing variants.
    /// Menu: PlayShift > Grass > Build Forest Materials, or batch: -executeMethod
    /// PlayShift.Grass.Editor.ForestMaterialBuilder.BuildForBatch
    /// </summary>
    public static class ForestMaterialBuilder
    {
        private const string Dir = "Assets/PlayShift/Grass/Resources/OpenField/";

        [MenuItem("PlayShift/Grass/Build Forest Materials", priority = 40)]
        public static void Build()
        {
            AssetDatabase.Refresh();
            var standard = Shader.Find("Standard");
            Make("TreesLit", standard, "Plants_BaseColor.jpg", false);
            Make("TreeBillboards", standard, "TreeBillboards.png", true);
            AssetDatabase.SaveAssets();
            Debug.Log("[PlayShift Grass] Forest materials built.");
        }

        public static void BuildForBatch()
        {
            Build();
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        private static void Make(string name, Shader shader, string texture, bool cutout)
        {
            string path = Dir + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool isNew = m == null;
            if (isNew) m = new Material(shader) { name = name };
            m.shader = shader;
            m.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + texture);
            m.color = Color.white;
            m.SetFloat("_Glossiness", 0.05f);
            m.SetFloat("_Metallic", 0f);
            m.enableInstancing = true;
            if (cutout)
            {
                m.SetFloat("_Mode", 1f);
                m.SetFloat("_Cutoff", 0.5f);
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                m.SetInt("_ZWrite", 1);
                m.EnableKeyword("_ALPHATEST_ON");
                m.DisableKeyword("_ALPHABLEND_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.SetOverrideTag("RenderType", "TransparentCutout");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
            if (isNew) AssetDatabase.CreateAsset(m, path);
            else EditorUtility.SetDirty(m);
        }
    }
}

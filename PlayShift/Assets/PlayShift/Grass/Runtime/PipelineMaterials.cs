using UnityEngine;
using UnityEngine.Rendering;

namespace PlayShift.Grass
{
    /// <summary>Creates simple materials that work in either the Built-in pipeline or URP.</summary>
    public static class PipelineMaterials
    {
        public static bool IsUrp => GraphicsSettings.currentRenderPipeline != null;

        /// <summary>Opaque (or alpha-clipped) lit material with an optional texture.</summary>
        public static Material CreateLit(string name, Color color, Texture texture = null, bool alphaClip = false, float cutoff = 0.45f, bool doubleSided = false)
        {
            var shader = IsUrp ? Shader.Find("Universal Render Pipeline/Lit") : Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Diffuse");
            var m = new Material(shader) { name = name };
            if (IsUrp)
            {
                m.SetColor("_BaseColor", color);
                if (texture != null) m.SetTexture("_BaseMap", texture);
                m.SetFloat("_Smoothness", 0.1f);
                if (alphaClip)
                {
                    m.SetFloat("_AlphaClip", 1f);
                    m.SetFloat("_Cutoff", cutoff);
                    m.EnableKeyword("_ALPHATEST_ON");
                    m.renderQueue = (int)RenderQueue.AlphaTest;
                }
                if (doubleSided) m.SetFloat("_Cull", 0f);
            }
            else
            {
                m.color = color;
                if (texture != null) m.mainTexture = texture;
                m.SetFloat("_Glossiness", 0.1f);
                if (alphaClip)
                {
                    m.SetFloat("_Mode", 1f);
                    m.SetFloat("_Cutoff", cutoff);
                    m.EnableKeyword("_ALPHATEST_ON");
                    m.SetOverrideTag("RenderType", "TransparentCutout");
                    m.renderQueue = (int)RenderQueue.AlphaTest;
                }
                if (doubleSided && m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            }
            return m;
        }

        /// <summary>Unlit vertex-coloured particle material.</summary>
        public static Material CreateParticleUnlit(string name)
        {
            Shader shader = IsUrp ? Shader.Find("Universal Render Pipeline/Particles/Unlit") : Shader.Find("Particles/Standard Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var m = new Material(shader) { name = name };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            return m;
        }
    }
}

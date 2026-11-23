using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Blurs the game view behind the settings page. Quarter-resolution, a few separable Gaussian passes
    /// (well under 0.2 ms at 1080p). Enabled only while the menu is open; a disabled component costs nothing.
    /// The HUD canvas is drawn after the camera, so it stays sharp. Built-in render pipeline (OnRenderImage).
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public sealed class MenuBlurEffect : MonoBehaviour
    {
        [Tooltip("Resolution divider as a power of two: 2 = quarter size.")]
        [Range(0, 3)] [SerializeField] private int downsample = 2;
        [Range(1, 4)] [SerializeField] private int iterations = 3;
        [SerializeField] private float spread = 1.2f;

        private static readonly int BlurDirId = Shader.PropertyToID("_BlurDir");
        private Material _material;

        /// <summary>Adds (once) and switches the blur on the given camera.</summary>
        public static void Set(Camera cam, bool on)
        {
            if (cam == null) return;
            var blur = cam.GetComponent<MenuBlurEffect>();
            if (blur == null)
            {
                if (!on) return;
                blur = cam.gameObject.AddComponent<MenuBlurEffect>();
            }
            blur.enabled = on;
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (_material == null)
            {
                var shader = Shader.Find("Hidden/PlayShift/Blur");
                if (shader == null) { Graphics.Blit(src, dst); return; }
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            int w = Mathf.Max(1, src.width >> downsample), h = Mathf.Max(1, src.height >> downsample);
            var a = RenderTexture.GetTemporary(w, h, 0, src.format);
            var b = RenderTexture.GetTemporary(w, h, 0, src.format);
            a.filterMode = b.filterMode = FilterMode.Bilinear;
            Graphics.Blit(src, a); // bilinear downsample
            for (int i = 0; i < iterations; i++)
            {
                float s = spread * (1f + i);
                _material.SetVector(BlurDirId, new Vector4(s, 0f, 0f, 0f));
                Graphics.Blit(a, b, _material);
                _material.SetVector(BlurDirId, new Vector4(0f, s, 0f, 0f));
                Graphics.Blit(b, a, _material);
            }
            Graphics.Blit(a, dst);
            RenderTexture.ReleaseTemporary(a);
            RenderTexture.ReleaseTemporary(b);
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}

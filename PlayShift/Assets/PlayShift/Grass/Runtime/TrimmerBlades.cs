using System.Collections.Generic;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Spins the trimmer's cutting head(s). Turning on accelerates smoothly, turning off coasts down; all
    /// timing uses deltaTime, so it is frame-rate independent. At speed, a soft motion-blur ring fades in
    /// over each head so it reads as a fast spinning blade without strobing. Supports any number of heads
    /// (double-bladed / upgraded trimmers).
    /// </summary>
    public sealed class TrimmerBlades : MonoBehaviour
    {
        [Tooltip("Spinning parts (the CutterHead of each head). TrimmerViewmodel fills this at runtime.")]
        [SerializeField] private List<Transform> spinners = new List<Transform>();
        [Tooltip("Visual spin speed at full power, revolutions per second.")]
        [SerializeField] private float maxRps = 7f;
        [Tooltip("Seconds to reach ~95% speed.")]
        [SerializeField] private float spinUpTime = 1.1f;
        [Tooltip("Seconds to coast down to ~5%.")]
        [SerializeField] private float spinDownTime = 1.9f;
        [SerializeField] private bool startOn;
        [Header("Motion blur")]
        [SerializeField] private bool motionBlurRing = true;
        [SerializeField] private float blurRadius = 0.65f;
        [Range(0f, 1f)] [SerializeField] private float blurOpacity = 0.5f;

        private readonly List<Renderer> _blurRings = new List<Renderer>();
        private MaterialPropertyBlock _mpb;
        private static Texture2D s_blurTex;
        private static Material s_blurMat;

        /// <summary>Trimmer switched on (blades driven). Off = coasting down.</summary>
        public bool On { get; set; }

        /// <summary>0..1 blade speed.</summary>
        public float Rpm01 { get; private set; }

        /// <summary>Extra speed factor from gameplay modifiers (speed boost / slow).</summary>
        public float SpeedMultiplier { get; set; } = 1f;

        /// <summary>0..1 vibration for the viewmodel: idle hum when running, more under load.</summary>
        public float Vibration { get; private set; }

        /// <summary>Set by the viewmodel from GrassCutter.Load.</summary>
        public float Load { get; set; }

        public IReadOnlyList<Transform> Spinners => spinners;

        private void Awake() => On = startOn;

        public void SetSpinners(IEnumerable<Transform> heads)
        {
            spinners.Clear();
            spinners.AddRange(heads);
            foreach (var r in _blurRings) if (r != null) Destroy(r.gameObject);
            _blurRings.Clear();
            if (!motionBlurRing) return;
            foreach (var s in spinners)
                if (s != null) _blurRings.Add(CreateBlurRing(s.parent != null ? s.parent : s));
        }

        public void Toggle() => On = !On;

        private void Update()
        {
            float dt = Time.deltaTime;
            float target = On ? 1f : 0f;
            // Exponential approach: 3 time-constants = 95%.
            float tau = (On ? spinUpTime : spinDownTime) / 3f;
            Rpm01 = Mathf.Lerp(Rpm01, target, 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, tau)));
            if (!On && Rpm01 < 0.01f) Rpm01 = 0f;

            float degPerSec = Rpm01 * maxRps * SpeedMultiplier * 360f * (1f - 0.25f * Load);
            foreach (var s in spinners)
                if (s != null) s.Rotate(0f, degPerSec * dt, 0f, Space.Self);

            Vibration = Rpm01 * (0.35f + 0.65f * Load);

            if (_blurRings.Count > 0)
            {
                if (_mpb == null) _mpb = new MaterialPropertyBlock();
                float a = Mathf.Clamp01((Rpm01 - 0.3f) / 0.7f) * blurOpacity;
                foreach (var r in _blurRings)
                {
                    if (r == null) continue;
                    r.enabled = a > 0.01f;
                    r.GetPropertyBlock(_mpb);
                    _mpb.SetColor("_Color", new Color(1f, 1f, 1f, a));
                    r.SetPropertyBlock(_mpb);
                    r.transform.Rotate(0f, 0f, degPerSec * 0.37f * dt, Space.Self);
                }
            }
        }

        private Renderer CreateBlurRing(Transform headParent)
        {
            var go = new GameObject("MotionBlurRing");
            go.transform.SetParent(headParent, false);
            go.transform.localPosition = new Vector3(0f, 0.04f / Mathf.Max(0.01f, headParent.lossyScale.y), 0f);
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            float s = blurRadius * 2f / Mathf.Max(0.01f, headParent.lossyScale.x);
            go.transform.localScale = new Vector3(s, s, s);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = BlurMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.enabled = false;
            return mr;
        }

        private static Material BlurMaterial
        {
            get
            {
                if (s_blurMat != null) return s_blurMat;
                // Sprites/Default is always included in builds and renders in Built-in and URP.
                s_blurMat = new Material(Shader.Find("Sprites/Default")) { name = "TrimmerMotionBlur", mainTexture = BlurTexture };
                return s_blurMat;
            }
        }

        private static Texture2D BlurTexture
        {
            get
            {
                if (s_blurTex != null) return s_blurTex;
                const int n = 128;
                s_blurTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "TrimmerBlurRing", wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                        float r = Mathf.Sqrt(dx * dx + dy * dy), th = Mathf.Atan2(dy, dx);
                        float ring = (r < 1f && r > 0.3f) ? Mathf.Exp(-Mathf.Pow((r - 0.78f) / 0.2f, 2f)) : 0f;
                        float a = ring * (0.55f + 0.45f * Mathf.Cos(3f * th)) * 0.9f;
                        px[y * n + x] = new Color32(215, 220, 228, (byte)(a * 255f));
                    }
                s_blurTex.SetPixels32(px);
                s_blurTex.Apply(false, true);
                return s_blurTex;
            }
        }
    }
}

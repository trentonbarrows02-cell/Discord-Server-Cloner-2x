using System.Collections.Generic;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Spins the trimmer's cutting head(s). Turning on accelerates smoothly, turning off coasts down; all
    /// timing uses deltaTime, so it is frame-rate independent. As the blades speed up a motion-blur disc
    /// (sized to the blade tips, at blade height) fades in, and at full speed the solid blades are hidden: at
    /// 7 turns a second a 4-blade disc strobes at 30-60 fps (it seems to crawl or run backwards), a real
    /// trimmer reads as a blurred disc. Supports any number of heads (double-bladed / upgraded trimmers).
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
        [Range(0f, 1f)] [SerializeField] private float blurOpacity = 0.9f;

        [Tooltip("Above this speed (0..1) the solid blades are hidden and only the blur disc shows.")]
        [Range(0f, 1f)] [SerializeField] private float hideBladesAbove = 0.62f;

        private readonly List<Renderer> _blurRings = new List<Renderer>();
        private readonly List<Renderer> _bladeRenderers = new List<Renderer>();
        private bool _bladesShown = true;
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
            SetBladesShown(true);
            _bladeRenderers.Clear();
            if (!motionBlurRing) return;
            foreach (var s in spinners)
            {
                if (s == null) continue;
                _blurRings.Add(CreateBlurRing(s));
                // Only a blades-only spinner is hidden at speed (a whole-head spinner keeps showing).
                if (s.parent != null && s.name != "CutterHead")
                    foreach (var r in s.GetComponentsInChildren<Renderer>(true))
                        if (r.name != "MotionBlurRing") { r.enabled = true; _bladeRenderers.Add(r); }
            }
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

            if (_bladeRenderers.Count > 0) SetBladesShown(Rpm01 < hideBladesAbove);

            if (_blurRings.Count > 0)
            {
                if (_mpb == null) _mpb = new MaterialPropertyBlock();
                // Blur builds up with speed and is full by the time the solid blades are hidden.
                float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, hideBladesAbove, Rpm01)) * blurOpacity;
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

        private void SetBladesShown(bool show)
        {
            if (_bladesShown == show) return;
            _bladesShown = show;
            foreach (var r in _bladeRenderers) if (r != null) r.enabled = show;
        }

        /// <summary>Blur disc on the blade plane, as wide as the blade tips (measured from the spinner's mesh).</summary>
        private Renderer CreateBlurRing(Transform spinner)
        {
            var headParent = spinner.parent != null ? spinner.parent : spinner;
            float tip = 0f, bladeY = 0f;
            var smf = spinner.GetComponent<MeshFilter>();
            if (smf != null && smf.sharedMesh != null)
            {
                var b = smf.sharedMesh.bounds;
                tip = Mathf.Max(b.extents.x + Mathf.Abs(b.center.x), b.extents.z + Mathf.Abs(b.center.z)) * spinner.lossyScale.x;
                bladeY = b.center.y;
            }
            float radius = tip > 0.05f ? tip * 1.02f : blurRadius;
            var go = new GameObject("MotionBlurRing");
            go.transform.SetParent(headParent, false);
            // Just above the blades' mid-plane so the disc covers them without floating above.
            bool own = spinner != headParent;
            Vector3 local = own ? spinner.localPosition : Vector3.zero;
            local.y += (own ? bladeY * spinner.localScale.y : 0f) + 0.006f / Mathf.Max(0.01f, headParent.lossyScale.y);
            go.transform.localPosition = local;
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            float s = radius * 2f / Mathf.Max(0.01f, headParent.lossyScale.x);
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
                const int n = 256;
                s_blurTex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "TrimmerBlurRing", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                        float r = Mathf.Sqrt(dx * dx + dy * dy), th = Mathf.Atan2(dy, dx);
                        // Disc from under the cap out to the blade tips, soft anti-aliased rim.
                        float disc = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.32f, r)) *
                                     (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.965f, 1f, r)));
                        // Smeared steel: denser toward the tips (blades are wider there), faint 4-blade lobes,
                        // fine concentric speed rings and a bright band where the blade edges catch the light.
                        float body = 0.34f + 0.26f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0.9f, r));
                        float lobes = 0.88f + 0.12f * Mathf.Cos(4f * th);
                        float rings = 0.9f + 0.1f * Mathf.Sin(r * 70f);
                        float band = Mathf.Exp(-Mathf.Pow((r - 0.9f) / 0.05f, 2f));
                        float a = disc * Mathf.Clamp01(body * lobes * rings + 0.08f * band);
                        float shade = Mathf.Lerp(0.8f, 0.95f, band) * (0.92f + 0.08f * Mathf.Cos(2f * th + 0.6f));
                        px[y * n + x] = new Color32((byte)(172 * shade), (byte)(178 * shade), (byte)(186 * shade), (byte)(a * 255f));
                    }
                s_blurTex.SetPixels32(px);
                s_blurTex.Apply(true, true);
                return s_blurTex;
            }
        }
    }
}

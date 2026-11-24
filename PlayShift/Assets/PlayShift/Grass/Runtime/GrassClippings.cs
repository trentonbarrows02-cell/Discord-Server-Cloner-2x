using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Lightweight flying grass clippings. One pooled ParticleSystem (fixed max particle count, no
    /// GameObjects per particle). Small green/yellow slivers are thrown tangentially off the spinning blade,
    /// plus the head's own movement; more for dense, tall grass, few for short grass. They come off the front of
    /// the blade disc (where it bites into uncut grass) while the head moves, all round it when standing still.
    /// Chunky and readable on a phone screen: leafy flakes, chopped bits and long slivers in greens with some
    /// darker olive/brown, sprayed up and back toward the player as well as sideways off the blades. They tumble,
    /// fall and fade quickly. No smoke or snow.
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public sealed class GrassClippings : MonoBehaviour
    {
        [SerializeField] private GrassCutter cutter;
        [Tooltip("Particle material (unlit, vertex colour). Created at runtime when empty.")]
        [SerializeField] private Material clippingMaterial;
        [SerializeField] private int maxParticles = 700;
        [Tooltip("Particles per 100 grass units cut, before the height factor.")]
        [SerializeField] private float perHundredUnits = 22f;
        [SerializeField] private int maxPerFrame = 40;
        [Tooltip("-1 = clockwise seen from above, which matches TrimmerBlades rotating about +Y in Unity.")]
        [SerializeField] private float spinDirection = -1f;

        private static readonly Color32[] Palette =
        {
            new Color32(88, 150, 46, 255), new Color32(104, 165, 52, 255), new Color32(76, 136, 40, 255),
            new Color32(120, 172, 60, 255), new Color32(64, 120, 34, 255),
            new Color32(44, 96, 26, 255), new Color32(52, 104, 30, 255), new Color32(38, 84, 22, 255), new Color32(58, 112, 34, 255),
        };

        private Transform _cam;

        private ParticleSystem _ps;
        private float _carry;

        private void Awake()
        {
            if (cutter == null) cutter = GetComponentInParent<GrassCutter>();
            _ps = GetComponent<ParticleSystem>();
            Configure();
        }

        private void OnEnable() { if (cutter != null) cutter.GrassCut += OnGrassCut; }
        private void OnDisable() { if (cutter != null) cutter.GrassCut -= OnGrassCut; }

        private void Configure()
        {
            _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = _ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.55f, 1.15f);
            main.startSize3D = true;
            main.startRotation3D = true;
            var emission = _ps.emission;
            emission.enabled = false;
            var shape = _ps.shape;
            shape.enabled = false;

            var limit = _ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.drag = 2.2f;
            limit.multiplyDragByParticleSize = false;
            limit.multiplyDragByParticleVelocity = false;

            var rot = _ps.rotationOverLifetime;
            rot.enabled = true;
            rot.separateAxes = true;
            rot.x = new ParticleSystem.MinMaxCurve(-20f, 20f);
            rot.y = new ParticleSystem.MinMaxCurve(-20f, 20f);
            rot.z = new ParticleSystem.MinMaxCurve(-20f, 20f);

            var size = _ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.75f, 1f), new Keyframe(1f, 0f)));

            var col = _ps.collision;
            col.enabled = true;
            col.type = ParticleSystemCollisionType.Planes;
            col.dampen = 0.9f;
            col.bounce = 0.1f;
            col.lifetimeLoss = 0f;

            var r = GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            r.alignment = ParticleSystemRenderSpace.Local;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (clippingMaterial == null) clippingMaterial = PipelineMaterials.CreateParticleUnlit("GrassClippings");
            r.sharedMaterial = clippingMaterial;
            _ps.Play();
        }

        private void OnGrassCut(Vector3 head, Vector3 headVel, long units, float grassHeightM, float radius)
        {
            // Ground plane for cheap particle collision.
            var m = GrassLevelManager.Instance;
            if (m != null)
            {
                var col = _ps.collision;
                if (col.planeCount == 0) col.AddPlane(m.FieldTransform);
            }

            float tall = Mathf.Clamp01(grassHeightM / 1.5f);
            _carry += units / 100f * perHundredUnits * (0.5f + tall);
            int n = Mathf.Min(maxPerFrame, (int)_carry);
            _carry -= n;
            if (n <= 0) return;

            var p = new ParticleSystem.EmitParams();
            float len = Mathf.Lerp(0.03f, 0.06f, tall);
            // Leading edge: the side of the disc moving into uncut grass.
            var flat = new Vector2(headVel.x, headVel.z);
            float lead = Mathf.Atan2(flat.y, flat.x);
            float spread = flat.magnitude > 0.25f ? 1.25f : Mathf.PI;
            // Part of the spray comes back toward the player (string trimmers throw debris at the operator).
            if (_cam == null && Camera.main != null) _cam = Camera.main.transform;
            Vector3 toPlayer = Vector3.zero;
            if (_cam != null)
            {
                toPlayer = _cam.position - head;
                toPlayer.y = 0f;
                toPlayer = toPlayer.sqrMagnitude > 1e-4f ? toPlayer.normalized : Vector3.zero;
            }
            for (int i = 0; i < n; i++)
            {
                float phi = lead + Random.Range(-spread, spread);
                float rr = radius * Random.Range(0.6f, 1f);
                var dir = new Vector3(Mathf.Cos(phi), 0f, Mathf.Sin(phi));
                var tangent = new Vector3(-dir.z, 0f, dir.x) * spinDirection;
                float sp = Random.Range(2.2f, 5.4f);
                p.position = head + dir * rr + Vector3.up * Random.Range(0.04f, 0.06f + 0.4f * tall);
                // Flung out off the blade tips: along the blade's path and outward from the head, a little up.
                p.velocity = tangent * sp + dir * Random.Range(1.4f, 2.6f) + Vector3.up * Random.Range(1.0f, 3.0f) + headVel * 0.6f
                             + toPlayer * Random.Range(0.2f, 0.9f);
                // Leafy flakes, chopped bits and long slivers, big enough to read at stream resolution.
                float kind = Random.value;
                p.startSize3D = kind < 0.35f ? new Vector3(Random.Range(0.05f, 0.09f), Random.Range(0.05f, 0.095f), 1f)
                              : kind < 0.7f ? new Vector3(0.02f, len * Random.Range(0.8f, 1.3f), 1f)
                                            : new Vector3(0.016f, len * Random.Range(1.6f, 2.6f), 1f);
                p.rotation3D = new Vector3(Random.value * 360f, Random.value * 360f, Random.value * 360f);
                p.startColor = Random.value < 0.5f ? Palette[Random.Range(5, Palette.Length)] : Palette[Random.Range(0, 5)];
                p.startLifetime = Random.Range(0.55f, 1.15f);
                _ps.Emit(p, 1);
            }
        }
    }
}

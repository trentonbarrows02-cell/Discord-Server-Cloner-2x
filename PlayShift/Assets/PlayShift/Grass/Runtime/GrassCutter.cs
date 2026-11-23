using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>World-space state of one cutting head, shared with the grass renderer (bending) and effects.</summary>
    public struct CutterHeadState
    {
        public Vector3 Position;
        public float Radius;
        public float Spin;
    }

    /// <summary>
    /// Cuts grass under one or more cutting heads. Each frame every head sweeps a capsule from its previous
    /// position to its current one, so GRASS LEFT stays exact at any speed or frame rate. Only spinning
    /// blades cut (when a <see cref="TrimmerBlades"/> is assigned). Batched per frame: one capsule per head.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class GrassCutter : MonoBehaviour
    {
        [Tooltip("Defaults to GrassLevelManager.Instance.")]
        [SerializeField] private GrassLevelManager manager;
        [Tooltip("Cutting head centres (on the ground). Defaults to this transform. TrimmerViewmodel fills this at runtime.")]
        [SerializeField] private List<Transform> heads = new List<Transform>();
        [Tooltip("Cutting radius in metres; match the blade tips of the trimmer head.")]
        [Min(0.05f)] [SerializeField] private float cutRadius = 0.65f;
        [Tooltip("Optional. When set, grass is only cut while the blades spin at least this fast.")]
        [SerializeField] private TrimmerBlades blades;
        [Range(0f, 1f)] [SerializeField] private float minSpinToCut = 0.55f;
        [Tooltip("A jump larger than this in one frame (respawn/teleport) does not cut a line across the lawn.")]
        [SerializeField] private float teleportDistance = 8f;
        [Tooltip("Only cut while the head is within this height above the lawn (metres). 0 = always.")]
        [SerializeField] private float maxHeightAboveLawn = 0.6f;

        private static readonly List<CutterHeadState> s_heads = new List<CutterHeadState>();
        private static int s_headsFrame = -1;

        private readonly List<Vector2> _last = new List<Vector2>();
        private readonly List<Vector3> _lastWorld = new List<Vector3>();
        private readonly List<bool> _hasLast = new List<bool>();
        private int _lastLevel = -1;

        /// <summary>All cutting heads this frame (read by GrassFieldRenderer for bending).</summary>
        public static IReadOnlyList<CutterHeadState> ActiveHeads
        {
            get
            {
                if (s_headsFrame != Time.frameCount) { s_heads.Clear(); s_headsFrame = Time.frameCount; }
                return s_heads;
            }
        }

        /// <summary>Raised per head that cut something: (head world position, head velocity, units cut, grown grass height m, radius).</summary>
        public event Action<Vector3, Vector3, long, float, float> GrassCut;

        public float BaseRadius { get => cutRadius; set => cutRadius = Mathf.Max(0.05f, value); }
        public float CutRadius => cutRadius * (Manager != null ? Manager.Modifiers.RadiusMultiplier : 1f);

        /// <summary>Smoothed 0..1 "how hard the blades are working" (thick, tall grass = high). Drives vibration, resistance and motor strain.</summary>
        public float Load { get; private set; }

        /// <summary>Units cut by this cutter during the current level.</summary>
        public long UnitsCutThisLevel { get; private set; }

        public List<Transform> Heads => heads;

        private GrassLevelManager Manager => manager != null ? manager : GrassLevelManager.Instance;

        public void SetHeads(IEnumerable<Transform> newHeads)
        {
            heads.Clear();
            heads.AddRange(newHeads);
        }

        private void OnEnable() => _hasLast.Clear();

        private void Update()
        {
            var m = Manager;
            if (heads.Count == 0) heads.Add(transform);
            while (_last.Count < heads.Count) { _last.Add(default); _lastWorld.Add(default); _hasLast.Add(false); }

            float spin = blades != null ? blades.Rpm01 : 1f;
            float radius = CutRadius;
            var list = (List<CutterHeadState>)ActiveHeads;
            foreach (var h in heads)
                if (h != null && h.gameObject.activeInHierarchy)
                    list.Add(new CutterHeadState { Position = h.position, Radius = radius, Spin = spin });

            if (m == null || m.Progression == null || !m.Progression.IsPlaying) { ResetSweep(); DecayLoad(0f); return; }
            if (_lastLevel != m.Progression.CurrentLevelIndex)
            {
                _lastLevel = m.Progression.CurrentLevelIndex;
                UnitsCutThisLevel = 0;
                ResetSweep();
            }

            bool cutting = spin >= minSpinToCut;
            var field = m.Field;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            long totalCut = 0;
            float grassHeightM = field.CurrentMaxHeightCm * 0.01f;

            for (int i = 0; i < heads.Count; i++)
            {
                var h = heads[i];
                if (h == null || !h.gameObject.activeInHierarchy) { _hasLast[i] = false; continue; }
                Vector3 world = h.position;
                Vector3 local = m.FieldTransform.InverseTransformPoint(world);
                var now = new Vector2(local.x, local.z);
                bool onLawn = maxHeightAboveLawn <= 0f || local.y <= maxHeightAboveLawn;

                long cut = 0;
                if (cutting && onLawn)
                {
                    if (_hasLast[i] && (now - _last[i]).sqrMagnitude <= teleportDistance * teleportDistance)
                        cut = field.CutCapsule(_last[i].x, _last[i].y, now.x, now.y, radius);
                    else
                        cut = field.CutCircle(now.x, now.y, radius);
                }

                if (cut > 0)
                {
                    Vector3 vel = _hasLast[i] ? (world - _lastWorld[i]) / dt : Vector3.zero;
                    GrassCut?.Invoke(world, vel, cut, grassHeightM, radius);
                }
                totalCut += cut;
                _last[i] = now;
                _lastWorld[i] = world;
                _hasLast[i] = true;
            }

            UnitsCutThisLevel += totalCut;
            // Load: units per second relative to a brisk cut, weighted by how tall the grass is.
            float tallness = Mathf.Clamp01(grassHeightM / 1.5f);
            float target = totalCut > 0 ? Mathf.Clamp01(totalCut / (dt * 40000f) * (0.4f + 0.8f * tallness)) : 0f;
            DecayLoad(target);
        }

        private void DecayLoad(float target) => Load = Mathf.Lerp(Load, target, 1f - Mathf.Exp(-6f * Time.deltaTime));

        private void ResetSweep()
        {
            for (int i = 0; i < _hasLast.Count; i++) _hasLast[i] = false;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.85f, 0.1f, 0.9f);
            var list = heads.Count > 0 ? heads : new List<Transform> { transform };
            foreach (var h in list)
            {
                if (h == null) continue;
                const int seg = 32;
                for (int i = 0; i < seg; i++)
                {
                    float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                    Gizmos.DrawLine(h.position + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * cutRadius,
                                    h.position + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * cutRadius);
                }
            }
        }
    }
}

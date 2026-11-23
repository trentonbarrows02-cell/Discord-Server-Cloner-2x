using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Put this on the mower (or its blade deck). Every frame it sweeps a capsule from last frame's position
    /// to this frame's, so GRASS LEFT is exact no matter how fast the mower moves or how low the frame rate.
    /// </summary>
    public sealed class GrassCutter : MonoBehaviour
    {
        [Tooltip("Defaults to GrassLevelManager.Instance.")]
        [SerializeField] private GrassLevelManager manager;
        [Tooltip("Centre of the cutting deck. Defaults to this transform.")]
        [SerializeField] private Transform cutPoint;
        [Tooltip("Cutting radius in metres.")]
        [Min(0.05f)] [SerializeField] private float cutRadius = 0.6f;
        [Tooltip("A jump larger than this in one frame (respawn/teleport) does not cut a line across the lawn.")]
        [SerializeField] private float teleportDistance = 8f;
        [Tooltip("Optional: only cut while the deck is within this height above the lawn (metres). 0 = always.")]
        [SerializeField] private float maxHeightAboveLawn = 0f;

        private Vector2 _last;
        private bool _hasLast;
        private int _lastLevel = -1;

        public float CutRadius
        {
            get => cutRadius;
            set => cutRadius = Mathf.Max(0.05f, value);
        }

        /// <summary>Units cut by this cutter during the current level.</summary>
        public long UnitsCutThisLevel { get; private set; }

        private void OnEnable() => _hasLast = false;

        private void Update()
        {
            var m = manager != null ? manager : GrassLevelManager.Instance;
            if (m == null || m.Progression == null || !m.Progression.IsPlaying) { _hasLast = false; return; }

            if (_lastLevel != m.Progression.CurrentLevelIndex)
            {
                _lastLevel = m.Progression.CurrentLevelIndex;
                UnitsCutThisLevel = 0;
                _hasLast = false;
            }

            Transform p = cutPoint != null ? cutPoint : transform;
            Vector3 local = m.FieldTransform.InverseTransformPoint(p.position);
            if (maxHeightAboveLawn > 0f && local.y > maxHeightAboveLawn) { _hasLast = false; return; }

            var now = new Vector2(local.x, local.z);
            var field = m.Field;
            long cut;
            if (_hasLast && (now - _last).sqrMagnitude <= teleportDistance * teleportDistance)
                cut = field.CutCapsule(_last.x, _last.y, now.x, now.y, cutRadius);
            else
                cut = field.CutCircle(now.x, now.y, cutRadius);

            UnitsCutThisLevel += cut;
            _last = now;
            _hasLast = true;
        }

        private void OnDrawGizmosSelected()
        {
            Transform p = cutPoint != null ? cutPoint : transform;
            Gizmos.color = new Color(1f, 0.85f, 0.1f, 0.9f);
            const int seg = 32;
            Vector3 c = p.position;
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                Gizmos.DrawLine(c + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * cutRadius,
                                c + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * cutRadius);
            }
        }
    }
}

using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Makes the trimmer model feel alive: spins its "CutterHead" child while a level is being played
    /// and adds a small side-to-side sweep like a hand-held string trimmer. Purely visual; cutting is done
    /// by <see cref="GrassCutter"/>.
    /// </summary>
    public sealed class TrimmerVisuals : MonoBehaviour
    {
        [Tooltip("Child that spins. Found by name ('CutterHead') when left empty.")]
        [SerializeField] private Transform cutterHead;
        [SerializeField] private string cutterHeadName = "CutterHead";
        [Tooltip("Head speed while cutting, revolutions per second.")]
        [SerializeField] private float spinRps = 22f;
        [Tooltip("Head speed when the level is not being played.")]
        [SerializeField] private float idleRps = 0.5f;
        [Tooltip("Optional transform that sweeps left/right (usually the visual model, not the object the cutter is on).")]
        [SerializeField] private Transform sweepRoot;
        [SerializeField] private float sweepDegrees = 7f;
        [SerializeField] private float sweepHz = 0.9f;

        private float _currentRps;
        private Quaternion _sweepBase;

        private void Awake()
        {
            if (cutterHead == null) cutterHead = FindDeep(transform, cutterHeadName);
            if (cutterHead == null)
                Debug.LogWarning($"[PlayShift Grass] TrimmerVisuals: no child named '{cutterHeadName}' to spin.", this);
            if (sweepRoot != null) _sweepBase = sweepRoot.localRotation;
        }

        private void Update()
        {
            var m = GrassLevelManager.Instance;
            bool working = m != null && m.Progression != null && m.Progression.IsPlaying;
            float target = working ? spinRps : idleRps;
            _currentRps = Mathf.MoveTowards(_currentRps, target, Mathf.Max(spinRps, 1f) * 1.5f * Time.deltaTime);

            if (cutterHead != null)
                cutterHead.Rotate(0f, _currentRps * 360f * Time.deltaTime, 0f, Space.Self);

            if (sweepRoot != null)
            {
                float angle = working ? Mathf.Sin(Time.time * sweepHz * Mathf.PI * 2f) * sweepDegrees : 0f;
                sweepRoot.localRotation = _sweepBase * Quaternion.Euler(0f, angle, 0f);
            }
        }

        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}

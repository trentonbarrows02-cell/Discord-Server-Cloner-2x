using System.Collections.Generic;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// First-person trimmer with no hands, arms or body: the tool floats in its operating position, its
    /// shaft entering from the bottom-right of the screen and its cutting head on the ground ahead.
    ///
    /// The shaft follows the player's yaw with a slight lag (smooth turning), sweeps left/right while
    /// cutting, sways with walking, hums when running and vibrates more and pulls back a little in thick
    /// grass. The cutting head is detached from the shaft so it stays level with the ground and is the exact
    /// point the <see cref="GrassCutter"/> cuts at, so the cut width always matches the visible head.
    /// Extra heads (multi-blade upgrades/modifiers) are spawned beside it on a crossbar.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public sealed class TrimmerViewmodel : MonoBehaviour
    {
        [SerializeField] private FirstPersonController controller;
        [SerializeField] private GrassCutter cutter;
        [SerializeField] private TrimmerBlades blades;
        [Tooltip("The trimmer model (e.g. Assets/PlayShift/Models/Trimmer.glb). Must contain a child named 'CutterHead'.")]
        [SerializeField] private GameObject trimmerModel;
        [SerializeField] private string cutterHeadName = "CutterHead";
        [SerializeField] private float modelScale = 1.85f;
        [Tooltip("Lengthens only the shaft (model Z) so it reaches from the head on the ground up to the bottom-right corner. The cutting head keeps its real size.")]
        [SerializeField] private float shaftStretch = 1.7f;

        [Header("Operating position (player space, metres/degrees)")]
        [Tooltip("Head position: x = right, z = forward.")]
        [SerializeField] private Vector2 headOffset = new Vector2(0.05f, 1.4f);
        [Tooltip("Shaft rise: raises the handle end towards the player.")]
        [SerializeField] private float shaftPitch = 17f;
        [Tooltip("Swings the handle end to the right so the shaft enters from the bottom-right of the screen.")]
        [SerializeField] private float handleSwing = 34f;
        [SerializeField] private float headHeight = 0.02f;

        [Header("Motion")]
        [SerializeField] private float sweepDegrees = 24f;
        [SerializeField] private float sweepHz = 0.55f;
        [SerializeField] private float turnLag = 7f;
        [SerializeField] private float walkSway = 0.03f;
        [SerializeField] private float vibrationMetres = 0.006f;
        [SerializeField] private float thickGrassPullBack = 0.06f;

        [Header("Multi-blade")]
        [SerializeField] private float extraHeadSpacing = 1.4f;
        [SerializeField] private Material crossbarMaterial;

        private Transform _rig, _bodyPivot, _mainHeadRoot, _mainSpinner;
        private readonly List<Transform> _extraRoots = new List<Transform>();
        private readonly List<Transform> _extraSpinners = new List<Transform>();
        private float _yaw, _sweepPhase, _sweep, _walkPhase;
        private int _extraCount = -1;
        private Vector3 _prevHead;

        /// <summary>World position of the main cutting head (on the ground).</summary>
        public Vector3 HeadPosition => _mainHeadRoot != null ? _mainHeadRoot.position : transform.position;
        public Vector3 HeadVelocity { get; private set; }

        private void Awake()
        {
            if (controller == null) controller = GetComponent<FirstPersonController>();
            if (cutter == null) cutter = GetComponent<GrassCutter>();
            if (blades == null) blades = GetComponent<TrimmerBlades>();
        }

        private void Start()
        {
            _yaw = transform.eulerAngles.y;
            _rig = new GameObject("TrimmerRig").transform;
            _bodyPivot = new GameObject("ShaftPivot").transform;
            _bodyPivot.SetParent(_rig, false);
            _mainHeadRoot = new GameObject("CuttingHead").transform;
            _mainHeadRoot.SetParent(_rig, false);

            if (trimmerModel != null)
            {
                var model = trimmerModel.scene.IsValid() ? trimmerModel : Instantiate(trimmerModel);
                model.name = "TrimmerModel";
                model.transform.SetParent(_bodyPivot, false);
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                model.transform.localScale = new Vector3(modelScale, modelScale, modelScale * shaftStretch);
                foreach (var c in model.GetComponentsInChildren<Collider>()) Destroy(c);
                _mainSpinner = FindDeep(model.transform, cutterHeadName);
                if (_mainSpinner != null)
                {
                    // Keep the head level with the ground while the shaft is angled up to the player.
                    _mainSpinner.SetParent(_mainHeadRoot, false);
                    _mainSpinner.localPosition = Vector3.zero;
                    _mainSpinner.localRotation = Quaternion.identity;
                    _mainSpinner.localScale = Vector3.one * modelScale;
                }
                else Debug.LogWarning($"[PlayShift Grass] Trimmer model has no '{cutterHeadName}' child; the head will not spin separately.", this);
            }
            else Debug.LogWarning("[PlayShift Grass] TrimmerViewmodel has no trimmer model assigned.", this);

            ApplyHeads(0);
            _prevHead = HeadPosition;
        }

        private void LateUpdate()
        {
            if (_rig == null) return;
            float dt = Time.deltaTime;
            var m = GrassLevelManager.Instance;
            int extra = m != null ? m.Modifiers.ExtraBlades : 0;
            if (extra != _extraCount) ApplyHeads(extra);

            float speed = controller != null ? controller.Speed : 0f;
            float rpm = blades != null ? blades.Rpm01 : 0f;
            float load = cutter != null ? cutter.Load : 0f;
            if (blades != null) blades.Load = load;
            float vib = blades != null ? blades.Vibration : 0f;

            // Smooth turning: the tool lags slightly behind the view.
            float targetYaw = controller != null ? controller.Yaw : transform.eulerAngles.y;
            _yaw = Mathf.LerpAngle(_yaw, targetYaw, 1f - Mathf.Exp(-turnLag * dt));

            // Operator sweep while the trimmer runs.
            _sweepPhase += dt * sweepHz * Mathf.PI * 2f * Mathf.Clamp01(rpm * 1.5f);
            float sweepTarget = sweepDegrees * Mathf.Sin(_sweepPhase) * Mathf.Clamp01((rpm - 0.2f) * 2f);
            _sweep = Mathf.Lerp(_sweep, sweepTarget, 1f - Mathf.Exp(-8f * dt));

            _walkPhase += speed * dt;
            float t = Time.time;
            float sway = Mathf.Sin(_walkPhase * 2.6f) * walkSway * Mathf.Min(1f, speed) + Mathf.Sin(t * 1.3f) * 0.006f;
            float jx = (Mathf.Sin(t * 91f) + Mathf.Sin(t * 57.3f)) * 0.5f * vibrationMetres * vib;
            float jy = Mathf.Sin(t * 73.1f) * vibrationMetres * vib;
            float pull = -thickGrassPullBack * load;

            _rig.position = transform.position;
            _rig.rotation = Quaternion.Euler(0f, _yaw + _sweep, 0f);
            float groundY = m != null ? m.FieldTransform.position.y - transform.position.y : 0f;
            var headLocal = new Vector3(headOffset.x + sway + jx, groundY + headHeight + jy, headOffset.y + pull);
            _bodyPivot.localPosition = headLocal;
            _bodyPivot.localRotation = Quaternion.Euler(shaftPitch, -handleSwing, 0f);
            _mainHeadRoot.localPosition = headLocal;
            _mainHeadRoot.localRotation = Quaternion.Euler(jy * 120f, 0f, jx * 120f);

            for (int i = 0; i < _extraRoots.Count; i++)
            {
                float side = (i % 2 == 0 ? -1f : 1f) * extraHeadSpacing * (i / 2 + 1);
                _extraRoots[i].localPosition = headLocal + new Vector3(side, 0f, 0f);
                _extraRoots[i].localRotation = _mainHeadRoot.localRotation;
            }

            HeadVelocity = (HeadPosition - _prevHead) / Mathf.Max(dt, 1e-4f);
            _prevHead = HeadPosition;
        }

        private void ApplyHeads(int extra)
        {
            _extraCount = extra;
            while (_extraRoots.Count < extra) AddExtraHead();
            for (int i = 0; i < _extraRoots.Count; i++) _extraRoots[i].gameObject.SetActive(i < extra);

            var cutPoints = new List<Transform> { _mainHeadRoot };
            var spinners = new List<Transform>();
            if (_mainSpinner != null) spinners.Add(_mainSpinner);
            for (int i = 0; i < extra; i++)
            {
                cutPoints.Add(_extraRoots[i]);
                if (_extraSpinners[i] != null) spinners.Add(_extraSpinners[i]);
            }
            if (cutter != null) cutter.SetHeads(cutPoints);
            if (blades != null) blades.SetSpinners(spinners);
        }

        private void AddExtraHead()
        {
            int i = _extraRoots.Count;
            var root = new GameObject($"ExtraCuttingHead{i + 1}").transform;
            root.SetParent(_rig, false);
            Transform spinner = null;
            if (_mainSpinner != null)
            {
                spinner = Instantiate(_mainSpinner.gameObject, root).transform;
                spinner.name = cutterHeadName;
                spinner.localPosition = Vector3.zero;
                spinner.localRotation = Quaternion.identity;
                spinner.localScale = _mainSpinner.localScale;
                foreach (var ring in spinner.GetComponentsInChildren<Transform>())
                    if (ring != spinner && ring.name == "MotionBlurRing") Destroy(ring.gameObject);
            }
            // Crossbar from the main head to this one, at hub height.
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(bar.GetComponent<Collider>());
            bar.name = "Crossbar";
            bar.transform.SetParent(root, false);
            float side = (i % 2 == 0 ? -1f : 1f) * extraHeadSpacing * (i / 2 + 1);
            bar.transform.localPosition = new Vector3(-side * 0.5f, 0.12f, 0f);
            bar.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            bar.transform.localScale = new Vector3(0.035f, Mathf.Abs(side) * 0.5f, 0.035f);
            if (crossbarMaterial != null) bar.GetComponent<Renderer>().sharedMaterial = crossbarMaterial;
            _extraRoots.Add(root);
            _extraSpinners.Add(spinner);
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

using System.Collections.Generic;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// First-person trimmer with no hands, arms or body: the tool floats in its operating position, its
    /// shaft entering from the bottom-right of the screen and its cutting head on the ground ahead.
    ///
    /// By default the tool is locked to where the player aims (see lockToView); a loose mode adds turn lag and an
    /// automatic left/right sweep. It sways slightly with walking, hums when running and vibrates more and pulls
    /// back a little in thick grass. The cutting head is detached from the shaft so it stays level with the ground and is the exact
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
        [Tooltip("Hide the loop handle (Resources/TrimmerBody_NoHandle). Off by default: owner wants the handle. Either way the " +
                 "loose blade fragment is dropped (Resources/TrimmerBody_Clean). Both built by TrimmerMeshTools.")]
        [SerializeField] private bool hideHandle;

        [Header("Operating position (player space, metres/degrees)")]
        [Tooltip("Head position: x = right, z = forward.")]
        [SerializeField] private Vector2 headOffset = new Vector2(0.05f, 1.4f);
        [Tooltip("Head distance ahead on vertical (portrait) screens: closer, so it sits low in the tall view.")]
        [SerializeField] private float portraitHeadForward = 1.15f;
        [Tooltip("Head sideways offset on portrait screens (negative = left), so it sits lower-middle with the shaft from the bottom-right.")]
        [SerializeField] private float portraitHeadRight = -0.12f;
        [Tooltip("Shaft rise: raises the handle end towards the player.")]
        [SerializeField] private float shaftPitch = 17f;
        [Tooltip("Swings the handle end to the right so the shaft enters from the bottom-right of the screen.")]
        [SerializeField] private float handleSwing = 34f;
        [Tooltip("Gap between the lowest point of the cutting head (blades included) and the ground. Keeps the blades from clipping into the lawn, even while vibrating.")]
        [SerializeField] private float headHeight = 0.035f;

        [Header("Camera-relative tool")]
        [Tooltip("On (default): the cutting head sits at a fixed place in the lower view, on the ground where that line of " +
                 "sight meets the lawn (looking down cuts close, looking further reaches out). Looking up lifts it along " +
                 "the view so it stays visible and stops cutting. The shaft runs from the head to the rig's bottom-right " +
                 "ToolAnchor. Off: the older pose planted at Operating Position.")]
        [SerializeField] private bool cameraRelative = true;
        [Tooltip("Degrees above the bottom edge of the view where the head's centre sits.")]
        [SerializeField] private float headScreenMargin = 12f;
        [Tooltip("Head offset to the right of the view centre (tangent units; 0 = centred).")]
        [SerializeField] private float headScreenRight = 0.06f;
        [Tooltip("Closest / furthest the head goes from the player on the ground (metres).")]
        [SerializeField] private float minReach = 1.0f, maxReach = 2.4f;
        [Tooltip("Grip point in camera space when the player has no rig ToolAnchor.")]
        [SerializeField] private Vector3 fallbackGrip = new Vector3(0.42f, -0.5f, 0.55f);
        [Tooltip("Keeps the head out of walls/hedges (everything but the player's Ignore Raycast layer).")]
        [SerializeField] private LayerMask obstacleMask = ~(1 << 2);
        [Tooltip("Cut radius = the measured blade tips (so grass only goes where the blades reach).")]
        [SerializeField] private bool cutRadiusFromBlades = true;

        [Header("Motion")]
        [Tooltip("On: the trimmer stays exactly where you aim (no turn lag, no automatic left/right sweep, no idle " +
                 "drift), so the view feels locked and steady. Off: the older loose feel with sweep and lag below.")]
        [SerializeField] private bool lockToView = true;
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
        private float _headLift; // distance from the head pivot down to the lowest blade/head vertex
        private float _bladeRadius; // horizontal reach of the blade tips from the head centre (measured)
        private Transform _grip;

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
                SwapBody(model.transform, hideHandle ? "TrimmerBody_NoHandle" : "TrimmerBody_Clean");
                _mainSpinner = FindDeep(model.transform, cutterHeadName);
                if (_mainSpinner != null)
                {
                    // Keep the head level with the ground while the shaft is angled up to the player.
                    _mainSpinner.SetParent(_mainHeadRoot, false);
                    _mainSpinner.localPosition = Vector3.zero;
                    _mainSpinner.localRotation = Quaternion.identity;
                    _mainSpinner.localScale = Vector3.one * modelScale;
                    _headLift = LowestPointBelowPivot(_mainHeadRoot);
                    _bladeRadius = HorizontalReach(_mainHeadRoot);
                }
                else Debug.LogWarning($"[PlayShift Grass] Trimmer model has no '{cutterHeadName}' child; the head will not spin separately.", this);
            }
            else Debug.LogWarning("[PlayShift Grass] TrimmerViewmodel has no trimmer model assigned.", this);

            ApplyHeads(0);
            _prevHead = HeadPosition;

            // Grass only goes where the blades reach: the cut radius is the measured blade tip radius.
            if (cutRadiusFromBlades && cutter != null && _bladeRadius > 0.1f)
            {
                cutter.BaseRadius = _bladeRadius;
                Debug.Log($"[PlayShift Grass] Cut radius set to the blade tips: {_bladeRadius:F2} m.", this);
            }
            var rig = controller != null ? controller.Rig : null;
            _grip = rig != null ? rig.ToolAnchor : null;
        }

        /// <summary>
        /// Furthest horizontal distance of the head's mesh vertices (the blade tips) from its pivot: the radius the
        /// spinning blades sweep. Measured once at start (vertices when readable, else the mesh bounds' half-extent).
        /// </summary>
        private static float HorizontalReach(Transform head)
        {
            float best = 0f;
            Vector3 c = head.position;
            var verts = new List<Vector3>();
            foreach (var mf in head.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var tr = mf.transform;
                if (mesh.isReadable)
                {
                    mesh.GetVertices(verts);
                    for (int i = 0; i < verts.Count; i++)
                    {
                        Vector3 w = tr.TransformPoint(verts[i]);
                        float dx = w.x - c.x, dz = w.z - c.z;
                        best = Mathf.Max(best, Mathf.Sqrt(dx * dx + dz * dz));
                    }
                }
                else
                {
                    var e = Vector3.Scale(mesh.bounds.extents, tr.lossyScale);
                    best = Mathf.Max(best, Mathf.Max(Mathf.Abs(e.x), Mathf.Abs(e.z)));
                }
            }
            return best;
        }

        private void LateUpdate()
        {
            if (_rig == null) return;
            // Title screen: the lawn is the backdrop, the trimmer is hidden.
            bool show = !MainMenu.IsOpen;
            if (_rig.gameObject.activeSelf != show) _rig.gameObject.SetActive(show);
            if (!show) return;
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
            _yaw = lockToView ? targetYaw : Mathf.LerpAngle(_yaw, targetYaw, 1f - Mathf.Exp(-turnLag * dt));

            // Operator sweep while the trimmer runs (loose mode only).
            float sweepTarget = 0f;
            if (!lockToView)
            {
                _sweepPhase += dt * sweepHz * Mathf.PI * 2f * Mathf.Clamp01(rpm * 1.5f);
                sweepTarget = sweepDegrees * Mathf.Sin(_sweepPhase) * Mathf.Clamp01((rpm - 0.2f) * 2f);
            }
            _sweep = Mathf.Lerp(_sweep, sweepTarget, 1f - Mathf.Exp(-8f * dt));

            _walkPhase += speed * dt;
            float t = Time.time;
            float sway = Mathf.Sin(_walkPhase * 2.6f) * walkSway * Mathf.Min(1f, speed) + (lockToView ? 0f : Mathf.Sin(t * 1.3f) * 0.006f);
            // Locked mode: a light hum only. The high-frequency shake aliases against the frame rate into a visible
            // wobble of the cutting head, so it is kept small.
            float shake = vibrationMetres * vib * (lockToView ? 0.35f : 1f);
            float jx = (Mathf.Sin(t * 91f) + Mathf.Sin(t * 57.3f)) * 0.5f * shake;
            float jy = Mathf.Sin(t * 73.1f) * shake;
            float pull = -thickGrassPullBack * load;

            _rig.position = transform.position;
            _rig.rotation = Quaternion.Euler(0f, _yaw + _sweep, 0f);
            float groundY = m != null ? m.FieldTransform.position.y - transform.position.y : 0f;
            // Vibration only lifts the head (never pushes it down) so the blades stay above the lawn.
            var cam = controller != null ? controller.Camera : null;

            // Upgrades that widen the cut also enlarge the visible head, so the blades always match the cut.
            float scale = m != null ? m.Modifiers.RadiusMultiplier : 1f;
            if (!Mathf.Approximately(_mainHeadRoot.localScale.x, scale))
            {
                _mainHeadRoot.localScale = Vector3.one * scale;
                for (int i = 0; i < _extraRoots.Count; i++) _extraRoots[i].localScale = Vector3.one * scale;
            }
            float clearance = _headLift * scale + headHeight;
            if (cutter != null) cutter.HeadClearance = clearance;

            if (cameraRelative && cam != null)
            {
                PlaceCameraRelative(cam, m, clearance, sway, jx, Mathf.Abs(jy), -pull);
                HeadVelocity = (HeadPosition - _prevHead) / Mathf.Max(dt, 1e-4f);
                _prevHead = HeadPosition;
                return;
            }
            bool portrait = cam != null && cam.aspect < 1f;
            float forward = portrait ? portraitHeadForward : headOffset.y;
            float right = portrait ? portraitHeadRight : headOffset.x;
            var headLocal = new Vector3(right + sway + jx, groundY + clearance + Mathf.Abs(jy), forward + pull);
            _bodyPivot.localPosition = headLocal;
            _bodyPivot.localRotation = Quaternion.Euler(shaftPitch, -handleSwing, 0f);
            _mainHeadRoot.localPosition = headLocal;
            // Head stays level when locked (tilting it made the blade disc wobble); slight tilt in loose mode.
            _mainHeadRoot.localRotation = lockToView ? Quaternion.identity : Quaternion.Euler(jy * 120f, 0f, jx * 120f);

            for (int i = 0; i < _extraRoots.Count; i++)
            {
                float side = (i % 2 == 0 ? -1f : 1f) * extraHeadSpacing * (i / 2 + 1);
                _extraRoots[i].localPosition = headLocal + new Vector3(side, 0f, 0f);
                _extraRoots[i].localRotation = _mainHeadRoot.localRotation;
            }

            HeadVelocity = (HeadPosition - _prevHead) / Mathf.Max(dt, 1e-4f);
            _prevHead = HeadPosition;
        }

        /// <summary>
        /// Camera-relative pose: the head sits <see cref="headScreenMargin"/> degrees above the bottom of the view. Where
        /// that line of sight meets the lawn (within reach) the head rests on the grass and cuts; looking higher holds
        /// it at full reach along the view, lifted (visible, not cutting). A sphere cast keeps it out of hedges/walls.
        /// The shaft is aimed from the head to the bottom-right grip. No allocations.
        /// </summary>
        private void PlaceCameraRelative(Camera cam, GrassLevelManager m, float clearance, float sway, float jx, float lift, float pullBack)
        {
            var ct = cam.transform;
            Vector3 camPos = ct.position;
            float baseY = (m != null ? m.FieldTransform.position.y : transform.position.y) + clearance;
            float down = Mathf.Max(5f, cam.fieldOfView * 0.5f - headScreenMargin) * Mathf.Deg2Rad;
            Vector3 dir = ct.TransformDirection(new Vector3(headScreenRight, -Mathf.Tan(down), 1f)).normalized;
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            float flatLen = flat.magnitude;
            Vector3 flatDir = flatLen > 1e-4f ? flat / flatLen : _rig.forward;
            Vector3 right = Vector3.Cross(Vector3.up, flatDir);

            float h = camPos.y - baseY;
            float reach = dir.y < -1e-4f && flatLen > 1e-4f ? h * flatLen / -dir.y : float.PositiveInfinity;
            Vector3 head;
            if (reach <= maxReach)
            {
                reach = Mathf.Max(reach, minReach) - pullBack;
                head = new Vector3(camPos.x, baseY, camPos.z) + flatDir * reach;
            }
            else
            {
                // Looking up: full reach along the view, lifted off the lawn.
                head = camPos + dir * (maxReach / Mathf.Max(flatLen, 1e-3f)) - flatDir * pullBack;
                if (head.y < baseY) head.y = baseY;
            }

            // Keep the blades out of hedges and walls: pull the head back to just before the first obstacle.
            var from = new Vector3(camPos.x, head.y + 0.3f, camPos.z);
            Vector3 to = head + Vector3.up * 0.3f, d = to - from;
            float dist = d.magnitude;
            float bladeR = Mathf.Max(0.2f, _bladeRadius);
            if (dist > 1e-3f && Physics.SphereCast(from, 0.12f, d / dist, out var hit, dist + bladeR, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                float keep = Mathf.Max(0.3f, hit.distance - bladeR);
                if (keep < dist) head = from + d / dist * keep - Vector3.up * 0.3f;
            }

            head += right * (sway + jx) + Vector3.up * lift; // walk sway + motor hum (lift only: never into the lawn)
            var headRot = Quaternion.LookRotation(flatDir, Vector3.up);
            _mainHeadRoot.SetPositionAndRotation(head, headRot);

            // Shaft: the classic shallow rise (shaftPitch) from the head, swung towards the grip at the bottom-right of
            // the view, so it runs out of the bottom-right corner like a trimmer held at the hip.
            Vector3 grip = _grip != null ? _grip.position : ct.TransformPoint(fallbackGrip);
            Vector3 toGrip = grip - head;
            toGrip.y = 0f;
            float swing = toGrip.sqrMagnitude > 1e-4f ? -Vector3.SignedAngle(-flatDir, toGrip, Vector3.up) : handleSwing;
            swing = Mathf.Clamp(Mathf.Max(swing, handleSwing), 12f, 55f);
            _bodyPivot.SetPositionAndRotation(head, headRot * Quaternion.Euler(shaftPitch, -swing, 0f));

            for (int i = 0; i < _extraRoots.Count; i++)
            {
                float side = (i % 2 == 0 ? -1f : 1f) * extraHeadSpacing * (i / 2 + 1);
                _extraRoots[i].SetPositionAndRotation(head + right * side, headRot);
            }
        }

        /// <summary>
        /// How far the head's meshes reach below its pivot (the blades can hang below the hub). Measured once, while
        /// the head is still level, from the renderers' world bounds; the head only spins about its vertical axis, so
        /// this holds at every blade angle. Tilt from vibration is tiny and covered by <see cref="headHeight"/>.
        /// </summary>
        /// <summary>Replaces the body mesh with a pre-built cleaned version (no loose blade fragment; optionally no handle).</summary>
        private void SwapBody(Transform model, string resourceName)
        {
            var body = Resources.Load<Mesh>(resourceName);
            if (body == null) { Debug.LogWarning($"[PlayShift Grass] {resourceName} mesh missing; using the original body.", this); return; }
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null && mf.sharedMesh.name == "TrimmerBody") mf.sharedMesh = body;
        }

        private static float LowestPointBelowPivot(Transform head)
        {
            float minY = 0f;
            foreach (var r in head.GetComponentsInChildren<Renderer>())
                minY = Mathf.Min(minY, r.bounds.min.y - head.position.y);
            return -minY;
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

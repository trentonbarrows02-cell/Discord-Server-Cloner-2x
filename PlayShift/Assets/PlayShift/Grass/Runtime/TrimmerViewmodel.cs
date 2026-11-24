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
        [Tooltip("The trimmer model (Assets/PlayShift/Models/StringTrimmer.glb). Must contain a child named 'CutterHead'.")]
        [SerializeField] private GameObject trimmerModel;
        [SerializeField] private string cutterHeadName = "CutterHead";
        [Tooltip("Child of the cutter head holding only the blades: the only part that spins (the cap, collar and " +
                 "spool stay fixed to the shaft). Without it the whole head spins.")]
        [SerializeField] private string bladesName = "Blades";
        [SerializeField] private float modelScale = 1.85f;
        [Tooltip("Lengthens only the shaft (model Z) so it reaches from the head on the ground up to the bottom-right corner. The cutting head keeps its real size.")]
        [SerializeField] private float shaftStretch = 1f;
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
        [SerializeField] private float shaftPitch = 0f;
        [Tooltip("Swings the handle end to the right so the shaft enters from the bottom-right of the screen.")]
        [SerializeField] private float handleSwing = 34f;
        [Tooltip("Cutting height: gap between the lowest point of the cutting head (blades included) and the ground " +
                 "(3-8 cm). Keeps the blades from clipping into the lawn, even while vibrating.")]
        [SerializeField, Range(0.03f, 0.08f)] private float cuttingHeight = 0.04f;

        [Header("Ground-following tool")]
        [Tooltip("On (default): the cutting head works on the ground at a fixed spot ahead of the player. It follows the " +
                 "player's turning (yaw) only, never the camera's up/down look, so looking up or down never lifts it or " +
                 "moves the cut. A downward ray keeps it at Cutting Height over the real ground. Off: the older pose " +
                 "planted at Operating Position.")]
        [SerializeField] private bool groundFollow = true;
        [Tooltip("How far ahead of the player the head's centre works (metres). Far enough that the blades are in view " +
                 "with the normal, slightly lowered look.")]
        [SerializeField] private float workReach = 3.3f;
        [Tooltip("Head offset to the right of the player's centre line (metres).")]
        [SerializeField] private float workRight = 0f;
        [Tooltip("Ground the head follows (everything but the player's Ignore Raycast layer; the grass has no colliders).")]
        [SerializeField] private LayerMask groundMask = ~(1 << 2);
        [Tooltip("How quickly the head settles onto a new ground height (1/s). Higher = tighter, lower = smoother.")]
        [SerializeField] private float groundFollowRate = 18f;
        [Tooltip("Most the head tilts to stay parallel with sloped ground (degrees).")]
        [SerializeField] private float maxGroundTilt = 20f;
        [Tooltip("Where the trimmer is held, relative to the player's feet (x right, y up, z forward): the right hip. The " +
                 "shaft always runs back towards it, out of the bottom-right of the view.")]
        [SerializeField] private Vector3 hipGrip = new Vector3(0.85f, 0.8f, 0.2f);
        [Tooltip("Shaft length of the trimmer model at stretch 1 (m, world) and its built-in rise from the head (deg).")]
        [SerializeField] private float shaftLength = 2.2f;
        [SerializeField] private float shaftBuiltInRise = 22.5f;
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
        [Tooltip("Side-to-side drift of the head while walking (m). 0 = the head holds still; only the blades spin.")]
        [SerializeField] private float walkSway = 0f;
        [Tooltip("Motor shake of the head (m). 0 = the head holds still; only the blades spin.")]
        [SerializeField] private float vibrationMetres = 0f;
        [Tooltip("How far thick grass pushes the head back (m). 0 = the head holds still.")]
        [SerializeField] private float thickGrassPullBack = 0f;

        [Header("Multi-blade")]
        [SerializeField] private float extraHeadSpacing = 1.4f;
        [SerializeField] private Material crossbarMaterial;

        private Transform _rig, _bodyPivot, _mainHeadRoot, _mainSpinner, _mainBlades, _model;
        private readonly List<Transform> _extraRoots = new List<Transform>();
        private readonly List<Transform> _extraSpinners = new List<Transform>();
        private float _yaw, _sweepPhase, _sweep, _walkPhase;
        private int _extraCount = -1;
        private Vector3 _prevHead;
        private float _headLift; // distance from the head pivot down to the lowest blade/head vertex
        private float _bladeRadius; // horizontal reach of the blade tips from the head centre (measured)
        private float _groundY;
        private Vector3 _groundN = Vector3.up;
        private bool _groundInit;

        /// <summary>World position of the main cutting head (on the ground).</summary>
        public Vector3 HeadPosition => _mainHeadRoot != null ? _mainHeadRoot.position : transform.position;
        /// <summary>Head position relative to the player in the player's turn frame, as placed this frame.</summary>
        public Vector3 HeadOffsetFromPlayer { get; private set; }
        /// <summary>The fixed housing's rotation relative to the player's turn frame (changes only with ground tilt).</summary>
        public Quaternion HousingRotationFromPlayer { get; private set; } = Quaternion.identity;
        /// <summary>Gap kept between the lowest point of the blades and the ground (m).</summary>
        public float CuttingHeight => cuttingHeight;
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
                _model = model.transform;
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
                    _mainBlades = FindDeep(_mainSpinner, bladesName);
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
            float clearance = _headLift * scale + cuttingHeight;
            if (cutter != null) cutter.HeadClearance = clearance;

            if (groundFollow)
            {
                PlaceOnGround(m, clearance, sway, jx, Mathf.Abs(jy), -pull, dt);
                HeadOffsetFromPlayer = Quaternion.Inverse(_rig.rotation) * (HeadPosition - _rig.position);
                if (_mainSpinner != null) HousingRotationFromPlayer = Quaternion.Inverse(_rig.rotation) * _mainSpinner.rotation;
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
        /// Ground-following pose. The head works on the ground <see cref="workReach"/> ahead of the player, along the
        /// player's yaw only: the camera's pitch never moves or lifts it, so looking up or down keeps cutting the same
        /// spot. A downward ray finds the real ground under the head (grass has no colliders; the player is on Ignore
        /// Raycast) and the head eases onto it at <see cref="cuttingHeight"/>, tilted to stay parallel with slopes.
        /// A sphere cast keeps it out of fences/walls. The shaft runs from the head to the hip grip. No allocations.
        /// </summary>
        private void PlaceOnGround(GrassLevelManager m, float clearance, float sway, float jx, float lift, float pullBack, float dt)
        {
            Vector3 feet = transform.position;
            Vector3 flatDir = _rig.forward;
            Vector3 right = _rig.right;
            float lawnY = m != null ? m.FieldTransform.position.y : feet.y;

            Vector3 head = feet + flatDir * (workReach - pullBack) + right * workRight;

            // Keep the blades out of fences and walls: pull the head back to just before the first obstacle. Cast at
            // knee height above the player's own feet (not the lawn), and only walls count: a slope or step the player
            // stands on or walks towards is ground, which the head climbs instead.
            float bladeR = Mathf.Max(0.2f, _bladeRadius) * (_mainHeadRoot != null ? _mainHeadRoot.localScale.x : 1f);
            float castY = Mathf.Max(feet.y, lawnY) + 0.5f;
            var from = new Vector3(feet.x, castY, feet.z);
            Vector3 d = new Vector3(head.x, castY, head.z) - from;
            float dist = d.magnitude;
            if (dist > 1e-3f && Physics.SphereCast(from, 0.12f, d / dist, out var hit, dist + bladeR, obstacleMask, QueryTriggerInteraction.Ignore)
                && hit.normal.y < 0.6f)
            {
                float keep = Mathf.Max(0.3f, hit.distance - bladeR);
                if (keep < dist) { Vector3 p = from + d / dist * keep; head.x = p.x; head.z = p.z; }
            }

            // Ground under the blades: downward rays from well above at the centre and four points round the blade
            // rim (walls/fences are kept out of reach above). Any surface counts for height, so the edge of a step or
            // ramp is never missed; only fairly flat ones set the head's tilt.
            float top = Mathf.Max(feet.y, lawnY) + 1.5f;
            Vector3 groundN = _groundInit ? _groundN : Vector3.up;
            if (Physics.Raycast(new Vector3(head.x, top, head.z), Vector3.down, out var g, 4f, groundMask, QueryTriggerInteraction.Ignore)
                && g.normal.y > 0.5f)
                groundN = g.normal;
            float k = _groundInit ? 1f - Mathf.Exp(-groundFollowRate * dt) : 1f;
            _groundN = _groundInit ? Vector3.Slerp(_groundN, groundN, k).normalized : groundN;
            // The blade disc's plane (tilted with the ground, limited): its height changes by grad.x per metre right
            // (world x) and grad.y per metre forward (world z) from the centre.
            Vector3 nT = Vector3.RotateTowards(Vector3.up, _groundN, maxGroundTilt * Mathf.Deg2Rad, 0f);
            float gx = -nT.x / nT.y, gz = -nT.z / nT.y;
            // Lowest centre height that keeps every sampled point of the disc on or above the ground under it.
            float groundY = float.NegativeInfinity;
            float rim = 0.85f * bladeR;
            for (int i = 0; i < 5; i++)
            {
                Vector3 o = i == 0 ? Vector3.zero : i == 1 ? flatDir * rim : i == 2 ? -flatDir * rim : i == 3 ? right * rim : -right * rim;
                if (Physics.Raycast(new Vector3(head.x + o.x, top, head.z + o.z), Vector3.down, out var hit2, 4f, groundMask,
                        QueryTriggerInteraction.Ignore)
                    && hit2.point.y < top - 0.9f) // above knee height = a wall top beside the head, not ground under it
                    groundY = Mathf.Max(groundY, hit2.point.y - (gx * o.x + gz * o.z));
            }
            if (float.IsNegativeInfinity(groundY)) groundY = _groundInit ? _groundY : lawnY; // off the map: hold
            // Ease onto the ground (no snapping, no jitter); first frame snaps. Never below the ground it is over this
            // frame (the blades must not sink in while easing up a bump).
            _groundY = Mathf.Lerp(_groundY, groundY, k);
            _groundInit = true;
            head.y = Mathf.Max(_groundY, groundY) + clearance;

            head += right * (sway + jx) + Vector3.up * lift; // walk sway + motor hum (lift only: never into the lawn)
            var flatRot = Quaternion.LookRotation(flatDir, Vector3.up);
            var tilt = Quaternion.FromToRotation(Vector3.up, Vector3.RotateTowards(Vector3.up, _groundN, maxGroundTilt * Mathf.Deg2Rad, 0f));
            var headRot = tilt * flatRot;
            _mainHeadRoot.SetPositionAndRotation(head, headRot);

            // Shaft: runs from the head straight back to the grip at the player's right side (off-screen, so it comes
            // into view from the bottom-right corner). The model is stretched along its length to span the distance:
            // stretching by k along the model's z gives length L*sqrt(sin^2 a + k^2 cos^2 a) at rise atan(tan a / k).
            Vector3 grip = transform.TransformPoint(hipGrip);
            Vector3 toGrip = grip - head;
            Vector3 toGripFlat = new Vector3(toGrip.x, 0f, toGrip.z);
            float swing = toGripFlat.sqrMagnitude > 1e-4f ? -Vector3.SignedAngle(-flatDir, toGripFlat, Vector3.up) : handleSwing;
            swing = Mathf.Clamp(swing, 0f, 60f);
            float riseToGrip = Mathf.Atan2(toGrip.y, Mathf.Max(0.05f, toGripFlat.magnitude)) * Mathf.Rad2Deg;
            float a0 = shaftBuiltInRise * Mathf.Deg2Rad;
            float sin0 = Mathf.Sin(a0), cos0 = Mathf.Cos(a0);
            float need = toGrip.magnitude / Mathf.Max(0.1f, shaftLength);
            float stretch = Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0f, need * need - sin0 * sin0)) / cos0, 0.6f, 2.5f);
            if (_model != null) _model.localScale = new Vector3(modelScale, modelScale, modelScale * stretch);
            float rise = riseToGrip - Mathf.Atan(Mathf.Tan(a0) / stretch) * Mathf.Rad2Deg;
            _bodyPivot.SetPositionAndRotation(head, flatRot * Quaternion.Euler(rise, -swing, 0f));

            for (int i = 0; i < _extraRoots.Count; i++)
            {
                float side = (i % 2 == 0 ? -1f : 1f) * extraHeadSpacing * (i / 2 + 1);
                _extraRoots[i].SetPositionAndRotation(head + headRot * new Vector3(side, 0f, 0f), headRot);
            }
        }

        /// <summary>
        /// How far the head's meshes reach below its pivot (the blades can hang below the hub). Measured once, while
        /// the head is still level, from the renderers' world bounds; the head only spins about its vertical axis, so
        /// this holds at every blade angle. Tilt from vibration is tiny and covered by <see cref="cuttingHeight"/>.
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
            if (_mainSpinner != null) spinners.Add(_mainBlades != null ? _mainBlades : _mainSpinner);
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
                var cloneBlades = _mainBlades != null ? FindDeep(spinner, bladesName) : null;
                if (cloneBlades != null)
                {
                    cloneBlades.localRotation = _mainBlades.localRotation;
                    spinner = cloneBlades; // only the blades spin
                }
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

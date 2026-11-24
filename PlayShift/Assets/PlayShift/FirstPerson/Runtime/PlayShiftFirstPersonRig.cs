using System;
using System.Reflection;
using UnityEngine;

namespace PlayShift.FirstPerson
{
    /// <summary>
    /// Reusable PlayShift first-person rig (Cut the Grass, Snow Cleanup, future simulators). Walking and looking are
    /// done by the Easy Peasy First Person Controller (Asset Store, Eys Store) on the same object; this component
    /// is the game-facing side of it:
    ///  - pauses it while a menu is open (<see cref="InputBlocked"/>) or while a game drives the player itself
    ///    (<see cref="Suspended"/>, e.g. scripted captures);
    ///  - feeds it the game's speed multiplier (upgrades, slowdowns), the FOV and mouse sensitivity settings;
    ///  - exposes yaw, pitch, velocity, the camera and a camera-relative <see cref="ToolAnchor"/> for held tools.
    ///
    /// The asset is found by type name, never referenced directly: Asset Store licences forbid putting it in a public
    /// repo, so code that uses this rig compiles and runs without it (<see cref="HasController"/> is then false and
    /// the game moves the player itself). Values are only pushed to the controller when they change, so there are no
    /// per-frame allocations.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)] // before the controller's Update, so speed/FOV/blocking apply the same frame
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayShiftFirstPersonRig : MonoBehaviour
    {
        public const string ControllerTypeName = "EasyPeasyFirstPersonController.FirstPersonController";

        [SerializeField] private Camera playerCamera;
        [Tooltip("Camera-relative point where a held tool's grip sits (bottom-right of the view).")]
        [SerializeField] private Transform toolAnchor;
        [Tooltip("Turns off the controller's walking head bob so the view stays perfectly steady.")]
        [SerializeField] private bool steadyCamera = true;

        [Header("Look")]
        [Tooltip("Left/right look speed at sensitivity 1 (degrees per 20 px of mouse movement). The game's mouse " +
                 "sensitivity setting multiplies it.")]
        [UnityEngine.Serialization.FormerlySerializedAs("baseMouseSensitivity")]
        [SerializeField] private float horizontalSensitivity = 2f;
        [Tooltip("Up/down look speed at sensitivity 1 (same units).")]
        [SerializeField] private float verticalSensitivity = 1.3f;
        [Tooltip("Camera angle at the start (degrees, negative = looking down).")]
        [SerializeField, Range(-60f, 20f)] private float defaultPitch = -35f;
        [Tooltip("Highest the player can look (degrees above the horizon).")]
        [SerializeField, Range(-60f, 60f)] private float maxLookUp = -20f;
        [Tooltip("Lowest the player can look (degrees below the horizon). Keeps the view off the trimmer's underside and " +
                 "the player's own feet.")]
        [SerializeField, Range(0f, 89f)] private float maxLookDown = 45f;
        [Tooltip("Camera (eye) height above the ground (m). Higher = a better view down onto the grass being cut.")]
        [SerializeField, Range(1.4f, 2.6f)] private float cameraHeight = 1.95f;
        [Tooltip("On: mouse up/down does nothing; the camera stays at Default Pitch. Left/right stays free (360).")]
        [SerializeField] private bool lockVerticalLook;

        public bool VerticalLookLocked => lockVerticalLook;
        /// <summary>Starting pitch (negative = down); also the locked pitch.</summary>
        public float DefaultPitch { get => defaultPitch; set => defaultPitch = Mathf.Clamp(value, -maxLookDown, maxLookUp); }
        public float LockedPitch => defaultPitch;
        /// <summary>The look pitch the rig holds (negative = down). Use this during Update: the controller writes its own
        /// unlimited value into the camera then, which the rig replaces in LateUpdate.</summary>
        public float LookPitch => lockVerticalLook ? defaultPitch : _pitchInit ? _pitch : defaultPitch;
        public float MaxLookUp { get => maxLookUp; set => maxLookUp = value; }
        public float MaxLookDown { get => maxLookDown; set => maxLookDown = value; }

        private float _pitch;
        private bool _pitchInit;

        /// <summary>Set once by the game: true while menus are open (movement and look paused).</summary>
        public static Func<bool> InputBlocked;

        /// <summary>True while the game drives this player itself; the controller is paused.</summary>
        public bool Suspended { get; set; }

        /// <summary>Multiplies walk/sprint/crouch speed (movement upgrades, heavy-work slowdowns). Default 1.</summary>
        public float SpeedMultiplier { get; set; } = 1f;

        /// <summary>Vertical field of view in degrees for the camera (the game converts its setting for the aspect).</summary>
        public float VerticalFov { get; set; } = 65f;

        /// <summary>Mouse sensitivity scale from the game's settings (1 = default).</summary>
        public float MouseSensitivityScale { get; set; } = 1f;

        public Camera Camera => playerCamera;
        public Transform ToolAnchor => toolAnchor;
        public bool HasController => _ext != null;
        public float Yaw => transform.eulerAngles.y;
        public float MinPitch => -maxLookDown;
        public float MaxPitch => maxLookUp;

        /// <summary>Horizontal velocity (m/s) the character controller moved with last frame.</summary>
        public Vector3 Velocity { get; private set; }
        public float Speed { get; private set; }

        /// <summary>Look pitch in degrees, negative = looking down. Setting it also turns the camera immediately.</summary>
        public float Pitch
        {
            // Read from the camera (the controller writes it there): reading the field would box a float every call.
            get => playerCamera != null ? -Mathf.DeltaAngle(0f, playerCamera.transform.localEulerAngles.x) : 0f;
            set
            {
                float p = Mathf.Clamp(value, -maxLookDown, maxLookUp);
                _pitch = p; _pitchInit = true;
                if (playerCamera != null) playerCamera.transform.localRotation = Quaternion.Euler(-p, 0f, 0f);
            }
        }

        private MonoBehaviour _ext;
        private CharacterController _cc;
        private FieldInfo _fWalk, _fSprint, _fCrouch, _fNormalFov, _fSprintFov, _fSensitivity, _fPitch, _fCamHeight, _fCrouchCamHeight;
        private float _sentCamHeight = -1f;
        private float _baseWalk, _baseSprint, _baseCrouch;
        // Last values pushed (reflection boxes floats, so only write on change).
        private float _sentSpeedK = -1f, _sentFov = -1f, _sentSens = -1f;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            if (playerCamera == null) playerCamera = GetComponentInChildren<Camera>();
            FindController();
        }

        private void FindController()
        {
            foreach (var mb in GetComponents<MonoBehaviour>())
            {
                if (mb == null || mb == this || mb.GetType().FullName != ControllerTypeName) continue;
                var t = mb.GetType();
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _fWalk = t.GetField("walkSpeed", F);
                _fSprint = t.GetField("sprintSpeed", F);
                _fCrouch = t.GetField("crouchSpeed", F);
                _fNormalFov = t.GetField("normalFov", F);
                _fSprintFov = t.GetField("sprintFov", F);
                _fSensitivity = t.GetField("mouseSensitivity", F);
                _fPitch = t.GetField("xRotation", F);
                _fCamHeight = t.GetField("standingCameraHeight", F);
                _fCrouchCamHeight = t.GetField("crouchingCameraHeight", F);
                if (_fWalk == null || _fSprint == null || _fCrouch == null || _fNormalFov == null || _fSprintFov == null ||
                    _fSensitivity == null || _fPitch == null)
                {
                    Debug.LogWarning("[PlayShift] Easy Peasy controller found, but its fields changed (new version?). " +
                                     "Using the game's built-in movement.", this);
                    mb.enabled = false;
                    return;
                }
                _ext = mb;
                if (steadyCamera) t.GetField("useHeadBob", F)?.SetValue(mb, false);
                _ext.useGUILayout = false; // it has an OnGUI debug label (off); skip the IMGUI layout pass
                _baseWalk = (float)_fWalk.GetValue(mb);
                _baseSprint = (float)_fSprint.GetValue(mb);
                _baseCrouch = (float)_fCrouch.GetValue(mb);
                return;
            }
        }

        private void Update()
        {
            Vector3 v = _cc.velocity;
            Velocity = new Vector3(v.x, 0f, v.z);
            Speed = Velocity.magnitude;
            if (_ext == null) return;

            bool blocked = Suspended || (InputBlocked != null && InputBlocked());
            if (_ext.enabled == blocked) _ext.enabled = !blocked;
            if (blocked) return;
            if (!_checkedState) EnsureControllerInitialised();

            // Quantised so a continuously varying multiplier (e.g. cutting load) does not write every frame.
            float k = Mathf.Round(Mathf.Max(0f, SpeedMultiplier) * 50f) / 50f;
            if (k != _sentSpeedK)
            {
                _sentSpeedK = k;
                _fWalk.SetValue(_ext, _baseWalk * k);
                _fSprint.SetValue(_ext, _baseSprint * k);
                _fCrouch.SetValue(_ext, _baseCrouch * k);
            }
            float fov = Mathf.Round(VerticalFov * 10f) / 10f;
            if (fov != _sentFov)
            {
                _sentFov = fov;
                _fNormalFov.SetValue(_ext, fov);
                _fSprintFov.SetValue(_ext, fov); // no sprint zoom
            }
            if (_fCamHeight != null && !Mathf.Approximately(cameraHeight, _sentCamHeight))
            {
                _sentCamHeight = cameraHeight;
                _fCamHeight.SetValue(_ext, cameraHeight);
                _fCrouchCamHeight?.SetValue(_ext, cameraHeight); // crouch is off; keep it level anyway
            }
            float sens = horizontalSensitivity * MouseSensitivityScale;
            if (sens != _sentSens)
            {
                _sentSens = sens;
                _fSensitivity.SetValue(_ext, sens);
            }
        }

        private bool _checkedState;

        /// <summary>
        /// The controller sets up its state machine in Awake. If that has not happened by the time it first runs
        /// (it was disabled while the title screen held it), its Update would throw every frame; run its Awake then.
        /// Checked once, the first frame it is handed control.
        /// </summary>
        private void EnsureControllerInitialised()
        {
            _checkedState = true;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = _ext.GetType();
            var state = t.GetField("currentState", F);
            if (state == null || state.GetValue(_ext) != null) return;
            Debug.Log("[PlayShift] Easy Peasy controller had no state yet; initialising it.", this);
            t.GetMethod("Awake", F)?.Invoke(_ext, null);
        }

        private void LateUpdate()
        {
            // Up/down look is owned here (the controller only turns left/right): after every Update and before anything
            // places a tool or renders, the camera pitch is set from our own value, moved by this frame's mouse Y at the
            // vertical sensitivity and held inside the look limits. No smoothing (no input delay), no roll.
            // (The controller's own pitch value is left alone: writing it via reflection would allocate every frame,
            // and whatever it holds is overwritten here before anything uses or draws the camera.)
            if (playerCamera == null) return;
            if (!_pitchInit) { _pitch = defaultPitch; _pitchInit = true; }
            // While the controller is paused (menus, scripted control) it does not move the camera: hold the eye height.
            if ((_ext == null || !_ext.enabled) && playerCamera.transform.parent != null && playerCamera.transform.parent != transform)
            {
                var cp = playerCamera.transform.parent;
                var lp = cp.localPosition;
                if (!Mathf.Approximately(lp.y, cameraHeight)) cp.localPosition = new Vector3(lp.x, cameraHeight, lp.z);
            }
            if (lockVerticalLook) _pitch = defaultPitch;
            else if (Suspended || _ext == null || !_ext.enabled)
            {
                // Scripted control (captures) or the game's built-in movement turned the camera itself: keep it in limits.
                bool blocked = InputBlocked != null && InputBlocked();
                if (Suspended || _ext == null || !blocked) _pitch = Pitch;
            }
            else _pitch += ReadMouseY() * 0.05f * verticalSensitivity * MouseSensitivityScale;
            _pitch = Mathf.Clamp(_pitch, -maxLookDown, maxLookUp);
            playerCamera.transform.localRotation = Quaternion.Euler(-_pitch, 0f, 0f);
        }

        /// <summary>This frame's vertical mouse movement in pixels (up = positive), same source the controller uses.</summary>
        private static float ReadMouseY()
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null ? mouse.delta.ReadValue().y : 0f;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetAxisRaw("Mouse Y") * 20f;
#else
            return 0f;
#endif
        }

        private void OnDisable()
        {
            if (_ext != null) _ext.enabled = false;
        }
        // No OnEnable: touching the controller's enabled flag before Unity has run its Awake skipped that Awake.
        // Update re-enables it when it is not blocked.
    }
}

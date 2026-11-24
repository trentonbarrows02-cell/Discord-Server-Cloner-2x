using PlayShift.FirstPerson;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Cut the Grass player glue. Walking and looking come from the reusable <see cref="PlayShiftFirstPersonRig"/>
    /// (Easy Peasy First Person Controller); this component keeps the game's side of the player: trimmer controls,
    /// pausing for menus, FOV and mouse settings, movement upgrades (gift speed modifiers) and the thick-grass
    /// slowdown, plus scripted input for captures. Without the Easy Peasy asset (it is not in git: Asset Store
    /// licence) it moves the player itself with the same controls. No body, arms or hands exist: the only visible
    /// equipment is the trimmer (see <see cref="TrimmerViewmodel"/>).
    ///
    /// Controls: WASD / arrows move, mouse looks, Shift sprints, hold Left Mouse to run the trimmer (release stops it),
    /// Space toggles it on/off (hands-free for streams), Esc opens settings (and frees the cursor).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class FirstPersonController : MonoBehaviour
    {
        [SerializeField] private Camera playerCamera;
        [Header("Built-in movement (used only without the Easy Peasy controller)")]
        [SerializeField] private float eyeHeight = 1.7f;
        [SerializeField] private float walkSpeed = 3.5f;
        [SerializeField] private float sprintSpeed = 5.5f;
        [SerializeField] private float acceleration = 14f;
        [SerializeField] private float mouseSensitivity = 0.12f;
        [SerializeField] private bool invertY;
        [SerializeField] private float minPitch = -75f;
        [SerializeField] private float maxPitch = 55f;
        [Tooltip("A natural, slightly lowered look: the field ahead and the cutting head both in view. The head works on the ground whatever the pitch.")]
        [SerializeField] private float startPitch = -14f;
        [Tooltip("Speed lost at full cutting load (thick, tall grass).")]
        [Range(0f, 0.8f)] [SerializeField] private float thickGrassResistance = 0.35f;
        [SerializeField] private GrassCutter cutter;
        [SerializeField] private TrimmerBlades blades;
        [SerializeField] private bool lockCursor = true;

        [Header("View")]
        [Tooltip("Start pitch on vertical (portrait) screens: looks further ahead, like TikTok trimmer games; the taller " +
                 "view still shows the cutting head near the bottom.")]
        [SerializeField] private float portraitStartPitch = -14f;
        [Tooltip("Upper limit for the vertical FOV on very tall screens. The FOV itself comes from GameSettings.")]
        [SerializeField] private float maxVerticalFov = 130f;
        private bool _pitchTouched;

        [Header("Controller")]
        [Tooltip("Reusable PlayShift rig (Assets/PlayShift/FirstPerson). With the Easy Peasy controller it does the walking " +
                 "and looking; without it this component moves the player.")]
        [SerializeField] private PlayShiftFirstPersonRig rig;

        private static readonly System.Func<bool> s_menusOpen = () => MainMenu.IsOpen || GameSettingsMenu.IsOpen;

        private CharacterController _cc;
        private Vector3 _velocity;
        private float _yaw, _pitch, _verticalSpeed;
        private bool _toggleOn, _scripted;

        public Camera Camera => playerCamera;
        public float Yaw => _yaw;
        public float Pitch => _pitch;
        /// <summary>Horizontal speed in m/s (drives walk sway).</summary>
        public float Speed { get; private set; }
        /// <summary>Horizontal velocity in world space.</summary>
        public Vector3 Velocity => _velocity;
        /// <summary>The reusable rig, if this player has one (tool anchor, controller state).</summary>
        public PlayShiftFirstPersonRig Rig => rig;
        public bool UsesExternalController => rig != null && rig.HasController;

        /// <summary>Optional external input (scripted demos, automation). When set, it replaces keyboard/mouse.</summary>
        public System.Func<FpInput> InputOverride { get; set; }

        public struct FpInput
        {
            public Vector2 Move;      // x = strafe, y = forward, -1..1
            public Vector2 Look;      // degrees this frame (yaw, pitch)
            public bool Sprint;
            public bool TrimmerHeld;
            public bool TrimmerToggle;
        }

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            if (rig == null) rig = GetComponent<PlayShiftFirstPersonRig>();
            if (playerCamera == null) playerCamera = rig != null && rig.Camera != null ? rig.Camera : GetComponentInChildren<Camera>();
            if (cutter == null) cutter = GetComponent<GrassCutter>();
            if (blades == null) blades = GetComponent<TrimmerBlades>();
            _yaw = transform.eulerAngles.y;
            _pitch = startPitch;
            // Camera directly under the player: place it at eye height. Under the rig's CameraParent the parent already
            // sits at eye height.
            if (playerCamera != null && playerCamera.transform.parent == transform)
                playerCamera.transform.localPosition = new Vector3(0f, eyeHeight, 0f);
            PlayShiftFirstPersonRig.InputBlocked = s_menusOpen;
        }

        private void Start()
        {
            // Start looking down at the trimmer (the controller starts level).
            if (UsesExternalController)
            {
                bool portrait = playerCamera != null && playerCamera.aspect < 1f;
                _pitch = portrait ? portraitStartPitch : startPitch;
                rig.Pitch = _pitch;
            }
        }

        // Never grab the mouse while a menu is up: the title screen / settings page need a free cursor to click.
        private void OnEnable() => SetCursor(lockCursor && !MainMenu.IsOpen && !GameSettingsMenu.IsOpen);

        private void Update()
        {
            if (UsesExternalController && UpdateWithRig()) return;

            // Settings page open: the player stands still (the level keeps running so gifts still land on stream).
            bool menus = GameSettingsMenu.IsOpen || MainMenu.IsOpen;
            var input = InputOverride != null && !MainMenu.IsOpen ? InputOverride() : menus ? default : ReadInput();
            float dt = Time.deltaTime;

            UpdateFieldOfView();
            bool portrait = playerCamera != null && playerCamera.aspect < 1f;
            if (input.Look.y != 0f) _pitchTouched = true;
            if (!_pitchTouched && !UsesExternalController) _pitch = portrait ? portraitStartPitch : startPitch; // follows orientation until the player looks
            _yaw += input.Look.x;
            _pitch = Mathf.Clamp(_pitch + input.Look.y, minPitch, maxPitch);
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            if (playerCamera != null) playerCamera.transform.localRotation = Quaternion.Euler(-_pitch, 0f, 0f);

            if (input.TrimmerToggle) _toggleOn = !_toggleOn;
            if (blades != null) blades.On = _toggleOn || input.TrimmerHeld;

            var m = GrassLevelManager.Instance;
            float mod = m != null ? m.Modifiers.MoveSpeedMultiplier : 1f;
            if (blades != null) blades.SpeedMultiplier = m != null ? m.Modifiers.SpinMultiplier : 1f;
            float load = cutter != null ? cutter.Load : 0f;
            float speed = (input.Sprint ? sprintSpeed : walkSpeed) * mod * (1f - thickGrassResistance * load);

            Vector3 wish = transform.right * input.Move.x + transform.forward * input.Move.y;
            if (wish.sqrMagnitude > 1f) wish.Normalize();
            _velocity = Vector3.MoveTowards(_velocity, wish * speed, acceleration * dt);
            _verticalSpeed = _cc.isGrounded ? -1f : _verticalSpeed - 9.81f * dt;
            _cc.Move((_velocity + Vector3.up * _verticalSpeed) * dt);
            Speed = new Vector2(_velocity.x, _velocity.z).magnitude;
        }

        /// <summary>
        /// Rig mode: the controller walks and looks; this feeds it the movement multiplier (upgrades, thick grass), FOV
        /// and mouse settings, and takes control back for scripted input (captures). Menus pause it through the rig.
        /// Returns false when this component should run the built-in movement this frame.
        /// </summary>
        private bool UpdateWithRig()
        {
            bool menus = GameSettingsMenu.IsOpen || MainMenu.IsOpen;
            bool scripted = InputOverride != null && !MainMenu.IsOpen;
            if (scripted != _scripted)
            {
                // Hand the view over so neither side snaps.
                if (scripted) { _yaw = rig.Yaw; _pitch = rig.Pitch; }
                else rig.Pitch = _pitch;
                _scripted = scripted;
            }
            rig.Suspended = scripted;
            if (scripted) return false;

            var input = menus ? default : ReadInput(); // only the trimmer buttons are used; the controller reads move/look
            if (input.TrimmerToggle) _toggleOn = !_toggleOn;
            if (blades != null) blades.On = _toggleOn || input.TrimmerHeld;

            var m = GrassLevelManager.Instance;
            float mod = m != null ? m.Modifiers.MoveSpeedMultiplier : 1f;
            if (blades != null) blades.SpeedMultiplier = m != null ? m.Modifiers.SpinMultiplier : 1f;
            float load = cutter != null ? cutter.Load : 0f;
            rig.SpeedMultiplier = mod * (1f - thickGrassResistance * load);
            if (playerCamera != null) rig.VerticalFov = GameSettings.VerticalFov(playerCamera.aspect, maxVerticalFov);
            rig.MouseSensitivityScale = GameSettings.MouseSensitivity;

            _velocity = rig.Velocity;
            Speed = rig.Speed;
            _yaw = rig.Yaw;
            _pitch = rig.Pitch;
            return true;
        }

        /// <summary>The FOV setting is converted to the camera's vertical FOV for the current aspect ratio.</summary>
        private void UpdateFieldOfView()
        {
            if (playerCamera == null) return;
            float fov = GameSettings.VerticalFov(playerCamera.aspect, maxVerticalFov);
            if (!Mathf.Approximately(playerCamera.fieldOfView, fov)) playerCamera.fieldOfView = fov;
        }

        private FpInput ReadInput()
        {
            var i = new FpInput();
            bool locked = Cursor.lockState == CursorLockMode.Locked || !lockCursor;
            float sens = mouseSensitivity * GameSettings.MouseSensitivity;
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (kb != null)
            {
                i.Move.x = (kb.dKey.isPressed || kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed || kb.leftArrowKey.isPressed ? 1f : 0f);
                i.Move.y = (kb.wKey.isPressed || kb.upArrowKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed || kb.downArrowKey.isPressed ? 1f : 0f);
                i.Sprint = kb.leftShiftKey.isPressed;
                i.TrimmerToggle = kb.spaceKey.wasPressedThisFrame;
                // Esc belongs to GameSettingsMenu (opens settings and frees the cursor).
            }
            if (mouse != null)
            {
                if (locked)
                {
                    Vector2 d = mouse.delta.ReadValue() * sens;
                    i.Look = new Vector2(d.x, invertY ? -d.y : d.y);
                }
                i.TrimmerHeld = mouse.leftButton.isPressed && locked;
                if (mouse.leftButton.wasPressedThisFrame && lockCursor && Cursor.lockState != CursorLockMode.Locked) SetCursor(true);
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            i.Move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            i.Sprint = Input.GetKey(KeyCode.LeftShift);
            i.TrimmerToggle = Input.GetKeyDown(KeyCode.Space);
            // Esc belongs to GameSettingsMenu (opens settings and frees the cursor).
            if (locked)
            {
                // Legacy axes are already scaled per frame; 10x keeps the same feel as the Input System path.
                float k = sens * 10f;
                i.Look = new Vector2(Input.GetAxisRaw("Mouse X") * k, (invertY ? -1f : 1f) * Input.GetAxisRaw("Mouse Y") * k);
            }
            i.TrimmerHeld = Input.GetMouseButton(0) && locked;
            if (Input.GetMouseButtonDown(0) && lockCursor && Cursor.lockState != CursorLockMode.Locked) SetCursor(true);
#endif
            return i;
        }

        private static void SetCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}

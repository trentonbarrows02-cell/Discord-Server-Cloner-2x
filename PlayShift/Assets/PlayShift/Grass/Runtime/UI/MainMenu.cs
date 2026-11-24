using PlayShift.Grass.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayShift.Grass
{
    /// <summary>
    /// Title screen: CUT THE GRASS, PLAY / SETTINGS / QUIT, over the real lawn (blurred, camera slowly panning).
    /// Shown at startup and from the settings page (MAIN MENU). While open the level timer, player input, the
    /// gameplay HUD and the trimmer are held; PLAY (or CONTINUE mid-level) resumes. Built in code on the HUD canvas;
    /// inactive (zero cost) while closed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenu : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }
        public static MainMenu Instance { get; private set; }

        private static readonly Color PlayColor = new Color(0.17f, 0.7f, 0.29f, 1f);
        private static readonly Color SettingsColor = new Color(0.36f, 0.27f, 0.72f, 1f);
        private static readonly Color QuitColor = new Color(0.72f, 0.22f, 0.24f, 1f);

        [Tooltip("Show the title screen when the game starts.")]
        [SerializeField] private bool showOnStart = true;

        private GameObject _root;
        private TextMeshProUGUI _playLabel;
        private RectTransform _content;
        private Canvas _canvas;

        [Header("Map view behind the title")]
        [Tooltip("Degrees per second the camera circles the yard (slow, cinematic).")]
        [SerializeField] private float menuOrbitSpeed = 1.2f;
        [Tooltip("How far outside the yard's corners the camera circles (m); negative = inside the fence, so no trees " +
                 "block the foreground.")]
        [SerializeField] private float menuOrbitBeyondCorner = -8f;
        [Tooltip("Camera height above the ground (m).")]
        [SerializeField] private float menuCameraHeight = 15f;
        [Tooltip("Frame rate cap while the title screen is open (the game's own setting returns on Play). 0 = no cap.")]
        [SerializeField] private int menuFrameRate = 60;
        private int _savedTargetFps = -1, _savedVSync = -1;
        private bool _fpsCapped;
        [Tooltip("Height of the point it looks at over the yard centre (m): higher puts the lawn lower under the title.")]
        [SerializeField] private float menuLookHeight = 5f;
        [SerializeField] private float orbitFov = 50f;
        [Tooltip("Size the title layout was designed for (canvas units). It is scaled to fit the screen's height and width.")]
        [SerializeField] private Vector2 layoutSize = new Vector2(1100f, 1580f); // a margin above the title on every screen

        private Camera _menuCam, _playerCam;
        private float _orbitAngle = 35f;

        /// <summary>Camera the player currently sees: the map camera on the title screen, else the first-person one.</summary>
        public static Camera ActiveCamera
        {
            get
            {
                if (IsOpen && Instance != null && Instance._menuCam != null) return Instance._menuCam;
                var player = FindAnyObjectByType<FirstPersonController>();
                return player != null && player.Camera != null ? player.Camera : Camera.main;
            }
        }

        public Camera MenuCamera => _menuCam;

        private void Update()
        {
            // Fit the title layout inside the canvas (height and width), so the title is never cropped on short,
            // ultrawide, windowed or portrait screens.
            if (_canvas != null && _content != null)
            {
                var r = ((RectTransform)_canvas.transform).rect;
                float s = Mathf.Min(1f, Mathf.Min(r.height / layoutSize.y, r.width / layoutSize.x));
                if (!Mathf.Approximately(_content.localScale.x, s)) _content.localScale = new Vector3(s, s, 1f);
            }
            if (IsOpen)
            {
                Orbit(Time.unscaledDeltaTime);
                // Whatever locked the mouse (start-up order, focus changes), the title screen keeps it free and visible.
                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible) Cursor.visible = true;
            }
        }

        /// <summary>
        /// Cinematic backyard view: the camera circles slowly just outside the yard's corners, over the treetops,
        /// looking across the lawn, so the fence and forest frame the shot and the forest runs off every edge.
        /// </summary>
        private void Orbit(float dt)
        {
            if (_menuCam == null) return;
            _orbitAngle += menuOrbitSpeed * dt;
            var m = GrassLevelManager.Instance;
            Vector2 size = m != null && m.Settings != null ? m.Settings.fieldSizeMeters : new Vector2(24f, 24f);
            Vector3 origin = m != null ? m.FieldTransform.position : Vector3.zero;
            Vector3 centre = origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f);
            float radius = 0.5f * Mathf.Sqrt(size.x * size.x + size.y * size.y) + menuOrbitBeyondCorner;
            float a = _orbitAngle * Mathf.Deg2Rad;
            Vector3 pos = centre + new Vector3(Mathf.Sin(a) * radius, menuCameraHeight, Mathf.Cos(a) * radius);
            // Inside the fence all the way round (a circle through the corners would pass over the forest mid-side):
            // nothing between the camera and the lawn.
            const float inset = 3f;
            pos.x = Mathf.Clamp(pos.x, origin.x + inset, origin.x + size.x - inset);
            pos.z = Mathf.Clamp(pos.z, origin.z + inset, origin.z + size.y - inset);
            _menuCam.transform.position = pos;
            _menuCam.transform.LookAt(centre + new Vector3(0f, menuLookHeight, 0f));
        }

        private void EnsureMenuCamera()
        {
            if (_menuCam != null) return;
            _playerCam = ActiveCamera;
            var go = new GameObject("MenuCamera");
            go.tag = "MainCamera"; // so Camera.main (grass LOD/culling) follows it while the player camera is off
            _menuCam = go.AddComponent<Camera>();
            if (_playerCam != null)
            {
                _menuCam.clearFlags = _playerCam.clearFlags;
                _menuCam.backgroundColor = _playerCam.backgroundColor;
                _menuCam.cullingMask = _playerCam.cullingMask;
            }
            _menuCam.fieldOfView = orbitFov;
            _menuCam.nearClipPlane = 0.3f;
            _menuCam.farClipPlane = 2000f; // the hills reach the horizon
            _menuCam.enabled = false;
        }

        /// <summary>Switches between the map camera (title screen) and the first-person camera.</summary>
        private void UseMenuCamera(bool menu)
        {
            EnsureMenuCamera();
            if (_playerCam == null)
            {
                var player = FindAnyObjectByType<FirstPersonController>();
                _playerCam = player != null ? player.Camera : null;
            }
            if (menu) Orbit(0f);
            _menuCam.enabled = menu;
            if (_playerCam != null) _playerCam.enabled = !menu; // AudioListener on its GameObject stays active
        }

        public void Build(RectTransform canvasRoot)
        {
            Instance = this;
            // Light tint only: the map behind is the backdrop, the chunky outlined title reads over it.
            var dim = GrassUiFactory.Panel("MainMenu", canvasRoot, new Color(0.02f, 0.01f, 0.08f, 0.15f), rounded: false, raycast: true);
            GrassUiFactory.Stretch(dim.rectTransform);
            _root = dim.gameObject;
            // All content in one container: laid out for a tall (vertical) screen, scaled down on wide screens.
            _content = GrassUiFactory.Place(GrassUiFactory.Rect("Content", dim.rectTransform), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000f, 1400f));
            var rt = _content;
            _canvas = canvasRoot.GetComponentInParent<Canvas>();

            // Title: CUT THE / GRASS, stacked, chunky HUD font.
            var top = GrassHud.HudText("TitleTop", rt, "CUT THE", 120f, Color.white, GrassHud.LabelMaterial, 10f);
            GrassUiFactory.Place(top.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 560f), new Vector2(1000f, 150f));
            var big = GrassHud.HudText("TitleGrass", rt, "GRASS", 230f, GrassHud.CounterGreen, GrassHud.GreenMaterial, 14f);
            GrassUiFactory.Place(big.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 380f), new Vector2(1000f, 260f));

            _playLabel = MenuButton(rt, "PLAY", PlayColor, 80f, new Vector2(0f, 60f), new Vector2(560f, 170f), Play);
            MenuButton(rt, "SETTINGS", SettingsColor, 60f, new Vector2(0f, -150f), new Vector2(560f, 140f), OpenSettings);
            MenuButton(rt, "QUIT", QuitColor, 60f, new Vector2(0f, -330f), new Vector2(560f, 140f), Quit);

            _root.SetActive(false);
            if (showOnStart) Show();
        }

        /// <summary>Opens the title screen (start of game, or MAIN MENU from settings).</summary>
        public void Show()
        {
            if (menuFrameRate > 0 && !_fpsCapped)
            {
                _fpsCapped = true;
                _savedTargetFps = Application.targetFrameRate;
                _savedVSync = QualitySettings.vSyncCount;
                QualitySettings.vSyncCount = 0;          // so the cap also holds on 120-240 Hz screens
                Application.targetFrameRate = menuFrameRate;
            }
            if (_root == null) return;
            IsOpen = true;
            _root.SetActive(true);
            var m = GrassLevelManager.Instance;
            bool midLevel = m != null && m.Progression != null && m.Progression.ElapsedSeconds > 0.5;
            _playLabel.text = midLevel ? "CONTINUE" : "PLAY";
            UseMenuCamera(true);
            Apply(true);
        }

        private void RestoreFrameRate()
        {
            if (!_fpsCapped) return;
            _fpsCapped = false;
            Application.targetFrameRate = _savedTargetFps;
            QualitySettings.vSyncCount = _savedVSync;
        }

        public void Play()
        {
            RestoreFrameRate();
            if (_root == null) return;
            IsOpen = false;
            _root.SetActive(false);
            UseMenuCamera(false);
            Apply(false);
        }

        private void OpenSettings()
        {
            if (GameSettingsMenu.Instance != null) GameSettingsMenu.Instance.SetOpen(true);
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        /// <summary>Holds/releases everything the title screen pauses.</summary>
        private static void Apply(bool menuOpen)
        {
            var m = GrassLevelManager.Instance;
            if (m != null) m.HoldTimer = menuOpen;
            // The map is shown sharp; blur only when the settings page is open over it.
            MenuBlurEffect.Set(ActiveCamera, GameSettingsMenu.IsOpen);
            Cursor.lockState = menuOpen ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = menuOpen;
        }

        private void OnDestroy()
        {
            if (Instance == this) { Instance = null; IsOpen = false; }
        }

        private static TextMeshProUGUI MenuButton(RectTransform parent, string label, Color color, float fontSize, Vector2 pos, Vector2 size,
                                                 UnityEngine.Events.UnityAction onClick)
        {
            // Drop shadow + rounded button, chunky label (game style, matches the settings card).
            var shadow = GrassUiFactory.Panel(label + "Shadow", parent, new Color(0f, 0f, 0f, 0.45f));
            GrassUiFactory.Place(shadow.rectTransform, new Vector2(0.5f, 0.5f), pos + new Vector2(0f, -12f), size);
            shadow.pixelsPerUnitMultiplier = 0.45f;
            var b = GrassUiFactory.Button(label, parent, label, color, out var text);
            GrassUiFactory.Place((RectTransform)b.transform, new Vector2(0.5f, 0.5f), pos, size);
            ((Image)b.targetGraphic).pixelsPerUnitMultiplier = 0.45f;
            text.fontSize = fontSize;
            var font = Resources.Load<TMP_FontAsset>("PlayShiftFonts/HudFont SDF");
            if (font != null && GrassHud.LabelMaterial != null)
            {
                text.font = font;
                text.fontStyle = FontStyles.Normal;
                text.fontSharedMaterial = GrassHud.LabelMaterial;
                text.characterSpacing = 6f;
            }
            b.onClick.AddListener(onClick);
            b.gameObject.AddComponent<MenuButtonMotion>().Shadow = shadow.rectTransform;
            return text;
        }
    }
}

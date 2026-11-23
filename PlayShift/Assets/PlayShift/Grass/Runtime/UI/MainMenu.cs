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
        [Tooltip("Degrees per second the camera circles the map.")]
        [SerializeField] private float orbitSpeed = 3f;
        [Tooltip("Orbit radius as a multiple of the lawn size (plus a margin), i.e. how far zoomed out.")]
        [SerializeField] private float orbitDistance = 1.15f;
        [SerializeField] private float orbitHeight = 0.55f;
        [SerializeField] private float orbitFov = 50f;

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
            // Wide (landscape) screens: the tall layout is scaled to fit the height.
            if (_canvas != null)
            {
                var r = _canvas.pixelRect;
                float s = r.height > r.width ? 1f : 0.68f;
                if (!Mathf.Approximately(_content.localScale.x, s)) _content.localScale = new Vector3(s, s, 1f);
            }
            if (IsOpen) Orbit(Time.unscaledDeltaTime);
        }

        /// <summary>Wide view of the whole map: a camera circling high above the lawn.</summary>
        private void Orbit(float dt)
        {
            if (_menuCam == null) return;
            _orbitAngle += orbitSpeed * dt;
            var m = GrassLevelManager.Instance;
            Vector2 size = m != null && m.Settings != null ? m.Settings.fieldSizeMeters : new Vector2(24f, 24f);
            Vector3 origin = m != null ? m.FieldTransform.position : Vector3.zero;
            Vector3 centre = origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f);
            float radius = Mathf.Max(size.x, size.y) * orbitDistance + 8f;
            float a = _orbitAngle * Mathf.Deg2Rad;
            _menuCam.transform.position = centre + new Vector3(Mathf.Sin(a) * radius, radius * orbitHeight, Mathf.Cos(a) * radius);
            // Aim a little past the centre so the lawn sits in the lower half, under the title.
            _menuCam.transform.LookAt(centre + new Vector3(0f, radius * 0.18f, 0f));
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
            _menuCam.farClipPlane = 900f;
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
            var big = GrassHud.HudText("TitleGrass", rt, "GRASS", 230f, GrassHud.CounterGreen, GrassHud.CounterMaterial, 14f);
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
            if (_root == null) return;
            IsOpen = true;
            _root.SetActive(true);
            var m = GrassLevelManager.Instance;
            bool midLevel = m != null && m.Progression != null && m.Progression.ElapsedSeconds > 0.5;
            _playLabel.text = midLevel ? "CONTINUE" : "PLAY";
            UseMenuCamera(true);
            Apply(true);
        }

        public void Play()
        {
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
            return text;
        }
    }
}

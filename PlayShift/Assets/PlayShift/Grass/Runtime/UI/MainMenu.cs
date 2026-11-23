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

        private void Update()
        {
            // Wide (landscape) screens: the tall layout is scaled to fit the height. Panning view behind the menu.
            if (_canvas == null) return;
            var r = _canvas.pixelRect;
            float s = r.height > r.width ? 1f : 0.68f;
            if (!Mathf.Approximately(_content.localScale.x, s)) _content.localScale = new Vector3(s, s, 1f);
        }

        public void Build(RectTransform canvasRoot)
        {
            Instance = this;
            var dim = GrassUiFactory.Panel("MainMenu", canvasRoot, new Color(0.02f, 0.01f, 0.08f, 0.35f), rounded: false, raycast: true);
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
            Apply(true);
        }

        public void Play()
        {
            if (_root == null) return;
            IsOpen = false;
            _root.SetActive(false);
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
            var player = FindAnyObjectByType<FirstPersonController>();
            var cam = player != null && player.Camera != null ? player.Camera : Camera.main;
            MenuBlurEffect.Set(cam, menuOpen || GameSettingsMenu.IsOpen);
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

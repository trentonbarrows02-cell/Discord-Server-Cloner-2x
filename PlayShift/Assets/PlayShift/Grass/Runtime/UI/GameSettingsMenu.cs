using System.Collections.Generic;
using PlayShift.Grass.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayShift.Grass
{
    /// <summary>
    /// Settings page (Esc): resolution, display mode, VSync, field of view (60-120), grass quality. Built in code on
    /// the HUD canvas, so no prefabs or scene changes are needed. While closed the panel is inactive (zero cost);
    /// while open the player's movement/look is paused (the level keeps running so gifts still land on stream).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameSettingsMenu : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }
        public static GameSettingsMenu Instance { get; private set; }

        private static readonly Color CardColor = new Color(0.1f, 0.08f, 0.2f, 0.94f);
        private static readonly Color ButtonColor = new Color(0.27f, 0.2f, 0.5f, 1f);
        private static readonly Color RimColor = new Color(0.45f, 0.34f, 0.86f, 1f);
        private static readonly Color OnColor = new Color(0.17f, 0.7f, 0.29f, 1f);
        private static readonly Color OffColor = new Color(0.55f, 0.18f, 0.2f, 1f);

        private GameObject _root;
        private TextMeshProUGUI _resolutionValue, _modeValue, _vsyncLabel, _fovValue, _qualityValue;
        private Image _vsyncButton;
        private Slider _fovSlider;
        private List<Vector2Int> _resolutions;
        private int _resolutionIndex;

        private static readonly FullScreenMode[] Modes =
            { FullScreenMode.Windowed, FullScreenMode.FullScreenWindow, FullScreenMode.ExclusiveFullScreen };
        private static readonly string[] ModeNames = { "WINDOWED", "BORDERLESS", "FULLSCREEN" };
        private static readonly string[] QualityNames = { "LOW", "MEDIUM", "HIGH", "ULTRA" };

        /// <summary>Builds the page under the HUD canvas root (called by <see cref="GrassHud"/>).</summary>
        public void Build(RectTransform canvasRoot)
        {
            Instance = this;
            GameSettings.Load();

            var dim = GrassUiFactory.Panel("SettingsMenu", canvasRoot, new Color(0f, 0f, 0f, 0.55f), rounded: false, raycast: true);
            GrassUiFactory.Stretch(dim.rectTransform);
            _root = dim.gameObject;
            // Game-style card: soft drop shadow, thick violet rim, big rounded corners.
            var cardSize = new Vector2(960f, 1120f);
            var shadow = GrassUiFactory.Panel("CardShadow", dim.rectTransform, new Color(0f, 0f, 0f, 0.45f));
            GrassUiFactory.Place(shadow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -18f), cardSize + new Vector2(28f, 28f));
            var rim = GrassUiFactory.Panel("CardRim", dim.rectTransform, RimColor);
            GrassUiFactory.Place(rim.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, cardSize + new Vector2(28f, 28f));
            var cardImage = GrassUiFactory.Panel("Card", dim.rectTransform, CardColor);
            var card = cardImage.rectTransform;
            GrassUiFactory.Place(card, new Vector2(0.5f, 0.5f), Vector2.zero, cardSize);
            // The rounded sprite has 20 px corners; < 1 enlarges them (about 55-60 px here).
            shadow.pixelsPerUnitMultiplier = 0.33f;
            rim.pixelsPerUnitMultiplier = 0.33f;
            cardImage.pixelsPerUnitMultiplier = 0.36f;

            var title = GrassHud.HudText("Title", card, "SETTINGS", 84f, Color.white, GrassHud.LabelMaterial, 8f);
            GrassUiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(900f, 100f));

            float y = -190f;
            const float step = 150f;
            _resolutionValue = Stepper(card, "RESOLUTION", y, () => StepResolution(-1), () => StepResolution(1)); y -= step;
            _modeValue = Stepper(card, "DISPLAY", y, () => StepMode(-1), () => StepMode(1)); y -= step;
            BuildVSync(card, y); y -= step;
            BuildFov(card, y); y -= step;
            _qualityValue = Stepper(card, "GRASS QUALITY", y, () => StepQuality(-1), () => StepQuality(1));

            var close = GrassUiFactory.Button("Close", card, "CLOSE", OnColor, out var closeLabel);
            StyleButtonLabel(closeLabel, 50f);
            _closeButton = (RectTransform)close.transform;
            close.onClick.AddListener(() => SetOpen(false));
            var menu = GrassUiFactory.Button("MainMenu", card, "MAIN MENU", ButtonColor, out var menuLabel);
            StyleButtonLabel(menuLabel, 44f);
            _menuButton = (RectTransform)menu.transform;
            menu.onClick.AddListener(() =>
            {
                SetOpen(false);
                if (MainMenu.Instance != null) MainMenu.Instance.Show();
            });
            LayoutBottomButtons();

            var hint = GrassUiFactory.Text("Hint", card, "ESC opens / closes settings", 26f, new Color(1f, 1f, 1f, 0.6f));
            GrassUiFactory.Place(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(900f, 34f));

            _resolutions = GameSettings.AvailableResolutions();
            _resolutionIndex = ClosestResolution(GameSettings.Width, GameSettings.Height);
            RefreshLabels();
            _root.SetActive(false);
        }

        public void SetOpen(bool open)
        {
            if (_root == null) return;
            IsOpen = open;
            _root.SetActive(open);
            // Blur whatever the player is looking at: the first-person view, or the map on the title screen.
            MenuBlurEffect.Set(MainMenu.ActiveCamera, open);
            if (open)
            {
                _resolutions = GameSettings.AvailableResolutions(); // monitor may have changed
                _resolutionIndex = ClosestResolution(Screen.width, Screen.height);
                RefreshLabels();
                LayoutBottomButtons();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                GameSettings.Save();
                bool backToTitle = MainMenu.IsOpen;
                Cursor.lockState = backToTitle ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = backToTitle;
            }
        }

        private void Update()
        {
            // Esc toggles settings during play; on the title screen it only closes settings (menu buttons handle the rest).
            if (EscapePressed() && (IsOpen || !MainMenu.IsOpen)) SetOpen(!IsOpen);
        }

        private RectTransform _closeButton, _menuButton;

        /// <summary>CLOSE alone when opened from the title screen; CLOSE + MAIN MENU side by side during play.</summary>
        private void LayoutBottomButtons()
        {
            if (_closeButton == null) return;
            bool fromTitle = MainMenu.IsOpen;
            _menuButton.gameObject.SetActive(!fromTitle);
            if (fromTitle) GrassUiFactory.Place(_closeButton, new Vector2(0.5f, 0f), new Vector2(0f, 64f), new Vector2(380f, 100f));
            else
            {
                GrassUiFactory.Place(_closeButton, new Vector2(0.5f, 0f), new Vector2(-200f, 64f), new Vector2(360f, 100f));
                GrassUiFactory.Place(_menuButton, new Vector2(0.5f, 0f), new Vector2(200f, 64f), new Vector2(360f, 100f));
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) { Instance = null; IsOpen = false; }
        }

        private static bool EscapePressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && kb.escapeKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Escape);
#else
            return false;
#endif
        }

        // ------------------------------------------------------------------ actions

        private void StepResolution(int dir)
        {
            if (_resolutions.Count == 0) return;
            _resolutionIndex = (_resolutionIndex + dir + _resolutions.Count) % _resolutions.Count;
            var r = _resolutions[_resolutionIndex];
            GameSettings.SetResolution(r.x, r.y);
            RefreshLabels();
        }

        private void StepMode(int dir)
        {
            int i = System.Array.IndexOf(Modes, GameSettings.Mode);
            if (i < 0) i = 0;
            GameSettings.SetMode(Modes[(i + dir + Modes.Length) % Modes.Length]);
            RefreshLabels();
        }

        private void StepQuality(int dir)
        {
            int q = ((int)GameSettings.GrassQuality + dir + QualityNames.Length) % QualityNames.Length;
            GameSettings.SetGrassQuality((GrassQuality)q);
            RefreshLabels();
        }

        private void RefreshLabels()
        {
            if (_resolutions.Count > 0)
            {
                var r = _resolutions[_resolutionIndex];
                _resolutionValue.text = $"{r.x} x {r.y}{(r.y > r.x ? "  (VERTICAL)" : "")}";
            }
            int m = System.Array.IndexOf(Modes, GameSettings.Mode);
            _modeValue.text = ModeNames[m < 0 ? 0 : m];
            _vsyncLabel.text = GameSettings.VSync ? "ON" : "OFF";
            _vsyncButton.color = GameSettings.VSync ? OnColor : OffColor;
            _fovSlider.SetValueWithoutNotify(GameSettings.FovHorizontal);
            _fovValue.text = $"{GameSettings.FovHorizontal:0}";
            _qualityValue.text = QualityNames[Mathf.Clamp((int)GameSettings.GrassQuality, 0, QualityNames.Length - 1)];
        }

        private int ClosestResolution(int w, int h)
        {
            int best = 0;
            long bestD = long.MaxValue;
            for (int i = 0; i < _resolutions.Count; i++)
            {
                long dx = _resolutions[i].x - w, dy = _resolutions[i].y - h;
                long d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // ------------------------------------------------------------------ widgets

        private static TextMeshProUGUI RowLabel(RectTransform card, string text, float y)
        {
            var label = GrassUiFactory.Text(text + "Label", card, text, 34f, Color.white, TextAlignmentOptions.Left, FontStyles.Bold);
            GrassUiFactory.Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(60f, y), new Vector2(380f, 100f));
            return label;
        }

        /// <summary>Label on the left; [&lt;] value [&gt;] on the right. Returns the value text.</summary>
        private static TextMeshProUGUI Stepper(RectTransform card, string label, float y, UnityEngine.Events.UnityAction prev, UnityEngine.Events.UnityAction next)
        {
            RowLabel(card, label, y);
            var left = GrassUiFactory.Button(label + "Prev", card, "<", ButtonColor, out var lt);
            StyleButtonLabel(lt, 50f);
            GrassUiFactory.Place((RectTransform)left.transform, new Vector2(0f, 1f), new Vector2(440f, y), new Vector2(86f, 100f));
            left.onClick.AddListener(prev);
            var value = GrassUiFactory.Text(label + "Value", card, "", 36f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
            GrassUiFactory.Place(value.rectTransform, new Vector2(0f, 1f), new Vector2(530f, y), new Vector2(284f, 100f));
            var right = GrassUiFactory.Button(label + "Next", card, ">", ButtonColor, out var rt);
            StyleButtonLabel(rt, 50f);
            GrassUiFactory.Place((RectTransform)right.transform, new Vector2(0f, 1f), new Vector2(818f, y), new Vector2(86f, 100f));
            right.onClick.AddListener(next);
            return value;
        }

        private void BuildVSync(RectTransform card, float y)
        {
            RowLabel(card, "VSYNC", y);
            var b = GrassUiFactory.Button("VSyncToggle", card, "ON", OnColor, out _vsyncLabel);
            StyleButtonLabel(_vsyncLabel, 46f);
            _vsyncButton = (Image)b.targetGraphic;
            GrassUiFactory.Place((RectTransform)b.transform, new Vector2(0f, 1f), new Vector2(440f, y), new Vector2(464f, 100f));
            b.onClick.AddListener(() => { GameSettings.SetVSync(!GameSettings.VSync); RefreshLabels(); });
        }

        private void BuildFov(RectTransform card, float y)
        {
            RowLabel(card, "FOV", y);
            var track = GrassUiFactory.Panel("FovSlider", card, new Color(0f, 0f, 0f, 0.45f), rounded: true, raycast: true);
            GrassUiFactory.Place(track.rectTransform, new Vector2(0f, 1f), new Vector2(440f, y - 34f), new Vector2(360f, 32f));
            var fillArea = GrassUiFactory.Stretch(GrassUiFactory.Rect("FillArea", track.rectTransform), 4f);
            var fill = GrassUiFactory.Panel("Fill", fillArea, OnColor);
            GrassUiFactory.Stretch(fill.rectTransform);
            var handleArea = GrassUiFactory.Stretch(GrassUiFactory.Rect("HandleArea", track.rectTransform));
            handleArea.offsetMin = new Vector2(18f, 0f); handleArea.offsetMax = new Vector2(-18f, 0f);
            var handle = GrassUiFactory.Panel("Handle", handleArea, Color.white, rounded: true, raycast: true);
            handle.rectTransform.anchorMin = new Vector2(0f, 0f);
            handle.rectTransform.anchorMax = new Vector2(0f, 1f);
            handle.rectTransform.sizeDelta = new Vector2(36f, 28f);

            _fovSlider = track.gameObject.AddComponent<Slider>();
            _fovSlider.fillRect = fill.rectTransform;
            _fovSlider.handleRect = handle.rectTransform;
            _fovSlider.targetGraphic = handle;
            _fovSlider.direction = Slider.Direction.LeftToRight;
            _fovSlider.minValue = GameSettings.MinFov;
            _fovSlider.maxValue = GameSettings.MaxFov;
            _fovSlider.wholeNumbers = true;
            _fovSlider.onValueChanged.AddListener(v => { GameSettings.SetFov(v); _fovValue.text = $"{GameSettings.FovHorizontal:0}"; });

            _fovValue = GrassUiFactory.Text("FovValue", card, "90", 40f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
            GrassUiFactory.Place(_fovValue.rectTransform, new Vector2(0f, 1f), new Vector2(810f, y), new Vector2(100f, 100f));
        }

        private static void StyleButtonLabel(TextMeshProUGUI t, float size)
        {
            t.fontSize = size;
            var font = Resources.Load<TMP_FontAsset>("PlayShiftFonts/HudFont SDF");
            if (font != null && GrassHud.LabelMaterial != null)
            {
                t.font = font;
                t.fontStyle = FontStyles.Normal;
                t.fontSharedMaterial = GrassHud.LabelMaterial;
            }
        }
    }
}

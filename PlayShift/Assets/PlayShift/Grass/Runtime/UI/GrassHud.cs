using System.Collections.Generic;
using PlayShift.Grass.UI;
using TMPro;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// GRASS LEFT counter, level/timer readout, TikTok gift feed, the LEVEL CLEARED screen and the final
    /// ALL LEVELS COMPLETED screen. Built entirely in code (uGUI + TextMeshPro) so it drops into any scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GrassHud : MonoBehaviour
    {
        private static readonly Color Ink = new Color(1f, 1f, 1f, 1f);
        private static readonly Color Muted = new Color(1f, 1f, 1f, 0.72f);
        private static readonly Color PanelBg = new Color(0.05f, 0.09f, 0.05f, 0.72f);
        private static readonly Color Grass = new Color(0.45f, 0.86f, 0.3f, 1f);
        private static readonly Color Gold = new Color(1f, 0.82f, 0.22f, 1f);
        private static readonly Color GiftAdd = new Color(0.55f, 1f, 0.45f, 1f);
        private static readonly Color GiftRemove = new Color(1f, 0.55f, 0.45f, 1f);
        private static readonly Color GiftTaller = new Color(0.55f, 0.8f, 1f, 1f);
        private static readonly Color CounterGreen = new Color(0.5f, 0.96f, 0.22f, 1f);   // flat lime
        private Color _counterFill = CounterGreen;

        /// <summary>Restyles the GRASS LEFT number (used by the capture tool to preview colour options side by side).</summary>
        internal void PreviewCounterStyle(Color fill, Material preset)
        {
            _counterFill = fill;
            _counterPunch = 0f;
            _grassLeftValue.color = fill;
            if (preset != null) _grassLeftValue.fontSharedMaterial = preset;
        }
        private static readonly Color CounterOutline = new Color(0.16f, 0.11f, 0.29f, 1f); // dark indigo (fallback font path)

        [SerializeField] private int sortingOrder = 100;
        [SerializeField] private Vector2 referenceResolution = new Vector2(1920f, 1080f);
        [Tooltip("Vertical (TikTok) screens: layout is scaled to the screen width and the counter/popup enlarged by this.")]
        [SerializeField] private float portraitHudScale = 1.2f;

        private Canvas _canvas;
        private UnityEngine.UI.CanvasScaler _scaler;
        private bool _portrait, _orientationKnown;
        private float _hudScale = 1f;
        [SerializeField] private float giftToastSeconds = 4f;

        private GrassLevelManager _manager;
        private RectTransform _root;

        // Playing HUD
        private TextMeshProUGUI _grassLeftValue;
        private TextMeshProUGUI _levelLabel;
        private TextMeshProUGUI _heightLabel;
        private TextMeshProUGUI _timerLabel;
        private RectTransform _counterPanel;
        private RectTransform _levelInfo;
        private long _shownRemaining = -1;
        private float _counterPunch;
        private Color _counterFlashColor = Ink;

        // Gift popup (under the counter)
        [SerializeField] private float giftPopupSeconds = 2.6f;
        [Tooltip("Also list gifts as small text lines bottom-left (off: the popup under the counter is enough).")]
        [SerializeField] private bool showGiftFeed;
        private RectTransform _giftPopup;
        private CanvasGroup _giftPopupGroup;
        private TextMeshProUGUI _giftPopupAmount, _giftPopupUser;
        private float _giftPopupShownAt = -100f;
        private string _giftPopupKey;
        private double _giftPopupTotal;
        private static readonly Color GiftAddColor = new Color(1f, 0.45f, 0.3f, 1f);

        // Gift feed
        private RectTransform _giftFeed;
        private readonly List<(TextMeshProUGUI text, float until)> _toasts = new List<(TextMeshProUGUI, float)>();

        // Overlays
        private Overlay _cleared;
        private Overlay _victory;
        private TextMeshProUGUI _clearedTitle, _clearedSubtitle, _clearedTime, _clearedGrass, _clearedDetail, _clearedNextHint;
        private UnityEngine.UI.Button _nextButton;
        private TextMeshProUGUI _victoryTitle, _victorySubtitle, _victoryTable, _victoryTotals;
        private UnityEngine.UI.Button _playAgainButton;

        private sealed class Overlay
        {
            public GameObject Root;
            public RectTransform Card;
            public CelebrationFx Fx;
            public TextMeshProUGUI Title;
            public float ShownAt;
        }

        private void Awake()
        {
            BuildCanvas();
            BuildPlayingHud();
            BuildGiftFeed();
            BuildClearedOverlay();
            BuildVictoryOverlay();
            HideOverlays();
            // Settings page (Esc) on top of everything else on this canvas.
            gameObject.AddComponent<GameSettingsMenu>().Build(_root);
            if (Resources.Load<TMP_Settings>("TMP Settings") == null || TMP_Settings.defaultFontAsset == null)
                Debug.LogError("[PlayShift Grass] TextMeshPro has no default font. Import 'TMP Essential Resources' " +
                               "(Window > TextMeshPro) or run PlayShift > Grass > Create Level Setup.", this);
        }

        public void Bind(GrassLevelManager manager)
        {
            _manager = manager;
            _nextButton.onClick.RemoveAllListeners();
            _nextButton.onClick.AddListener(manager.NextLevel);
            _playAgainButton.onClick.RemoveAllListeners();
            _playAgainButton.onClick.AddListener(manager.RestartCampaign);
        }

        // ------------------------------------------------------------------ state changes

        public void ShowPlaying(int levelIndex, int levelCount, GrassLevelDefinition level)
        {
            HideOverlays();
            _levelLabel.text = $"LEVEL {levelIndex + 1} / {levelCount}";
            _heightLabel.text = "Grass " + GrassUiFactory.FormatHeightRange(level.MinHeightCm, level.MaxHeightCm);
            _shownRemaining = -1;
            _counterFlashColor = _counterFill;
            _grassLeftValue.color = _counterFill;
            Refresh();
        }

        public void ShowLevelCleared(LevelResult result, GrassLevelDefinition next)
        {
            _shownRemaining = -1;
            Refresh(); // counter snaps to the final 0, then stops updating while the overlay is up
            _clearedTitle.text = "LEVEL CLEARED!";
            _clearedSubtitle.text = $"{result.LevelName.ToUpperInvariant()} COMPLETE";
            _clearedTime.text = GrassUiFactory.FormatTime(result.CompletionSeconds);
            _clearedGrass.text = result.TotalGrassCut.ToString("N0");
            _clearedDetail.text = DescribeGiftShare(result);
            _clearedNextHint.text = next != null
                ? $"Next: {next.DisplayName}  -  {GrassUiFactory.FormatHeightRange(next.MinHeightCm, next.MaxHeightCm)} grass"
                : string.Empty;
            Show(_cleared, burst: 140, rain: 18f);
            Select(_nextButton);
        }

        public void ShowAllLevelsCompleted(IReadOnlyList<LevelResult> results)
        {
            _shownRemaining = -1;
            Refresh();
            int count = results.Count;
            _victoryTitle.text = $"ALL {count} LEVELS COMPLETED!";
            _victorySubtitle.text = "EVERY LAWN MOWED - CHAMPION!";

            var sb = new System.Text.StringBuilder();
            double totalTime = 0;
            long totalCut = 0;
            foreach (var r in results)
            {
                sb.Append("<mspace=0.6em>").Append(r.LevelNumber).Append("</mspace>  ")
                  .Append(r.LevelName).Append("   <color=#FFD54A>")
                  .Append(GrassUiFactory.FormatTime(r.CompletionSeconds)).Append("</color>   ")
                  .Append(r.TotalGrassCut.ToString("N0")).Append(" cut\n");
                totalTime += r.CompletionSeconds;
                totalCut += r.TotalGrassCut;
            }
            _victoryTable.text = sb.ToString();
            _victoryTotals.text = $"TOTAL TIME  <color=#FFD54A>{GrassUiFactory.FormatTime(totalTime)}</color>     TOTAL GRASS CUT  <color=#8CF06A>{totalCut:N0}</color>";
            Show(_victory, burst: 260, rain: 45f);
            Select(_playAgainButton);
        }

        /// <summary>Adds a line to the on-screen gift feed.</summary>
        public void ShowGiftToast(GiftApplication gift)
        {
            if (!gift.WasApplied) return;
            Color c = gift.Action == GiftActionType.AddGrass ? GiftAdd
                    : gift.Action == GiftActionType.RemoveGrass ? GiftRemove : GiftTaller;
            // The big popup under the counter replaces the old text feed (keeps the stream clean, like TikTok games).
            if (showGiftFeed) ShowToast(gift.Describe(), c);
            ShowGiftPopup(gift);
            _counterFlashColor = c;
            _counterPunch = 1f;
            if (gift.Action == GiftActionType.IncreaseHeight && _manager != null && _manager.Field != null)
                _heightLabel.text = "Grass " + GrassUiFactory.FormatHeightRange(_manager.Field.CurrentMinHeightCm, _manager.Field.CurrentMaxHeightCm);
        }

        /// <summary>Big "-25,000 / username" popup under the counter. Same viewer + same effect while visible = add up.</summary>
        private void ShowGiftPopup(GiftApplication gift)
        {
            string user = gift.Gift.UserName ?? string.Empty;
            string key = user + "|" + gift.Action;
            bool visible = Time.unscaledTime - _giftPopupShownAt < giftPopupSeconds;
            _giftPopupTotal = visible && key == _giftPopupKey ? _giftPopupTotal + gift.Applied : gift.Applied;
            _giftPopupKey = key;

            string text;
            Color color;
            switch (gift.Action)
            {
                case GiftActionType.RemoveGrass: text = "-" + ((long)_giftPopupTotal).ToString("N0"); color = CounterGreen; break;
                case GiftActionType.AddGrass: text = "+" + ((long)_giftPopupTotal).ToString("N0"); color = GiftAddColor; break;
                case GiftActionType.IncreaseHeight: text = $"+{_giftPopupTotal:0.#} CM TALLER"; color = GiftTaller; break;
                case GiftActionType.SpeedBoost: text = "SPEED BOOST"; color = Gold; break;
                case GiftActionType.RadiusBoost: text = "WIDER CUT"; color = Gold; break;
                case GiftActionType.SlowTrimmer: text = "SLOWED DOWN"; color = GiftAddColor; break;
                default: text = $"+{(int)_giftPopupTotal} BLADE{(_giftPopupTotal > 1 ? "S" : "")}"; color = Gold; break;
            }
            _giftPopupAmount.text = text;
            _giftPopupAmount.color = color;
            _giftPopupUser.text = user;
            _giftPopupShownAt = Time.unscaledTime;
        }

        private void AnimateGiftPopup()
        {
            float age = Time.unscaledTime - _giftPopupShownAt;
            const float popIn = 0.18f, fadeOut = 0.35f;
            if (age > giftPopupSeconds + fadeOut)
            {
                if (_giftPopupGroup.alpha != 0f) _giftPopupGroup.alpha = 0f;
                return;
            }
            float k = Mathf.Clamp01(age / popIn);
            float s = _hudScale * Mathf.Lerp(1.35f, 1f, 1f - (1f - k) * (1f - k)); // ease-out punch
            _giftPopup.localScale = new Vector3(s, s, 1f);
            _giftPopupGroup.alpha = age < giftPopupSeconds ? 1f : 1f - (age - giftPopupSeconds) / fadeOut;
        }

        public void ShowToast(string message, Color color)
        {
            var t = GrassUiFactory.Text("Toast", _giftFeed, message, 30f, color, TextAlignmentOptions.BottomLeft, FontStyles.Bold);
            t.rectTransform.sizeDelta = new Vector2(900f, 40f);
            t.outlineWidth = 0.18f;
            t.outlineColor = new Color32(0, 0, 0, 200);
            _toasts.Add((t, Time.unscaledTime + giftToastSeconds));
            while (_toasts.Count > 6) { Destroy(_toasts[0].text.gameObject); _toasts.RemoveAt(0); }
            LayoutToasts();
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>
        /// Portrait (vertical TikTok stream) vs landscape: switches the canvas scaling reference so the layout follows
        /// the screen width in portrait, and enlarges the counter/popup there. Only does work when orientation flips.
        /// </summary>
        private void UpdateOrientation()
        {
            if (_canvas == null) return;
            var r = _canvas.pixelRect;
            if (r.width <= 0f || r.height <= 0f) return;
            bool portrait = r.height > r.width;
            if (_orientationKnown && portrait == _portrait) return;
            _orientationKnown = true;
            _portrait = portrait;
            _scaler.referenceResolution = portrait ? new Vector2(1080f, 1920f) : referenceResolution;
            _scaler.matchWidthOrHeight = portrait ? 0f : 0.5f; // portrait: fit the width
            _hudScale = portrait ? portraitHudScale : 1f;
            _counterPanel.localScale = new Vector3(_hudScale, _hudScale, 1f);
            _giftPopup.anchoredPosition = new Vector2(0f, -12f - 238f * _hudScale); // keep it just under the counter
            // The counter spans the width in portrait: level / time move to the bottom-left corner out of its way.
            if (portrait) GrassUiFactory.Place(_levelInfo, new Vector2(0f, 0f), new Vector2(36f, 40f), _levelInfo.sizeDelta);
            else GrassUiFactory.Place(_levelInfo, new Vector2(0f, 1f), new Vector2(36f, -28f), _levelInfo.sizeDelta);
        }

        /// <summary>Cards wider than a portrait screen shrink to fit.</summary>
        private float OverlayFit(Overlay o) => _portrait ? Mathf.Min(1f, 1020f / Mathf.Max(1f, o.Card.sizeDelta.x)) : 1f;

        private void Update()
        {
            UpdateOrientation();
            Refresh();
            AnimateOverlay(_cleared);
            AnimateOverlay(_victory);
            UpdateToasts();
            AnimateGiftPopup();

            if (_counterPunch > 0f)
            {
                _counterPunch = Mathf.Max(0f, _counterPunch - Time.unscaledDeltaTime * 2.5f);
                float s = _hudScale * (1f + 0.08f * _counterPunch);
                _counterPanel.localScale = new Vector3(s, s, 1f);
                _grassLeftValue.color = Color.Lerp(_counterFill, _counterFlashColor, _counterPunch);
            }
        }

        private void Refresh()
        {
            if (_manager == null || _manager.Progression == null || _manager.Progression.CurrentLevelIndex < 0) return;
            var p = _manager.Progression;
            var field = p.Field;

            // Count down smoothly towards the exact value (catches up within a few frames; jumps up instantly).
            long target = field.Remaining;
            long next = _shownRemaining < 0 || target > _shownRemaining ? target
                : System.Math.Max(target, _shownRemaining - System.Math.Max(1L, (long)((_shownRemaining - target) * Mathf.Clamp01(Time.unscaledDeltaTime * 12f))));
            if (next != _shownRemaining)
            {
                _shownRemaining = next;
                SetText(_grassLeftValue, _shownRemaining.ToString("N0"));
            }
            _timerLabel.text = "TIME  " + GrassUiFactory.FormatTime(p.ElapsedSeconds);
        }

        // ------------------------------------------------------------------ build

        private void BuildCanvas()
        {
            var canvas = gameObject.GetComponent<Canvas>();
            if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = gameObject.GetComponent<UnityEngine.UI.CanvasScaler>();
            if (scaler == null) scaler = gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution;
            scaler.matchWidthOrHeight = 0.5f;
            _canvas = canvas;
            _scaler = scaler;
            if (gameObject.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
                gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            gameObject.layer = 5;
            _root = (RectTransform)transform;
            GrassUiFactory.EnsureEventSystem();
        }

        private void BuildPlayingHud()
        {
            // Clean TikTok-LIVE style: no boxes, no progress bar; just GRASS LEFT and a huge bold outlined number.
            var counter = GrassUiFactory.Rect("GrassLeft", _root);
            _counterPanel = GrassUiFactory.Place(counter, new Vector2(0.5f, 1f), new Vector2(0f, -12f), new Vector2(1100f, 240f));
            // Chunky game font (HudFontBuilder.SourceFontPath): white heading and big flat lime numbers, both with a
            // thick dark-indigo outline and a solid indigo "3D" shadow (readable on any stream). Character spacing is
            // more than twice the outline, so every letter keeps its own outline instead of fusing with the next.
            var label = HudText("GrassLeftLabel", _counterPanel, "GRASS LEFT", 60f, Ink, LabelMaterial, 14f);
            GrassUiFactory.Place(label.rectTransform, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(900f, 72f));
            _grassLeftValue = HudText("GrassLeftValue", _counterPanel, "1,000,000", 165f, CounterGreen, CounterMaterial, 12f);
            GrassUiFactory.Place(_grassLeftValue.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -64f), new Vector2(1100f, 180f));

            // Gift popup under the counter: "-25,000" (+ the gifter's name), same style. Stacks repeat gifts from the
            // same viewer while it is up; punches in, holds, fades. Only rebuilt when a gift arrives.
            _giftPopup = GrassUiFactory.Place(GrassUiFactory.Rect("GiftPopup", _root), new Vector2(0.5f, 1f), new Vector2(0f, -250f), new Vector2(1000f, 180f));
            _giftPopupGroup = _giftPopup.gameObject.AddComponent<CanvasGroup>();
            _giftPopupGroup.alpha = 0f;
            _giftPopupGroup.blocksRaycasts = false;
            _giftPopupGroup.interactable = false;
            _giftPopupAmount = HudText("GiftAmount", _giftPopup, string.Empty, 96f, CounterGreen, CounterMaterial, 10f);
            GrassUiFactory.Place(_giftPopupAmount.rectTransform, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(1000f, 110f));
            _giftPopupUser = HudText("GiftUser", _giftPopup, string.Empty, 50f, Ink, LabelMaterial, 10f);
            GrassUiFactory.Place(_giftPopupUser.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -104f), new Vector2(1000f, 64f));

            // Level / height / timer, top left, no box.
            var info = GrassUiFactory.Rect("LevelInfo", _root);
            var infoRt = GrassUiFactory.Place(info, new Vector2(0f, 1f), new Vector2(36f, -28f), new Vector2(520f, 170f));
            _levelInfo = infoRt;
            // Secondary: smaller than the counter.
            _levelLabel = GrassUiFactory.Text("LevelLabel", infoRt, "LEVEL 1 / 5", 42f, Ink, TextAlignmentOptions.Left, FontStyles.Bold);
            GrassUiFactory.Place(_levelLabel.rectTransform, new Vector2(0f, 1f), Vector2.zero, new Vector2(520f, 50f));
            Outline(_levelLabel, 0.25f);
            _heightLabel = GrassUiFactory.Text("HeightLabel", infoRt, "Grass 15-25 cm", 26f, new Color(0.91f, 1f, 0.84f), TextAlignmentOptions.Left, FontStyles.Bold);
            GrassUiFactory.Place(_heightLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, -52f), new Vector2(520f, 34f));
            Outline(_heightLabel, 0.3f);
            _timerLabel = GrassUiFactory.Text("TimerLabel", infoRt, "TIME  00:00.0", 26f, Gold, TextAlignmentOptions.Left, FontStyles.Bold);
            GrassUiFactory.Place(_timerLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, -86f), new Vector2(520f, 34f));
            Outline(_timerLabel, 0.3f);
        }

        private static void Outline(TextMeshProUGUI t, float width) => Outline(t, width, Color.black);

        private static void Outline(TextMeshProUGUI t, float width, Color color)
        {
            t.outlineWidth = width;
            t.outlineColor = color;
        }

        /// <summary>HUD text in the chunky HUD font with a shared outline+shadow material (falls back to TMP's default font).</summary>
        internal static TextMeshProUGUI HudText(string name, Transform parent, string text, float size, Color color, Material preset, float spacing)
        {
            var font = CounterFont;
            var t = GrassUiFactory.Text(name, parent, text, size, color, TextAlignmentOptions.Center, font != null ? FontStyles.Normal : FontStyles.Bold);
            t.characterSpacing = spacing;
            if (font != null && preset != null)
            {
                t.font = font;
                t.fontSharedMaterial = preset; // outline + shadow baked in, shared: no per-text material instances
            }
            else { Outline(t, 0.26f, CounterOutline); AddShadow(t, size * 0.05f); }
            return t;
        }

        private static Material s_labelMaterial, s_counterMaterial;
        internal static Material LabelMaterial =>
            s_labelMaterial != null ? s_labelMaterial : (s_labelMaterial = Resources.Load<Material>("PlayShiftFonts/HudFont SDF - Label"));
        internal static Material CounterMaterial =>
            s_counterMaterial != null ? s_counterMaterial : (s_counterMaterial = Resources.Load<Material>("PlayShiftFonts/HudFont SDF - Counter"));

        private static TMP_FontAsset s_counterFont;

        /// <summary>HUD font (SDF), built by PlayShift > Grass > Rebuild HUD Font. Null falls back to TMP's default font.</summary>
        private static TMP_FontAsset CounterFont =>
            s_counterFont != null ? s_counterFont : (s_counterFont = Resources.Load<TMP_FontAsset>("PlayShiftFonts/HudFont SDF"));

        /// <summary>Drop shadow: a dark copy of the text behind it, offset downwards.</summary>
        private static void AddShadow(TextMeshProUGUI t, float offset)
        {
            var sh = GrassUiFactory.Text(t.name + "Shadow", t.rectTransform.parent, t.text, t.fontSize, new Color(0f, 0f, 0f, 0.55f), t.alignment, t.fontStyle);
            sh.font = t.font;
            var rt = sh.rectTransform;
            rt.anchorMin = t.rectTransform.anchorMin; rt.anchorMax = t.rectTransform.anchorMax; rt.pivot = t.rectTransform.pivot;
            rt.sizeDelta = t.rectTransform.sizeDelta;
            rt.anchoredPosition = t.rectTransform.anchoredPosition + new Vector2(0f, -offset);
            sh.characterSpacing = t.characterSpacing;
            sh.outlineWidth = t.outlineWidth;
            sh.outlineColor = new Color32(0, 0, 0, 140);
            rt.SetSiblingIndex(t.rectTransform.GetSiblingIndex());
            s_shadows[t] = sh;
        }

        private static readonly System.Collections.Generic.Dictionary<TextMeshProUGUI, TextMeshProUGUI> s_shadows =
            new System.Collections.Generic.Dictionary<TextMeshProUGUI, TextMeshProUGUI>();

        private static void SetText(TextMeshProUGUI t, string value)
        {
            t.text = value;
            if (s_shadows.TryGetValue(t, out var sh) && sh != null) sh.text = value;
        }

        private void BuildGiftFeed()
        {
            _giftFeed = GrassUiFactory.Rect("GiftFeed", _root);
            GrassUiFactory.Place(_giftFeed, new Vector2(0f, 0f), new Vector2(28f, 28f), new Vector2(900f, 300f));
        }

        private Overlay BuildOverlayShell(string name, Color cardColor, Color titleColor, Vector2 cardSize)
        {
            var o = new Overlay();
            var dim = GrassUiFactory.Panel(name, _root, new Color(0f, 0f, 0f, 0.62f), rounded: false, raycast: true);
            GrassUiFactory.Stretch(dim.rectTransform);
            o.Root = dim.gameObject;

            var fxArea = GrassUiFactory.Rect("Confetti", dim.rectTransform);
            GrassUiFactory.Stretch(fxArea);
            o.Fx = fxArea.gameObject.AddComponent<CelebrationFx>();
            o.Fx.Init(fxArea, 260);

            var card = GrassUiFactory.Panel("Card", dim.rectTransform, cardColor);
            o.Card = GrassUiFactory.Place(card.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, cardSize);
            var outline = card.gameObject.AddComponent<UnityEngine.UI.Outline>();
            outline.effectColor = new Color(titleColor.r, titleColor.g, titleColor.b, 0.85f);
            outline.effectDistance = new Vector2(4f, -4f);

            o.Title = GrassUiFactory.Text("Title", o.Card, "", 104f, titleColor, TextAlignmentOptions.Center, FontStyles.Bold);
            GrassUiFactory.Place(o.Title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(cardSize.x - 40f, 120f));
            o.Title.enableVertexGradient = true;
            o.Title.colorGradient = new VertexGradient(Color.white, Color.white, titleColor, titleColor);
            o.Title.outlineWidth = 0.2f;
            o.Title.outlineColor = new Color32(40, 25, 0, 255);

            // Confetti draws above the card too.
            fxArea.SetAsLastSibling();
            return o;
        }

        private void BuildClearedOverlay()
        {
            var size = new Vector2(980f, 660f);
            _cleared = BuildOverlayShell("LevelClearedOverlay", new Color(0.06f, 0.14f, 0.06f, 0.96f), Gold, size);
            _clearedTitle = _cleared.Title;
            var card = _cleared.Card;

            _clearedSubtitle = GrassUiFactory.Text("Subtitle", card, "LEVEL 1 COMPLETE", 36f, Grass, TextAlignmentOptions.Center, FontStyles.Bold);
            GrassUiFactory.Place(_clearedSubtitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -160f), new Vector2(900f, 44f));
            _clearedSubtitle.characterSpacing = 8f;

            _clearedTime = StatBlock(card, "COMPLETION TIME", new Vector2(-220f, -230f), Gold);
            _clearedGrass = StatBlock(card, "TOTAL GRASS CUT", new Vector2(220f, -230f), Grass);

            _clearedDetail = GrassUiFactory.Text("Detail", card, "", 26f, Muted, TextAlignmentOptions.Center);
            GrassUiFactory.Place(_clearedDetail.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -400f), new Vector2(900f, 34f));

            _nextButton = GrassUiFactory.Button("NextLevelButton", card, "NEXT LEVEL  >", new Color(0.28f, 0.68f, 0.2f, 1f), out _);
            GrassUiFactory.Place((RectTransform)_nextButton.transform, new Vector2(0.5f, 0f), new Vector2(0f, 92f), new Vector2(460f, 108f));

            _clearedNextHint = GrassUiFactory.Text("NextHint", card, "", 26f, Muted, TextAlignmentOptions.Center);
            GrassUiFactory.Place(_clearedNextHint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(900f, 34f));
        }

        private TextMeshProUGUI StatBlock(RectTransform card, string caption, Vector2 pos, Color valueColor)
        {
            var bg = GrassUiFactory.Panel(caption + "Block", card, new Color(0f, 0f, 0f, 0.3f));
            var rt = GrassUiFactory.Place(bg.rectTransform, new Vector2(0.5f, 1f), pos, new Vector2(400f, 150f));
            var cap = GrassUiFactory.Text("Caption", rt, caption, 26f, Muted, TextAlignmentOptions.Center, FontStyles.Bold);
            GrassUiFactory.Place(cap.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(380f, 34f));
            cap.characterSpacing = 6f;
            var value = GrassUiFactory.Text("Value", rt, "", 64f, valueColor, TextAlignmentOptions.Center, FontStyles.Bold);
            GrassUiFactory.Place(value.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 22f), new Vector2(380f, 76f));
            return value;
        }

        private void BuildVictoryOverlay()
        {
            var size = new Vector2(1240f, 860f);
            var gold = new Color(1f, 0.78f, 0.15f, 1f);
            _victory = BuildOverlayShell("AllLevelsCompletedOverlay", new Color(0.13f, 0.09f, 0.02f, 0.97f), gold, size);
            _victoryTitle = _victory.Title;
            _victoryTitle.fontSize = 96f;
            var card = _victory.Card;

            _victorySubtitle = GrassUiFactory.Text("Subtitle", card, "", 38f, Ink, TextAlignmentOptions.Center, FontStyles.Bold);
            GrassUiFactory.Place(_victorySubtitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -164f), new Vector2(1160f, 46f));
            _victorySubtitle.characterSpacing = 8f;

            var tableBg = GrassUiFactory.Panel("ResultsTable", card, new Color(0f, 0f, 0f, 0.3f));
            var tableRt = GrassUiFactory.Place(tableBg.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -232f), new Vector2(1000f, 320f));
            _victoryTable = GrassUiFactory.Text("Rows", tableRt, "", 34f, Ink, TextAlignmentOptions.Center);
            GrassUiFactory.Stretch(_victoryTable.rectTransform, 16f);
            _victoryTable.richText = true;
            _victoryTable.lineSpacing = 12f;

            _victoryTotals = GrassUiFactory.Text("Totals", card, "", 34f, Ink, TextAlignmentOptions.Center, FontStyles.Bold);
            GrassUiFactory.Place(_victoryTotals.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -578f), new Vector2(1160f, 44f));
            _victoryTotals.richText = true;

            _playAgainButton = GrassUiFactory.Button("PlayAgainButton", card, "PLAY AGAIN", new Color(0.85f, 0.6f, 0.1f, 1f), out _);
            GrassUiFactory.Place((RectTransform)_playAgainButton.transform, new Vector2(0.5f, 0f), new Vector2(0f, 60f), new Vector2(460f, 108f));
        }

        // ------------------------------------------------------------------ helpers

        private static string DescribeGiftShare(LevelResult r)
        {
            if (r.RemovedByGifts == 0 && r.AddedByGifts == 0) return "Mowed 100% by you";
            return $"Mower {r.CutByMower:N0}   |   Gifts removed {r.RemovedByGifts:N0}   |   Gifts added {r.AddedByGifts:N0}";
        }

        private void Show(Overlay o, int burst, float rain)
        {
            HideOverlays();
            o.Root.SetActive(true);
            o.Root.transform.SetAsLastSibling();
            o.ShownAt = Time.unscaledTime;
            o.Card.localScale = Vector3.one * (0.6f * OverlayFit(o));
            o.Fx.Play(burst, rain);
        }

        private void HideOverlays()
        {
            foreach (var o in new[] { _cleared, _victory })
            {
                if (o == null) continue;
                o.Fx.Stop();
                o.Root.SetActive(false);
            }
        }

        private void AnimateOverlay(Overlay o)
        {
            if (o == null || !o.Root.activeSelf) return;
            float t = Time.unscaledTime - o.ShownAt;
            // Pop in with overshoot (ease-out-back), then a gentle title pulse.
            float p = Mathf.Clamp01(t / 0.45f);
            const float c1 = 1.70158f, c3 = c1 + 1f;
            float ease = 1f + c3 * Mathf.Pow(p - 1f, 3f) + c1 * Mathf.Pow(p - 1f, 2f);
            o.Card.localScale = Vector3.one * (Mathf.LerpUnclamped(0.6f, 1f, ease) * OverlayFit(o));
            float pulse = 1f + 0.04f * Mathf.Sin(t * 5f);
            o.Title.rectTransform.localScale = new Vector3(pulse, pulse, 1f);
        }

        private void UpdateToasts()
        {
            bool changed = false;
            float now = Time.unscaledTime;
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                var (text, until) = _toasts[i];
                float left = until - now;
                if (left <= 0f) { Destroy(text.gameObject); _toasts.RemoveAt(i); changed = true; continue; }
                text.alpha = Mathf.Clamp01(left / 0.6f);
            }
            if (changed) LayoutToasts();
        }

        private void LayoutToasts()
        {
            for (int i = 0; i < _toasts.Count; i++)
            {
                var rt = _toasts[i].text.rectTransform;
                GrassUiFactory.Place(rt, Vector2.zero, new Vector2(0f, (_toasts.Count - 1 - i) * 44f), new Vector2(900f, 40f));
            }
        }

        private static void Select(UnityEngine.UI.Button b)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && b != null) es.SetSelectedGameObject(b.gameObject);
        }
    }
}

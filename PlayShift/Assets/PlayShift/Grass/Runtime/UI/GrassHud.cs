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

        [SerializeField] private int sortingOrder = 100;
        [SerializeField] private Vector2 referenceResolution = new Vector2(1920f, 1080f);
        [SerializeField] private float giftToastSeconds = 4f;

        private GrassLevelManager _manager;
        private RectTransform _root;

        // Playing HUD
        private TextMeshProUGUI _grassLeftValue;
        private TextMeshProUGUI _levelLabel;
        private TextMeshProUGUI _heightLabel;
        private TextMeshProUGUI _timerLabel;
        private RectTransform _progressFill;
        private RectTransform _counterPanel;
        private long _shownRemaining = -1;
        private float _counterPunch;
        private Color _counterFlashColor = Ink;

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
            if (TMP_Settings.defaultFontAsset == null)
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
            _counterFlashColor = Ink;
            Refresh();
        }

        public void ShowLevelCleared(LevelResult result, GrassLevelDefinition next)
        {
            Refresh(); // counter shows the final 0, then stops updating while the overlay is up
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
            ShowToast(gift.Describe(), c);
            _counterFlashColor = c;
            _counterPunch = 1f;
            if (gift.Action == GiftActionType.IncreaseHeight && _manager != null && _manager.Field != null)
                _heightLabel.text = "Grass " + GrassUiFactory.FormatHeightRange(_manager.Field.CurrentMinHeightCm, _manager.Field.CurrentMaxHeightCm);
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

        private void Update()
        {
            Refresh();
            AnimateOverlay(_cleared);
            AnimateOverlay(_victory);
            UpdateToasts();

            if (_counterPunch > 0f)
            {
                _counterPunch = Mathf.Max(0f, _counterPunch - Time.unscaledDeltaTime * 2.5f);
                float s = 1f + 0.08f * _counterPunch;
                _counterPanel.localScale = new Vector3(s, s, 1f);
                _grassLeftValue.color = Color.Lerp(Ink, _counterFlashColor, _counterPunch);
            }
        }

        private void Refresh()
        {
            if (_manager == null || _manager.Progression == null || _manager.Progression.CurrentLevelIndex < 0) return;
            var p = _manager.Progression;
            var field = p.Field;

            if (field.Remaining != _shownRemaining)
            {
                _shownRemaining = field.Remaining;
                _grassLeftValue.text = _shownRemaining.ToString("N0");
                long scale = System.Math.Max(field.InitialUnits, field.InitialUnits + field.AddedByGifts);
                float frac = scale > 0 ? Mathf.Clamp01((float)((double)_shownRemaining / scale)) : 0f;
                _progressFill.anchorMax = new Vector2(frac, 1f);
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
            if (gameObject.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
                gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            gameObject.layer = 5;
            _root = (RectTransform)transform;
            GrassUiFactory.EnsureEventSystem();
        }

        private void BuildPlayingHud()
        {
            // Clean TikTok-LIVE style: no boxes, a huge bold outlined number, a thin progress bar.
            var counter = GrassUiFactory.Rect("GrassLeft", _root);
            _counterPanel = GrassUiFactory.Place(counter, new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(900f, 230f));
            var label = GrassUiFactory.Text("GrassLeftLabel", _counterPanel, "GRASS LEFT", 36f, new Color(0.91f, 1f, 0.84f), TextAlignmentOptions.Center, FontStyles.Bold);
            GrassUiFactory.Place(label.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -4f), new Vector2(880f, 44f));
            label.characterSpacing = 14f;
            Outline(label, 0.28f);
            _grassLeftValue = GrassUiFactory.Text("GrassLeftValue", _counterPanel, "1,000,000", 136f, Ink, TextAlignmentOptions.Center, FontStyles.Bold);
            GrassUiFactory.Place(_grassLeftValue.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(900f, 150f));
            Outline(_grassLeftValue, 0.24f);

            var border = GrassUiFactory.Panel("ProgressBorder", _counterPanel, new Color(1f, 1f, 1f, 0.9f));
            GrassUiFactory.Place(border.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(626f, 22f));
            var track = GrassUiFactory.Panel("ProgressTrack", border.rectTransform, new Color(0f, 0f, 0f, 0.55f));
            GrassUiFactory.Stretch(track.rectTransform, 3f);
            var fill = GrassUiFactory.Panel("ProgressFill", track.rectTransform, Grass);
            _progressFill = fill.rectTransform;
            _progressFill.anchorMin = Vector2.zero;
            _progressFill.anchorMax = Vector2.one;
            _progressFill.pivot = new Vector2(0f, 0.5f);
            _progressFill.offsetMin = Vector2.zero;
            _progressFill.offsetMax = Vector2.zero;

            // Level / height / timer, top left, no box.
            var info = GrassUiFactory.Rect("LevelInfo", _root);
            var infoRt = GrassUiFactory.Place(info, new Vector2(0f, 1f), new Vector2(36f, -28f), new Vector2(520f, 170f));
            _levelLabel = GrassUiFactory.Text("LevelLabel", infoRt, "LEVEL 1 / 5", 58f, Ink, TextAlignmentOptions.Left, FontStyles.Bold);
            GrassUiFactory.Place(_levelLabel.rectTransform, new Vector2(0f, 1f), Vector2.zero, new Vector2(520f, 66f));
            Outline(_levelLabel, 0.25f);
            _heightLabel = GrassUiFactory.Text("HeightLabel", infoRt, "Grass 15-25 cm", 32f, new Color(0.91f, 1f, 0.84f), TextAlignmentOptions.Left, FontStyles.Bold);
            GrassUiFactory.Place(_heightLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, -68f), new Vector2(520f, 40f));
            Outline(_heightLabel, 0.3f);
            _timerLabel = GrassUiFactory.Text("TimerLabel", infoRt, "TIME  00:00.0", 32f, Gold, TextAlignmentOptions.Left, FontStyles.Bold);
            GrassUiFactory.Place(_timerLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, -108f), new Vector2(520f, 40f));
            Outline(_timerLabel, 0.3f);
        }

        private static void Outline(TextMeshProUGUI t, float width)
        {
            t.outlineWidth = width;
            t.outlineColor = new Color32(10, 30, 5, 245);
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
            o.Card.localScale = Vector3.one * 0.6f;
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

        private static void AnimateOverlay(Overlay o)
        {
            if (o == null || !o.Root.activeSelf) return;
            float t = Time.unscaledTime - o.ShownAt;
            // Pop in with overshoot (ease-out-back), then a gentle title pulse.
            float p = Mathf.Clamp01(t / 0.45f);
            const float c1 = 1.70158f, c3 = c1 + 1f;
            float ease = 1f + c3 * Mathf.Pow(p - 1f, 3f) + c1 * Mathf.Pow(p - 1f, 2f);
            o.Card.localScale = Vector3.one * Mathf.LerpUnclamped(0.6f, 1f, ease);
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

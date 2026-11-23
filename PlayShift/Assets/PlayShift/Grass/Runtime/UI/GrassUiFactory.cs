using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace PlayShift.Grass.UI
{
    /// <summary>Small helpers that build the HUD/overlay hierarchy in code, so the module needs no prefabs.</summary>
    internal static class GrassUiFactory
    {
        private static Sprite s_rounded;

        /// <summary>Procedural 9-sliced rounded rectangle (built-in UI sprites are editor-only).</summary>
        public static Sprite RoundedSprite
        {
            get
            {
                if (s_rounded != null) return s_rounded;
                const int size = 64, radius = 20;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "PlayShiftRounded", wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                        float cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                        byte a = (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255f);
                        px[y * size + x] = new Color32(255, 255, 255, a);
                    }
                tex.SetPixels32(px);
                tex.Apply(false, true);
                s_rounded = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                    SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
                s_rounded.name = "PlayShiftRounded";
                return s_rounded;
            }
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5; // UI
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        /// <summary>Anchors and pivots at the same normalized point, then positions and sizes.</summary>
        public static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 anchoredPos, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.sizeDelta = size;
            rt.anchoredPosition = anchoredPos;
            return rt;
        }

        public static RectTransform Stretch(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
            return rt;
        }

        public static UnityEngine.UI.Image Panel(string name, Transform parent, Color color, bool rounded = true, bool raycast = false)
        {
            var rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<UnityEngine.UI.Image>();
            if (rounded)
            {
                img.sprite = RoundedSprite;
                img.type = UnityEngine.UI.Image.Type.Sliced;
            }
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        public static TextMeshProUGUI Text(string name, Transform parent, string text, float size, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.Center, FontStyles style = FontStyles.Normal)
        {
            var rt = Rect(name, parent);
            var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.fontStyle = style;
            tmp.raycastTarget = false;
#if UNITY_6000_0_OR_NEWER
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
#else
            tmp.enableWordWrapping = false;
#endif
            tmp.overflowMode = TextOverflowModes.Overflow;
            return tmp;
        }

        public static UnityEngine.UI.Button Button(string name, Transform parent, string label, Color color, out TextMeshProUGUI labelText)
        {
            var img = Panel(name, parent, color, rounded: true, raycast: true);
            var button = img.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = img;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
            colors.selectedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            labelText = Text("Label", img.transform, label, 46f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
            Stretch(labelText.rectTransform);
            return button;
        }

        public static void EnsureEventSystem()
        {
#if UNITY_2023_1_OR_NEWER
            if (Object.FindFirstObjectByType<EventSystem>() != null) return;
#else
            if (Object.FindObjectOfType<EventSystem>() != null) return;
#endif
            var go = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
            go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }

        public static string FormatTime(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int totalTenths = (int)System.Math.Floor(seconds * 10.0);
            int tenths = totalTenths % 10;
            int totalSeconds = totalTenths / 10;
            int s = totalSeconds % 60;
            int m = (totalSeconds / 60) % 60;
            int h = totalSeconds / 3600;
            return h > 0 ? $"{h}:{m:00}:{s:00}.{tenths}" : $"{m:00}:{s:00}.{tenths}";
        }

        public static string FormatHeightRange(float minCm, float maxCm) => $"{minCm:0.#}-{maxCm:0.#} cm";
    }
}

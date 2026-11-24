using UnityEngine;
using UnityEngine.EventSystems;

namespace PlayShift.Grass
{
    /// <summary>
    /// Title-screen button feel: grows a little on hover, presses in on click, eased (unscaled time, works while the
    /// game is paused). The drop shadow follows. No layout changes, no allocations.
    /// </summary>
    public sealed class MenuButtonMotion : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        public RectTransform Shadow;
        [SerializeField] private float hoverScale = 1.06f;
        [SerializeField] private float pressScale = 0.94f;
        [SerializeField] private float speed = 14f;
        private bool _hover, _down;
        private float _scale = 1f;

        public void OnPointerEnter(PointerEventData e) => _hover = true;
        public void OnPointerExit(PointerEventData e) { _hover = false; _down = false; }
        public void OnPointerDown(PointerEventData e) => _down = true;
        public void OnPointerUp(PointerEventData e) => _down = false;
        private void OnDisable() { _hover = _down = false; _scale = 1f; Apply(); }

        private void Update()
        {
            float target = _down ? pressScale : _hover ? hoverScale : 1f;
            if (Mathf.Abs(_scale - target) < 0.0005f) return;
            _scale = Mathf.Lerp(_scale, target, 1f - Mathf.Exp(-speed * Time.unscaledDeltaTime));
            Apply();
        }

        private void Apply()
        {
            transform.localScale = new Vector3(_scale, _scale, 1f);
            if (Shadow != null) Shadow.localScale = transform.localScale;
        }
    }
}

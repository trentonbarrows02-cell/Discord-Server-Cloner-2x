using UnityEngine;

namespace PlayShift.Grass.UI
{
    /// <summary>Lightweight UI confetti: a fixed pool of small Images animated with unscaled time.</summary>
    [DisallowMultipleComponent]
    internal sealed class CelebrationFx : MonoBehaviour
    {
        private static readonly Color[] Palette =
        {
            new Color(1f, 0.84f, 0.2f), new Color(0.35f, 0.9f, 0.35f), new Color(0.3f, 0.7f, 1f),
            new Color(1f, 0.4f, 0.55f), new Color(1f, 1f, 1f), new Color(0.75f, 0.45f, 1f),
        };

        private RectTransform[] _pieces;
        private Vector2[] _velocity;
        private float[] _spin;
        private float[] _life;
        private RectTransform _area;
        private float _emitRate;
        private float _emitAccumulator;
        private int _next;

        public void Init(RectTransform area, int poolSize)
        {
            _area = area;
            _pieces = new RectTransform[poolSize];
            _velocity = new Vector2[poolSize];
            _spin = new float[poolSize];
            _life = new float[poolSize];
            for (int i = 0; i < poolSize; i++)
            {
                var img = GrassUiFactory.Panel("Confetti", area, Color.white, rounded: false);
                var rt = img.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.gameObject.SetActive(false);
                _pieces[i] = rt;
            }
        }

        /// <summary>One burst, plus optional continuous rain (pieces per second) until Stop.</summary>
        public void Play(int burst, float continuousPerSecond)
        {
            _emitRate = continuousPerSecond;
            for (int i = 0; i < burst; i++) Emit(fromTop: false);
        }

        public void Stop()
        {
            _emitRate = 0f;
            if (_pieces == null) return;
            foreach (var p in _pieces) p.gameObject.SetActive(false);
        }

        private void Emit(bool fromTop)
        {
            if (_pieces == null || _pieces.Length == 0) return;
            int i = _next;
            _next = (_next + 1) % _pieces.Length;
            var rt = _pieces[i];
            Vector2 half = _area.rect.size * 0.5f;

            if (fromTop)
            {
                rt.anchoredPosition = new Vector2(Random.Range(-half.x, half.x), half.y + 20f);
                _velocity[i] = new Vector2(Random.Range(-60f, 60f), Random.Range(-260f, -120f));
            }
            else
            {
                rt.anchoredPosition = new Vector2(Random.Range(-half.x * 0.3f, half.x * 0.3f), -half.y * 0.15f);
                float angle = Random.Range(35f, 145f) * Mathf.Deg2Rad;
                float speed = Random.Range(700f, 1500f);
                _velocity[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * speed;
            }

            rt.sizeDelta = new Vector2(Random.Range(10f, 20f), Random.Range(18f, 34f));
            rt.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            rt.GetComponent<UnityEngine.UI.Image>().color = Palette[Random.Range(0, Palette.Length)];
            _spin[i] = Random.Range(-540f, 540f);
            _life[i] = Random.Range(3.5f, 5.5f);
            rt.gameObject.SetActive(true);
        }

        private void Update()
        {
            if (_pieces == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);

            if (_emitRate > 0f)
            {
                _emitAccumulator += _emitRate * dt;
                while (_emitAccumulator >= 1f) { _emitAccumulator -= 1f; Emit(fromTop: true); }
            }

            float floor = -_area.rect.height * 0.5f - 60f;
            for (int i = 0; i < _pieces.Length; i++)
            {
                var rt = _pieces[i];
                if (!rt.gameObject.activeSelf) continue;
                _life[i] -= dt;
                Vector2 v = _velocity[i];
                v.y -= 900f * dt;                 // gravity
                v *= 1f - 1.6f * dt;              // air drag
                v.x += Mathf.Sin((Time.unscaledTime + i) * 4f) * 40f * dt; // flutter
                _velocity[i] = v;
                rt.anchoredPosition += v * dt;
                rt.localRotation *= Quaternion.Euler(_spin[i] * dt * 0.4f, 0f, _spin[i] * dt);
                if (_life[i] <= 0f || rt.anchoredPosition.y < floor) rt.gameObject.SetActive(false);
            }
        }
    }
}

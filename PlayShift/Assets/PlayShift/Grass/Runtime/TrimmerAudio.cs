using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Trimmer sound. By default everything is synthesised in code (original sound, no third-party assets):
    /// a two-stroke motor whose pitch follows blade speed (so start-up and shutdown are natural ramps) and
    /// which bogs down and gets raspier under load, plus a rustling cut noise that is louder and lower for
    /// thick grass. Any of the optional clips can replace a synthesised layer with your own licensed audio.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class TrimmerAudio : MonoBehaviour
    {
        [SerializeField] private TrimmerBlades blades;
        [SerializeField] private GrassCutter cutter;
        [Range(0f, 1f)] [SerializeField] private float masterVolume = 0.55f;

        [Header("Synth")]
        [SerializeField] private bool synthesizeMotor = true;
        [SerializeField] private bool synthesizeCutting = true;
        [SerializeField] private float idleHz = 38f;
        [SerializeField] private float fullHz = 118f;
        [Tooltip("Pitch drop at full load (thick grass).")]
        [Range(0f, 0.4f)] [SerializeField] private float strainPitchDrop = 0.14f;

        [Header("Optional licensed clips (replace the synth)")]
        [SerializeField] private AudioClip startupClip;
        [SerializeField] private AudioClip motorLoopClip;
        [SerializeField] private AudioClip shutdownClip;
        [SerializeField] private AudioClip cutLightLoopClip;
        [SerializeField] private AudioClip cutThickLoopClip;
        [SerializeField] private AudioClip[] clippingOneShots;

        private AudioSource _out;
        private AudioSource _motorLoop, _cutLight, _cutThick, _oneShots;
        private GameObject _clipHost;
        private bool _wasOn;
        private float _clipCooldown;

        // Audio-thread state (written on the main thread, read in OnAudioFilterRead).
        private volatile float _rpm, _load, _cutRate;
        // UnityEngine.Object comparisons (clip == null) are main-thread only in Unity 6, so resolve these in Update.
        private volatile bool _synthMotorActive, _synthCutActive;
        private double _phase;
        private float _noiseLp, _noiseBp, _cutEnv, _motorEnv;
        private uint _rng = 22222u;
        private int _sampleRate;

        private void Awake()
        {
            if (blades == null) blades = GetComponentInParent<TrimmerBlades>();
            if (cutter == null) cutter = GetComponentInParent<GrassCutter>();
            _sampleRate = AudioSettings.outputSampleRate;
            UpdateSynthFlags();
            _out = GetComponent<AudioSource>();
            _out.playOnAwake = true;
            _out.loop = true;
            _out.spatialBlend = 0f;
            _out.volume = 1f;
            // The synth writes into this source's filter chain; a silent looping clip keeps it running.
            if (_out.clip == null) _out.clip = AudioClip.Create("TrimmerSynth", _sampleRate, 1, _sampleRate, false);
            _out.Play();

            // Extra sources live on a child: a GameObject with OnAudioFilterRead may only have one AudioSource.
            _clipHost = new GameObject("TrimmerAudioClips");
            _clipHost.transform.SetParent(transform, false);
            _motorLoop = MakeLoop(motorLoopClip);
            _cutLight = MakeLoop(cutLightLoopClip);
            _cutThick = MakeLoop(cutThickLoopClip);
            _oneShots = _clipHost.AddComponent<AudioSource>();
            _oneShots.spatialBlend = 0f;
            _oneShots.playOnAwake = false;
            if (cutter != null) cutter.GrassCut += OnCut;
        }

        private void OnDestroy()
        {
            if (cutter != null) cutter.GrassCut -= OnCut;
        }

        private AudioSource MakeLoop(AudioClip clip)
        {
            if (clip == null) return null;
            var s = _clipHost.AddComponent<AudioSource>();
            s.clip = clip; s.loop = true; s.playOnAwake = false; s.volume = 0f; s.Play();
            return s;
        }

        private float _cutAccum;
        private void OnCut(Vector3 pos, Vector3 vel, long units, float heightM, float radius) => _cutAccum += units * (0.5f + heightM);

        private void Update()
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            float rpm = blades != null ? blades.Rpm01 : 0f;
            bool on = blades != null && blades.On;
            float load = cutter != null ? cutter.Load : 0f;
            float rate = Mathf.Clamp01(_cutAccum / dt / 60000f);
            _cutAccum = 0f;

            _rpm = rpm; _load = load; _cutRate = Mathf.Lerp(_cutRate, rate, 1f - Mathf.Exp(-12f * dt));
            UpdateSynthFlags();

            if (on != _wasOn)
            {
                var clip = on ? startupClip : shutdownClip;
                if (clip != null) _oneShots.PlayOneShot(clip, masterVolume);
                _wasOn = on;
            }
            if (_motorLoop != null)
            {
                _motorLoop.volume = masterVolume * Mathf.Clamp01(rpm * 1.2f);
                _motorLoop.pitch = Mathf.Lerp(0.6f, 1f, rpm) * (1f - strainPitchDrop * load);
            }
            if (_cutLight != null) _cutLight.volume = masterVolume * _cutRate * (1f - load);
            if (_cutThick != null) _cutThick.volume = masterVolume * _cutRate * load;

            _clipCooldown -= dt;
            if (clippingOneShots != null && clippingOneShots.Length > 0 && _cutRate > 0.15f && _clipCooldown <= 0f)
            {
                _oneShots.PlayOneShot(clippingOneShots[Random.Range(0, clippingOneShots.Length)], masterVolume * 0.5f * _cutRate);
                _clipCooldown = Random.Range(0.08f, 0.2f);
            }
        }

        private void UpdateSynthFlags()
        {
            _synthMotorActive = synthesizeMotor && motorLoopClip == null;
            _synthCutActive = synthesizeCutting && cutLightLoopClip == null && cutThickLoopClip == null;
        }

        private float Noise()
        {
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            return (_rng / 4294967295f) * 2f - 1f;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            bool motor = _synthMotorActive;
            bool cutting = _synthCutActive;
            if (!motor && !cutting) return;

            float rpm = _rpm, load = _load, cutRate = _cutRate;
            float hz = Mathf.Lerp(idleHz, fullHz, rpm) * (1f - strainPitchDrop * load);
            double inc = hz / _sampleRate;
            float motorTarget = motor ? Mathf.Clamp01(rpm * 1.4f) : 0f;
            float cutTarget = cutting ? cutRate : 0f;
            float bpCoef = Mathf.Lerp(0.35f, 0.18f, load); // thick grass = lower, heavier rustle

            for (int i = 0; i < data.Length; i += channels)
            {
                _motorEnv += (motorTarget - _motorEnv) * 0.0005f;
                _cutEnv += (cutTarget - _cutEnv) * 0.002f;
                _phase += inc;
                if (_phase >= 1.0) _phase -= 1.0;
                float ph = (float)_phase;

                // Two-stroke buzz: pulse train + harmonics, raspier under load.
                float pulse = ph < 0.18f ? 1f : -0.22f;
                float saw = 2f * ph - 1f;
                float n = Noise();
                float motorSig = (0.55f * pulse + 0.3f * saw + 0.15f * Mathf.Sin(ph * Mathf.PI * 6f)) * (0.9f + 0.1f * n * (1f + 2f * load));

                // Cutting: band-passed noise bursts.
                _noiseLp += (n - _noiseLp) * bpCoef;
                _noiseBp += (_noiseLp - _noiseBp) * 0.05f;
                float rustle = (_noiseLp - _noiseBp) * (0.7f + 0.3f * Mathf.Sin(ph * Mathf.PI * 2f));

                float s = (motorSig * 0.22f * _motorEnv + rustle * 0.9f * _cutEnv) * masterVolume;
                for (int c = 0; c < channels; c++) data[i + c] = s;
            }
        }
    }
}

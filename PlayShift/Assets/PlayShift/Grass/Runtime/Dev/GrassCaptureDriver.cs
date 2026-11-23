#if UNITY_EDITOR
// Editor-only development tool (excluded from builds). Lives outside an Editor folder because Unity can't add
// MonoBehaviours from editor assemblies to scene objects.
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Media;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PlayShift.Grass.Dev
{
    /// <summary>
    /// Play-mode driver added by <see cref="PlayShift.Grass.Editor.GrassCaptureTool"/>. Drives the first-person player
    /// through <see cref="FirstPersonController.InputOverride"/>: uncut grass, trimmer spinning, GPU frame time per
    /// quality preset, a ~5 s walk-and-cut clip, then turns around to show the cut path. Exits Play mode when done.
    ///
    /// Frames are rendered by calling Camera.Render() into a fixed 1920x1080 target (HUD switched to camera space),
    /// so capture works even when the editor's Game view is hidden or not focused.
    /// </summary>
    [DefaultExecutionOrder(10000)] // after GrassFieldRenderer.LateUpdate has queued this frame's grass
    public sealed class GrassCaptureDriver : MonoBehaviour
    {
        private const int Width = 1920, Height = 1080;
        private const int ClipFps = 30;
        private const float ClipSeconds = 5f;
        private const int ClipWidth = 1920, ClipHeight = 1080; // full res: downscaling thin grass blades makes the clip look soft

        private FirstPersonController.FpInput _input;
        private string _dir;
        private readonly StringBuilder _report = new StringBuilder();
        private int _errors;

        private Camera _cam;
        private TMPro.TMP_Text[] _texts = new TMPro.TMP_Text[0];
        private RenderTexture _rt;
        private Texture2D _frame, _clipFrame, _probe;
        private bool _grabRequested, _grabDone, _measureRequested;
        private float _probeMs;

        private void OnEnable() => Application.logMessageReceived += OnLog;
        private void OnDisable() => Application.logMessageReceived -= OnLog;

        private void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            _errors++;
            if (_errors <= 20) _report.AppendLine("LOG " + type + ": " + msg);
        }

        private static void Step(string what) => Debug.Log("[PlayShift Capture] " + what);

        private IEnumerator Start()
        {
            _dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Captures", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(_dir);
            Application.runInBackground = true;

            var fp = FindAnyObjectByType<FirstPersonController>();
            var cutter = FindAnyObjectByType<GrassCutter>();
            var fieldRenderer = FindAnyObjectByType<GrassFieldRenderer>();
            var manager = GrassLevelManager.Instance;
            if (fp == null || fp.Camera == null || manager == null || manager.Field == null)
            {
                Debug.LogError("[PlayShift Capture] Player, camera or GrassLevelManager missing; open GrassTestArea.unity.");
                Finish();
                yield break;
            }
            fp.InputOverride = () => _input;
            var field = manager.Field;
            SetUpOffscreen(fp.Camera);
            Step("started, output " + _dir);

            _report.AppendLine("Unity " + Application.unityVersion + ", GPU " + SystemInfo.graphicsDeviceName +
                               " (" + SystemInfo.graphicsDeviceType + "), capture " + Width + "x" + Height);
            _report.AppendLine("Level " + (manager.Progression.CurrentLevelIndex + 1) + ", initial units " + field.InitialUnits.ToString("N0"));

            yield return GameWait(1.5f); // settle: HUD build, shader warm-up, player lands on the ground
            _report.AppendLine("Player position after settling: " + fp.transform.position.ToString("F2"));
            yield return Shot("01_uncut_trimmer_off");

            _input.TrimmerHeld = true;
            yield return GameWait(1.0f);
            yield return Shot("02_trimmer_spinning");

            if (fieldRenderer != null)
            {
                foreach (var q in new[] { GrassQuality.Low, GrassQuality.Medium, GrassQuality.High, GrassQuality.Ultra })
                {
                    fieldRenderer.Quality = q;
                    yield return Frames(5);
                    _measureRequested = true;
                    while (_measureRequested) yield return null;
                    _report.AppendLine($"Quality {q}: {_probeMs:F2} ms per 1080p frame (~{1000f / _probeMs:F0} FPS render-bound), " +
                                       $"{fieldRenderer.LastDrawnBlades:N0} blades drawn");
                }
                fieldRenderer.Quality = GrassQuality.High;
                Step("frame times measured");
            }

            long before = field.Remaining;
            _report.AppendLine("Remaining before walk: " + before.ToString("N0"));

            // Clip: walk forward through the grass with a gentle side-to-side look, fixed 30 fps game time.
            Time.captureFramerate = ClipFps;
            var attrs = new VideoTrackAttributes
            {
                frameRate = new MediaRational(ClipFps),
                width = ClipWidth,
                height = ClipHeight,
                includeAlpha = false,
                bitRateMode = VideoBitrateMode.High
            };
            using (var encoder = new MediaEncoder(Path.Combine(_dir, "clip_walk_and_cut.mp4"), attrs))
            {
                int total = Mathf.RoundToInt(ClipSeconds * ClipFps);
                for (int f = 0; f < total; f++)
                {
                    float t = f / (float)ClipFps;
                    _input.Move = new Vector2(0f, 0.55f);
                    _input.Look = new Vector2(Mathf.Cos(t * Mathf.PI) * 14f * Mathf.PI / ClipFps, 0f);
                    yield return Grab();
                    if (f == total / 2) File.WriteAllBytes(Path.Combine(_dir, "03_cutting.jpg"), _frame.EncodeToJPG(92));
                    encoder.AddFrame(ToClipFrame());
                }
            }
            Time.captureFramerate = 0;
            _input.Look = Vector2.zero;
            _input.Move = Vector2.zero;
            Step("clip written");

            yield return GameWait(0.5f);
            long after = field.Remaining;
            _report.AppendLine("Remaining after walk: " + after.ToString("N0") + " (cut " + (before - after).ToString("N0") +
                               ", cutter counted " + (cutter != null ? cutter.UnitsCutThisLevel.ToString("N0") : "?") + ")");

            // Turn around (trimmer off) to show the cut path and the fresh-cut lawn.
            _input.TrimmerHeld = false;
            float turned = 0f;
            while (turned < 180f)
            {
                float step = Mathf.Min(180f - turned, 150f * Time.deltaTime);
                turned += step;
                _input.Look = new Vector2(step, 0f);
                yield return null;
            }
            _input.Look = Vector2.zero;
            yield return GameWait(1.2f); // let the floating trimmer settle after the turn
            yield return Shot("04_cut_path_behind");

            _input.Look = new Vector2(0f, -12f); // one frame: look further down at the stubble
            yield return null;
            _input.Look = Vector2.zero;
            yield return GameWait(0.8f);
            yield return Shot("05_fresh_cut_lawn_closeup");

            _report.AppendLine("Errors/exceptions logged during capture: " + _errors);
            Finish();
        }

        private void SetUpOffscreen(Camera cam)
        {
            _cam = cam;
            _rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4, name = "CaptureRT" };
            _cam.targetTexture = _rt;
            _frame = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            _clipFrame = new Texture2D(ClipWidth, ClipHeight, TextureFormat.RGBA32, false);
            _probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            // Overlay canvases are not drawn into camera targets; switch the HUD to camera space for the capture.
            foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (!canvas.gameObject.scene.IsValid() || !canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = _cam;
                canvas.planeDistance = _cam.nearClipPlane + 0.02f;
                _texts = canvas.GetComponentsInChildren<TMPro.TMP_Text>(true);
            }
        }

        private void LateUpdate()
        {
            if (_cam == null) return;
            if (_grabRequested)
            {
                RenderNow();
                var prev = RenderTexture.active;
                RenderTexture.active = _rt;
                _frame.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                _frame.Apply();
                RenderTexture.active = prev;
                _grabRequested = false;
                _grabDone = true;
            }
            if (_measureRequested)
            {
                const int n = 30;
                RenderNow(); Sync();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++) { RenderNow(); Sync(); }
                _probeMs = (float)sw.Elapsed.TotalMilliseconds / n;
                _measureRequested = false;
            }
        }

        private void RenderNow()
        {
            // TMP text meshes are not rebuilt for camera-space canvases on manual renders unless forced.
            foreach (var t in _texts) if (t != null && t.isActiveAndEnabled) t.ForceMeshUpdate();
            Canvas.ForceUpdateCanvases();
            _cam.Render();
        }

        /// <summary>1-pixel readback: waits for the GPU to finish the frame so the timing is real.</summary>
        private void Sync()
        {
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            _probe.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            _probe.Apply();
            RenderTexture.active = prev;
        }

        private IEnumerator Grab()
        {
            _grabDone = false;
            _grabRequested = true;
            while (!_grabDone) yield return null;
        }

        private Texture2D ToClipFrame()
        {
            var small = RenderTexture.GetTemporary(ClipWidth, ClipHeight, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(_rt, small);
            var prev = RenderTexture.active;
            RenderTexture.active = small;
            _clipFrame.ReadPixels(new Rect(0, 0, ClipWidth, ClipHeight), 0, 0);
            _clipFrame.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(small);
            return _clipFrame;
        }

        private IEnumerator Shot(string name)
        {
            yield return Grab();
            File.WriteAllBytes(Path.Combine(_dir, name + ".jpg"), _frame.EncodeToJPG(92));
            Step("saved " + name);
        }

        /// <summary>Waits in game time (frames are uncapped when the Game view is hidden, so frame counts are meaningless).</summary>
        private static IEnumerator GameWait(float seconds)
        {
            float end = Time.time + seconds;
            while (Time.time < end) yield return null;
        }

        private static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        private void Finish()
        {
            if (_dir != null) File.WriteAllText(Path.Combine(_dir, "report.txt"), _report.ToString());
            if (_cam != null) _cam.targetTexture = null;
            Step("CAPTURE DONE " + _dir);
            if (Application.isBatchMode) EditorApplication.Exit(0);
            else EditorApplication.isPlaying = false;
        }
    }
}
#endif

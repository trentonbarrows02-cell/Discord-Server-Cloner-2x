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
        // Portrait by default (vertical TikTok streams); -captureSize 1920x1080 for landscape.
        private static int Width = 1080, Height = 1920;
        private const int ClipFps = 30;
        private const float ClipSeconds = 5f;
        private static int ClipWidth => Width;   // full res: downscaling thin grass blades makes the clip look soft
        private static int ClipHeight => Height;

        private FirstPersonController.FpInput _input;
        private string _dir;
        private readonly StringBuilder _report = new StringBuilder();
        private int _errors;

        private Camera _cam, _uiCam;
        private TMPro.TMP_Text[] _texts = new TMPro.TMP_Text[0];
        private RenderTexture _rt, _out; // _rt: MSAA game render; _out: resolved copy with the HUD drawn on top
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
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-captureSize")
                {
                    var wh = args[i + 1].Split('x');
                    Width = int.Parse(wh[0]) & ~1; Height = int.Parse(wh[1]) & ~1;
                }

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
            if (MainMenu.IsOpen && MainMenu.Instance != null)
            {
                yield return GameWait(1.0f); // let the background pan a little
                yield return Shot("00_main_menu");
                _report.AppendLine($"Title screen: level timer held at {manager.Progression.ElapsedSeconds:F2} s (should be 0)");
                MainMenu.Instance.Play();
                yield return GameWait(0.6f);
            }
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
                                       $"{fieldRenderer.LastDrawnBlades:N0} blades drawn in {fieldRenderer.LastDrawCalls} draws, " +
                                       $"grass CPU {fieldRenderer.LastCpuMs:F3} ms, field total {fieldRenderer.TotalBlades:N0} blades");
                }
                fieldRenderer.Quality = GrassQuality.High;
                _report.AppendLine($"Grass GPU memory: {fieldRenderer.GpuBytes / 1048576.0:F2} MB (cells only; blades are procedural)");
                Step("frame times measured");
            }

            long before = field.Remaining;
            _report.AppendLine("Remaining before walk: " + before.ToString("N0"));

            // Clip: walk forward through the grass with a gentle side-to-side look, fixed 30 fps game time.
            int maxTiles = 0;
            float maxGrassCpu = 0f, sumGrassCpu = 0f;
            Time.captureFramerate = ClipFps;
            var attrs = new VideoTrackAttributes
            {
                frameRate = new MediaRational(ClipFps),
                width = (uint)ClipWidth,
                height = (uint)ClipHeight,
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
                    if (fieldRenderer != null)
                    {
                        maxTiles = Mathf.Max(maxTiles, fieldRenderer.LastTilesUploaded);
                        maxGrassCpu = Mathf.Max(maxGrassCpu, fieldRenderer.LastCpuMs);
                        sumGrassCpu += fieldRenderer.LastCpuMs;
                    }
                    if (f == total / 2) File.WriteAllBytes(Path.Combine(_dir, "03_cutting.jpg"), _frame.EncodeToJPG(92));
                    encoder.AddFrame(ToClipFrame());
                }
                _report.AppendLine($"While cutting: max {maxTiles} tiles uploaded in a frame ({maxTiles * 8} KB), " +
                                   $"grass CPU avg {sumGrassCpu / total:F3} ms, max {maxGrassCpu:F3} ms");
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

            // Real gift path (receiver -> router -> HUD): two "Ice Cream Cone" (remove 15k) from the same viewer stack
            // into one "-30,000 / DEC282" popup under the counter.
            var receiver = FindAnyObjectByType<TikTokGiftReceiver>();
            if (receiver != null)
            {
                _input.Look = new Vector2(0f, 8f); // look up a little so the popup is over the sky/meadow
                yield return null;
                _input.Look = Vector2.zero;
                receiver.EnqueueGift("DEC282", "Ice Cream Cone");
                yield return GameWait(0.5f);
                receiver.EnqueueGift("DEC282", "Ice Cream Cone");
                yield return GameWait(0.4f);
                yield return Shot("06_gift_popup");
            }

            if (GameSettingsMenu.Instance != null)
            {
                GameSettingsMenu.Instance.SetOpen(true);
                yield return GameWait(0.3f);
                yield return Shot("08_settings");
                GameSettingsMenu.Instance.SetOpen(false);
            }

            // Gift burst stress test (TikTok spam): 30 frames, every frame a remove-25k, add-25k and taller gift.
            if (fieldRenderer != null)
            {
                int burstMaxTiles = 0, burstFrames = 30;
                float burstMaxCpu = 0f, giftMaxMs = 0f, giftSumMs = 0f;
                for (int i = 0; i < burstFrames; i++)
                {
                    long g0 = Stopwatch.GetTimestamp();
                    manager.RemoveGrass(25_000);
                    manager.AddGrass(25_000);
                    manager.IncreaseHeight(0.5f);
                    float giftMs = (float)((Stopwatch.GetTimestamp() - g0) * 1000.0 / Stopwatch.Frequency);
                    giftMaxMs = Mathf.Max(giftMaxMs, giftMs);
                    giftSumMs += giftMs;
                    yield return null;
                    burstMaxTiles = Mathf.Max(burstMaxTiles, fieldRenderer.LastTilesUploaded);
                    burstMaxCpu = Mathf.Max(burstMaxCpu, fieldRenderer.LastCpuMs);
                }
                int backlog = field.DirtyTileCount;
                _report.AppendLine($"Gift burst ({burstFrames} frames of remove 25k + add 25k + taller): gift logic avg {giftSumMs / burstFrames:F2} ms, " +
                                   $"max {giftMaxMs:F2} ms; uploads max {burstMaxTiles} tiles/frame ({burstMaxTiles * 8} KB, cap 128); " +
                                   $"grass CPU max {burstMaxCpu:F3} ms; backlog after burst {backlog} tiles");
                yield return Shot("07_after_gift_burst");
            }

            _report.AppendLine("Errors/exceptions logged during capture: " + _errors);
            Finish();
        }

        private void SetUpOffscreen(Camera cam)
        {
            _cam = cam;
            _rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4, name = "CaptureRT" };
            _cam.targetTexture = _rt;
            // A second camera drawing onto an MSAA target crashes D3D12 (Camera::ResolveLastTargetToCurrentTarget),
            // so the HUD camera draws onto a plain resolved copy instead.
            _out = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "CaptureOut" };
            _frame = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            _clipFrame = new Texture2D(ClipWidth, ClipHeight, TextureFormat.RGBA32, false);
            _probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            // Overlay canvases are not drawn into camera targets; switch the HUD to camera space for the capture.
            foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (!canvas.gameObject.scene.IsValid() || !canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = UiCamera();
                canvas.planeDistance = 1f; // a normal distance: TMP SDF text breaks down a few cm from the camera
                _texts = canvas.GetComponentsInChildren<TMPro.TMP_Text>(true);
            }
        }

        /// <summary>HUD-only camera layered over the game image in the same render target.</summary>
        private Camera UiCamera()
        {
            if (_uiCam != null) return _uiCam;
            _uiCam = new GameObject("CaptureUICamera").AddComponent<Camera>();
            _uiCam.transform.SetParent(transform, false);
            _uiCam.clearFlags = CameraClearFlags.Depth;
            _uiCam.cullingMask = 1 << 5; // UI layer
            _uiCam.nearClipPlane = 0.1f;
            _uiCam.farClipPlane = 10f;
            _uiCam.targetTexture = _out;
            _uiCam.enabled = false; // rendered manually after the game camera
            return _uiCam;
        }

        private void LateUpdate()
        {
            if (_cam == null) return;
            if (_grabRequested)
            {
                RenderNow();
                var prev = RenderTexture.active;
                RenderTexture.active = _out;
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
            Graphics.Blit(_rt, _out); // resolve MSAA
            if (_uiCam != null) _uiCam.Render(); // HUD drawn on top by its own camera
        }

        /// <summary>1-pixel readback: waits for the GPU to finish the frame so the timing is real.</summary>
        private void Sync()
        {
            var prev = RenderTexture.active;
            RenderTexture.active = _out;
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
            Graphics.Blit(_out, small);
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

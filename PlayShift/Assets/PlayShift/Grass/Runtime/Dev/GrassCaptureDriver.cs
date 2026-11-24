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
    /// quality preset, a ~10 s walk-and-cut clip, then turns around to show the cut path. Exits Play mode when done.
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
        private const float ClipSeconds = 10f;
        private const uint ClipBitRate = 40_000_000; // bits per second
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
                // The title screen is seen through the orbiting map camera: render that one for this shot.
                var playerCam = _cam;
                var mapCam = MainMenu.Instance.MenuCamera;
                if (mapCam != null) { _cam = mapCam; _cam.targetTexture = _rt; }
                yield return Shot("00_main_menu");
                if (mapCam != null) { _cam.targetTexture = null; _cam = playerCam; }
                _report.AppendLine($"Title screen: level timer held at {manager.Progression.ElapsedSeconds:F2} s (should be 0)");
                MainMenu.Instance.Play();
                yield return GameWait(0.6f);
            }
            if (Array.IndexOf(args, "-captureGrid") >= 0)
            {
                yield return GridDiagnostics(cutter, manager, fieldRenderer);
                Finish();
                yield break;
            }
            _report.AppendLine("Player position after settling: " + fp.transform.position.ToString("F2"));
            yield return Shot("01_uncut_trimmer_off");

            _input.TrimmerHeld = true;
            yield return GameWait(1.0f);
            yield return Shot("02_trimmer_spinning");

            if (fieldRenderer != null)
            {
                string[] statNames = { "Draw Calls Count", "SetPass Calls Count", "Triangles Count", "Shadow Casters Count" };
                var stats = new Unity.Profiling.ProfilerRecorder[statNames.Length];
                var mainThread = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Internal, "Main Thread", 30);
                for (int i = 0; i < stats.Length; i++)
                    stats[i] = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, statNames[i]);
                foreach (var q in new[] { GrassQuality.Low, GrassQuality.Medium, GrassQuality.High, GrassQuality.Ultra })
                {
                    fieldRenderer.Quality = q;
                    yield return Frames(5);
                    var baseline = new long[stats.Length];
                    for (int i = 0; i < stats.Length; i++) baseline[i] = stats[i].LastValue;
                    _measureRequested = true;
                    while (_measureRequested) yield return null;
                    var per = new long[stats.Length];
                    for (int i = 0; i < stats.Length; i++) per[i] = Math.Max(0, stats[i].LastValue - baseline[i]) / 31;
                    _report.AppendLine($"Quality {q}: {_probeMs:F2} ms per 1080p frame (~{1000f / _probeMs:F0} FPS render-bound), " +
                                       $"{fieldRenderer.LastDrawnBlades:N0} blades drawn in {fieldRenderer.LastDrawCalls} draws, " +
                                       $"grass CPU {fieldRenderer.LastCpuMs:F3} ms, field total {fieldRenderer.TotalBlades:N0} blades; " +
                                       $"per frame {per[0]:N0} draw calls, {per[1]:N0} set-pass, {per[2] / 1000:N0}k tris, {per[3]:N0} shadow casters; " +
                                       $"shadows {QualitySettings.shadows} x{QualitySettings.shadowCascades} to {QualitySettings.shadowDistance:0} m, LOD bias {QualitySettings.lodBias:0.##}");
                    yield return Frames(30); // normal frames, for the CPU main-thread time
                    double cpuNs = 0; int cpuN = 0;
                    for (int i = 0; i < mainThread.Count; i++) { cpuNs += mainThread.GetSample(i).Value; cpuN++; }
                    _report.AppendLine($"    CPU main thread {(cpuN > 0 ? cpuNs / cpuN / 1e6 : 0):F2} ms per frame (editor batch frame, all scripts + culling + submit)");
                    yield return Shot("09_quality_" + q.ToString().ToLowerInvariant());
                }
                mainThread.Dispose();
                _report.AppendLine($"Memory: {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576.0:F0} MB allocated by Unity, " +
                                   $"{UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576.0:F0} MB graphics driver, " +
                                   $"{UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576.0:F0} MB managed heap in use");
                foreach (var r in stats) r.Dispose();
                fieldRenderer.Quality = GrassQuality.High;
                _report.AppendLine($"Grass GPU memory: {fieldRenderer.GpuBytes / 1048576.0:F2} MB (cells only; blades are procedural)");
                Step("frame times measured");
            }

            // Big-map numbers: targets, logic memory, cutting cost, save cost.
            {
                var layout = field.Layout;
                long cells = (long)layout.CellsX * layout.CellsZ;
                _report.AppendLine($"Field {layout.SizeX:0} x {layout.SizeZ:0} m, {layout.CellsX} x {layout.CellsZ} cells = {cells:N0} targets " +
                                   $"({cells / (layout.SizeX * layout.SizeZ):0} per m2, cell {layout.CellSizeX * 100f:0.#} cm); " +
                                   $"logic memory {cells * 12 / 1048576.0:F1} MB (units, height, cut time: 12 B/target); " +
                                   $"{field.TileCount:N0} data tiles of {GrassField.TileSize}x{GrassField.TileSize} cells");
                // Cutting cost: 2000 swept-capsule cuts (trimmer radius, one frame of walking) at random places, on a
                // separate copy of the field (same layout/level/seed) so the live lawn in the screenshots is untouched.
                var bench = new GrassField(layout);
                bench.Reset(field.Level, manager.Settings.seed);
                var rng = new System.Random(5);
                long remainingBefore = bench.Remaining, cutUnits = 0;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 2000; i++)
                {
                    float x = (float)rng.NextDouble() * layout.SizeX, z = (float)rng.NextDouble() * layout.SizeZ;
                    cutUnits += bench.CutCapsule(x, z, x + 0.04f, z + 0.03f, 0.65f);
                }
                double perCutUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 2000;
                _report.AppendLine($"Cutting: {perCutUs:F1} us per trimmer sweep (r 0.65 m), {cutUnits:N0} targets cut by 2000 sweeps, " +
                                   $"counter exact: {(remainingBefore - bench.Remaining == cutUnits ? "yes" : "NO")}");
                bench = null;
                // Save: encode the whole field state (what autosave does on the main thread; the disk write is async).
                sw.Restart();
                byte[] save;
                using (var ms = new MemoryStream()) { field.WriteState(ms); save = ms.ToArray(); }
                _report.AppendLine($"Save: {sw.Elapsed.TotalMilliseconds:F1} ms to encode, {save.Length / 1024.0:F0} KB");
                yield return null;
            }

            long before = field.Remaining;
            _report.AppendLine("Remaining before walk: " + before.ToString("N0"));

            // Clip: walk forward through the grass with a gentle side-to-side look, fixed 30 fps game time.
            int maxTiles = 0;
            float maxGrassCpu = 0f, sumGrassCpu = 0f;
            Time.captureFramerate = ClipFps;
            // H.264 High profile at a fixed high bit rate: dense grass is almost all fine detail, and the generic
            // "High" preset (~11 Mbps at 1080p) smeared it into blocks. 40 Mbps keeps the blades sharp.
            var attrs = new VideoTrackEncoderAttributes(new H264EncoderAttributes
            {
                gopSize = ClipFps,
                numConsecutiveBFrames = 2,
                profile = VideoEncodingProfile.H264High
            })
            {
                frameRate = new MediaRational(ClipFps),
                width = (uint)ClipWidth,
                height = (uint)ClipHeight,
                includeAlpha = false,
                bitRateMode = VideoBitrateMode.High,
                targetBitRate = ClipBitRate
            };
            var vm = FindAnyObjectByType<TrimmerViewmodel>();
            var clipPs = FindAnyObjectByType<GrassClippings>()?.GetComponent<ParticleSystem>();
            int maxClippings = 0;
            var spin = FindAnyObjectByType<TrimmerBlades>();
            Vector3 hMin = Vector3.positiveInfinity, hMax = Vector3.negativeInfinity;
            float housingTurn = 0f;
            Quaternion housing0 = Quaternion.identity;
            Transform housing = null;
            using (var encoder = new MediaEncoder(Path.Combine(_dir, "clip_walk_and_cut.mp4"), attrs))
            {
                int total = Mathf.RoundToInt(ClipSeconds * ClipFps);
                for (int f = 0; f < total; f++)
                {
                    float t = f / (float)ClipFps;
                    // 7 s walking and cutting with a gentle side-to-side look, then a slow 120 deg turn while still
                    // cutting so the clip also shows the cut path and the forest round the yard.
                    bool turn = t >= 7f;
                    _input.Move = new Vector2(0f, turn ? 0.3f : 0.55f);
                    _input.Look = turn ? new Vector2(40f / ClipFps, 0f)
                                       : new Vector2(Mathf.Cos(t * Mathf.PI) * 14f * Mathf.PI / ClipFps, 0f);
                    yield return Grab();
                    if (vm != null && fp != null)
                    {
                        // Head relative to the player (in the player's turn frame): should hold still while walking.
                        // Sampled by the viewmodel in the same frame it placed the head (no one-frame lag).
                        var local = vm.HeadOffsetFromPlayer;
                        if (t < 7f) { hMin = Vector3.Min(hMin, local); hMax = Vector3.Max(hMax, local); }
                        if (housing == null) { housing = vm.transform; housing0 = vm.HousingRotationFromPlayer; }
                        housingTurn = Mathf.Max(housingTurn, Quaternion.Angle(housing0, vm.HousingRotationFromPlayer));
                    }
                    if (clipPs != null) maxClippings = Mathf.Max(maxClippings, clipPs.particleCount);
                    if (fieldRenderer != null)
                    {
                        maxTiles = Mathf.Max(maxTiles, fieldRenderer.LastTilesUploaded);
                        maxGrassCpu = Mathf.Max(maxGrassCpu, fieldRenderer.LastCpuMs);
                        sumGrassCpu += fieldRenderer.LastCpuMs;
                    }
                    if (f == total / 2) File.WriteAllBytes(Path.Combine(_dir, "03_cutting.jpg"), _frame.EncodeToJPG(92));
                    if (f == 45 || f == 255 || f == total - 1) File.WriteAllBytes(Path.Combine(_dir, $"clip_frame_{f:000}.jpg"), _frame.EncodeToJPG(85));
                    encoder.AddFrame(ToClipFrame());
                }
                _report.AppendLine($"While cutting: max {maxTiles} tiles uploaded in a frame ({maxTiles * 8} KB), " +
                                   $"grass CPU avg {sumGrassCpu / total:F3} ms, max {maxGrassCpu:F3} ms");
            }
            _report.AppendLine($"Clippings in the air while cutting: max {maxClippings} (system playing: {(clipPs != null && clipPs.isPlaying)})");
            if (spin != null && spin.Spinners.Count > 0 && spin.Spinners[0] != null)
                _report.AppendLine($"Trimmer head while walking and cutting: spinning part '{spin.Spinners[0].name}' (only the blades should spin); " +
                                   $"head moved {(hMax.x - hMin.x) * 100f:F1} cm sideways, {(hMax.y - hMin.y) * 100f:F1} cm up/down, " +
                                   $"{(hMax.z - hMin.z) * 100f:F1} cm fore/aft relative to the player; housing turned at most {housingTurn:F1} deg (should be 0)");
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

            yield return CutAccuracyTest(cutter, manager);
            yield return CuttingQualityTest(cutter, manager, fieldRenderer);
            yield return EnvironmentTour(manager);

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

            // Aerial overview of the whole map (distant grass is ground-shaded from the same cell data).
            {
                var layout = field.Layout;
                var over = new GameObject("OverviewCam").AddComponent<Camera>();
                over.CopyFrom(fp.Camera);
                over.targetTexture = _rt;
                over.fieldOfView = 55f;
                Vector3 centre = manager.FieldTransform.position + new Vector3(layout.SizeX * 0.5f, 0f, layout.SizeZ * 0.5f);
                float span = Mathf.Max(layout.SizeX, layout.SizeZ);
                over.transform.position = centre + new Vector3(-span * 0.55f, span * 0.75f, -span * 0.75f);
                over.transform.LookAt(centre);
                var playerCam = _cam;
                _cam = over;
                yield return Shot("12_overview");
                _cam = playerCam;
                Destroy(over.gameObject);
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

        // ------------------------------------------------------------------ cut accuracy

        private TrimmerViewmodel _probeVm;
        private GrassCutter _probeCutter;
        private GrassLevelManager _probeManager;
        private bool _probeActive;
        private int _probeFrames, _probeGroundFrames, _probeMissFrames, _probeMaxMiss, _probeTotalMiss;
        private float _probeMaxLift, _probeMaxSide, _probeMinAhead, _probeMaxAhead, _probeMinGap, _probeMaxGap;
        // Where the head (and its blade circle) shows on screen, as viewport fractions (0-1, y from the bottom).
        private float _probeMinVx, _probeMaxVx, _probeMinVy, _probeMaxVy, _probeDiscMinVy, _probeDiscMinVx, _probeDiscMaxVx;
        private int _probeSinkLogs;

        /// <summary>Turns the player to face the middle of the field (room to walk in every test).</summary>
        private IEnumerator FaceFieldCentre(GrassLevelManager manager)
        {
            var f = manager.Field;
            Vector3 centre = manager.FieldTransform.TransformPoint(new Vector3(f.CellsX * f.CellSizeX * 0.5f, 0f, f.CellsZ * f.CellSizeZ * 0.5f));
            var pt = _probeVm.transform;
            Vector3 d = centre - pt.position;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            _input.Look = new Vector2(Mathf.DeltaAngle(pt.eulerAngles.y, yaw), 0f);
            yield return null;
            _input.Look = Vector2.zero;
            yield return null;
        }

        private void ResetProbe()
        {
            _probeFrames = _probeGroundFrames = _probeMissFrames = _probeMaxMiss = _probeTotalMiss = 0;
            _probeMaxLift = _probeMaxSide = 0f;
            _probeMinAhead = float.MaxValue; _probeMaxAhead = float.MinValue;
            _probeMinGap = float.MaxValue; _probeMaxGap = float.MinValue;
            _probeMinVx = _probeMinVy = _probeDiscMinVy = _probeDiscMinVx = float.MaxValue;
            _probeMaxVx = _probeMaxVy = _probeDiscMaxVx = float.MinValue;
        }

        /// <summary>Records where the head centre and the edges of its blade circle land on screen this frame.</summary>
        private void ProbeScreen()
        {
            var cam = Camera.main;
            if (cam == null || _probeVm == null) return;
            Vector3 h = _probeVm.HeadPosition;
            Vector3 v = cam.WorldToViewportPoint(h);
            _probeMinVx = Mathf.Min(_probeMinVx, v.x); _probeMaxVx = Mathf.Max(_probeMaxVx, v.x);
            _probeMinVy = Mathf.Min(_probeMinVy, v.y); _probeMaxVy = Mathf.Max(_probeMaxVy, v.y);
            float r = _probeCutter != null ? _probeCutter.CutRadius : 0.5f;
            Vector3 f = cam.transform.forward; f.y = 0f; f.Normalize();
            Vector3 rt = Vector3.Cross(Vector3.up, f);
            _probeDiscMinVy = Mathf.Min(_probeDiscMinVy, cam.WorldToViewportPoint(h - f * r).y); // near edge (lowest on screen)
            _probeDiscMinVx = Mathf.Min(_probeDiscMinVx, cam.WorldToViewportPoint(h - rt * r).x);
            _probeDiscMaxVx = Mathf.Max(_probeDiscMaxVx, cam.WorldToViewportPoint(h + rt * r).x);
        }

        private string ScreenSummary() =>
            _probeMinVx < float.MaxValue
                ? $"head centre on screen x {_probeMinVx:F2}-{_probeMaxVx:F2}, y {_probeMinVy:F2}-{_probeMaxVy:F2} (0-1, y from the bottom); " +
                  $"blade circle spans x {_probeDiscMinVx:F2}-{_probeDiscMaxVx:F2}, lowest y {_probeDiscMinVy:F2} (fully in view if all within 0-1)"
                : "head not measured on screen";

        /// <summary>
        /// Runs at the very end of the frame (after the trimmer placed its head and the cutter cut), i.e. exactly what
        /// is drawn: with the blades down, every cell whose centre lies inside the blade circle (minus half a cell) must
        /// already be cut. Uncut cells there = grass showing under the blades.
        /// </summary>
        private void ProbeCut()
        {
            var f = _probeManager.Field;
            Vector3 local = _probeManager.FieldTransform.InverseTransformPoint(_probeVm.HeadPosition);
            float lift = local.y - _probeCutter.HeadClearance;
            _probeFrames++;
            ProbeScreen();
            // Where the head is relative to the player: it must stay in front, not drift to the side.
            var pt = _probeVm.transform;
            Vector3 rel = _probeVm.HeadPosition - pt.position;
            float side = Mathf.Abs(Vector3.Dot(rel, pt.right)), ahead = Vector3.Dot(rel, pt.forward);
            _probeMaxSide = Mathf.Max(_probeMaxSide, side);
            _probeMinAhead = Mathf.Min(_probeMinAhead, ahead);
            _probeMaxAhead = Mathf.Max(_probeMaxAhead, ahead);
            // Gap between the lowest point of the blades and the real ground under the head (cutting height).
            Vector3 hp = _probeVm.HeadPosition;
            if (Physics.Raycast(hp + Vector3.up * 1.5f, Vector3.down, out var g, 4f, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                float gap = hp.y - g.point.y - (_probeCutter.HeadClearance - _probeVm.CuttingHeight);
                _probeMinGap = Mathf.Min(_probeMinGap, gap);
                if (gap < -0.005f && _probeSinkLogs++ < 5)
                    UnityEngine.Debug.LogWarning($"[Capture] blades {gap * 100f:F1} cm below ground: head {hp}, ground {g.point.y:F3} ({g.collider.name}), " +
                                                 $"feet {_probeVm.transform.position}");
                _probeMaxGap = Mathf.Max(_probeMaxGap, gap);
            }
            if (lift > 0.05f) { _probeMaxLift = Mathf.Max(_probeMaxLift, lift); return; }
            _probeGroundFrames++;
            float r = _probeCutter.CutRadius, cs = f.CellSizeX, half = cs * 0.7072f, inner = r - half;
            int x0 = Mathf.Max(0, (int)((local.x - r) / cs)), x1 = Mathf.Min(f.CellsX - 1, (int)((local.x + r) / cs));
            int z0 = Mathf.Max(0, (int)((local.z - r) / cs)), z1 = Mathf.Min(f.CellsZ - 1, (int)((local.z + r) / cs));
            int miss = 0;
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = (x + 0.5f) * cs - local.x, dz = (z + 0.5f) * cs - local.z;
                    if (dx * dx + dz * dz < inner * inner && !f.IsCellCut(f.CellIndex(x, z))) miss++;
                }
            if (miss > 0) _probeMissFrames++;
            _probeMaxMiss = Mathf.Max(_probeMaxMiss, miss);
            _probeTotalMiss += miss;
        }

        private string ProbeSummary() =>
            $"{_probeGroundFrames}/{_probeFrames} frames blades down, frames with grass left under the blades: {_probeMissFrames} " +
            $"(max {_probeMaxMiss} cells, total {_probeTotalMiss})" + (_probeMaxLift > 0f ? $", max lift {_probeMaxLift * 100f:F0} cm" : "") +
            $"; head ahead {_probeMinAhead:F2}-{_probeMaxAhead:F2} m, max sideways {_probeMaxSide * 100f:F0} cm" +
            (_probeMinGap < float.MaxValue ? $", blades {_probeMinGap * 100f:F1}-{_probeMaxGap * 100f:F1} cm above the ground" : "");

        /// <summary>
        /// Moves the trimmer the way a player does (standing, spinning fast, looking straight ahead / up / down while
        /// walking, walking backwards, sprint-strafing, crossing a bump, walking into the fence) at a fixed 30 fps and
        /// checks every frame that the grass is cut exactly under the blades and not outside them, that the head stays
        /// put whatever the camera pitch, and that the blades keep their cutting height over the real ground.
        /// </summary>
        /// <summary>
        /// Map tour: checks every tree/bush LOD group (cross-fade on, nothing ever culled), then shots from all four
        /// corners, a full 360 at the centre, a sprint across the whole map, and two title-screen views.
        /// </summary>
        private IEnumerator EnvironmentTour(GrassLevelManager manager)
        {
            var vm = FindAnyObjectByType<TrimmerViewmodel>();
            if (vm == null) yield break;
            var player = vm.transform;
            var cc = player.GetComponent<CharacterController>();
            int groups = 0, fade = 0, culled = 0;
            foreach (var g in FindObjectsByType<LODGroup>(FindObjectsSortMode.None))
            {
                groups++;
                if (g.fadeMode == LODFadeMode.CrossFade) fade++;
                var lods = g.GetLODs();
                if (lods.Length > 0 && lods[lods.Length - 1].screenRelativeTransitionHeight > 1e-5f) culled++;
            }
            _report.AppendLine($"Environment: {groups} tree/bush LOD groups, {fade} with cross-fade, {culled} that can disappear at a distance (must be 0)");
            long sceneryTris = 0; int sceneryRenderers = 0;
            var env = FindAnyObjectByType<OpenFieldEnvironment>();
            if (env != null)
                foreach (var mf in env.GetComponentsInChildren<MeshFilter>())
                    if (mf.sharedMesh != null && !mf.name.StartsWith("LOD1")) { sceneryTris += mf.sharedMesh.triangles.LongLength / 3; sceneryRenderers++; }
            _report.AppendLine($"  scenery meshes (near trees at full detail): {sceneryRenderers} renderers, {sceneryTris / 1000:N0}k triangles in total (before culling)");

            var f = manager.Field;
            Vector2 size = new Vector2(f.CellsX * f.CellSizeX, f.CellsZ * f.CellSizeZ);
            Vector3 origin = manager.FieldTransform.position;
            Vector3 centre = origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f);
            _input.TrimmerHeld = false;

            void Teleport(Vector3 p)
            {
                if (cc != null) cc.enabled = false;
                player.position = new Vector3(p.x, player.position.y, p.z);
                if (cc != null) cc.enabled = true;
            }
            IEnumerator Face(Vector3 dir)
            {
                float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                _input.Look = new Vector2(Mathf.DeltaAngle(player.eulerAngles.y, yaw), 0f);
                yield return null;
                _input.Look = Vector2.zero;
                yield return Frames(20); // LOD cross-fades settle
            }

            // Four corners: stand 4 m in from each corner, looking out across the corner into the forest.
            var corners = new[] { new Vector2(0f, 0f), new Vector2(size.x, 0f), new Vector2(size.x, size.y), new Vector2(0f, size.y) };
            for (int i = 0; i < 4; i++)
            {
                Vector3 c = origin + new Vector3(corners[i].x, 0f, corners[i].y);
                Vector3 inward = (centre - c).normalized;
                Teleport(c + inward * 5.5f);
                yield return Face(-inward);
                yield return Shot($"20_corner_{i}");
            }
            // Full 360 at the centre, every 45 degrees.
            Teleport(centre);
            for (int k = 0; k < 8; k++)
            {
                float a = k * 45f * Mathf.Deg2Rad;
                yield return Face(new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)));
                yield return Shot($"21_turn_{k * 45:000}");
            }
            // Sprint corner to corner across the whole map.
            Teleport(origin + new Vector3(3f, 0f, 3f));
            yield return Face(new Vector3(size.x, 0f, size.y));
            int frames = 0;
            _input.Sprint = true;
            float dStart = Vector3.Distance(player.position, origin + new Vector3(size.x - 3f, player.position.y, size.y - 3f));
            while (frames < 1200 && Vector3.Distance(new Vector3(player.position.x, 0f, player.position.z), origin + new Vector3(size.x - 4f, 0f, size.y - 4f)) > 2f)
            {
                _input.Move = new Vector2(0f, 1f);
                yield return null;
                frames++;
            }
            _input.Move = Vector2.zero; _input.Sprint = false;
            _report.AppendLine($"  sprinted corner to corner ({dStart:F0} m) in {frames} frames at 30 fps");
            yield return Shot("22_far_corner_arrival");

            // Title screen: the cinematic backyard view at two points of its slow orbit.
            if (MainMenu.Instance != null)
            {
                MainMenu.Instance.Show();
                yield return Frames(10);
                var playerCam = _cam;
                var mapCam = MainMenu.Instance.MenuCamera;
                if (mapCam != null) { _cam = mapCam; _cam.targetTexture = _rt; }
                yield return Shot("23_menu_a");
                yield return Frames(450); // 15 s of orbit
                yield return Shot("23_menu_b");
                if (mapCam != null) { _cam.targetTexture = null; _cam = playerCam; }
                MainMenu.Instance.Play();
                yield return Frames(10);
            }
            Step("environment tour done");
        }

        /// <summary>Level side view of the cutting head (orthographic, at blade height) to show the blades are flat.</summary>
        private IEnumerator SideShot(Transform rotor, string name)
        {
            var go = new GameObject("SideTestCamera");
            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(_cam);
            cam.orthographic = true;
            cam.orthographicSize = 0.45f;
            cam.nearClipPlane = 0.05f;
            Vector3 c = rotor.position;
            Vector3 side = Vector3.Cross(Vector3.up, _probeVm != null ? _probeVm.transform.forward : Vector3.forward).normalized;
            cam.transform.SetPositionAndRotation(c + side * 2.2f, Quaternion.LookRotation(-side, Vector3.up));
            cam.targetTexture = _rt;
            var keep = _cam;
            _cam = cam;
            yield return Shot(name);
            _cam = keep;
            Destroy(go);
        }

        /// <summary>
        /// Cutting feel and shape: start/stop timing (and nothing cut after release), rapid clicking, accuracy at 30-240
        /// fps, diagonal / circle / S-curve paths photographed from above, the fine cut mask's alignment, both upgrades
        /// (wider cut, extra heads), and all five levels with the counter checked on each.
        /// </summary>
        private IEnumerator CuttingQualityTest(GrassCutter cutter, GrassLevelManager manager, GrassFieldRenderer fr)
        {
            var vm = _probeVm != null ? _probeVm : FindAnyObjectByType<TrimmerViewmodel>();
            var tb = vm != null ? vm.GetComponent<TrimmerBlades>() : null;
            if (vm == null || tb == null || cutter == null) yield break;
            _probeVm = vm; _probeCutter = cutter; _probeManager = manager;
            var player = vm.transform;
            var cc = player.GetComponent<CharacterController>();
            var f = manager.Field;
            Vector2 size = new Vector2(f.CellsX * f.CellSizeX, f.CellsZ * f.CellSizeZ);
            Vector3 origin = manager.FieldTransform.position;
            void Teleport(Vector3 p)
            {
                if (cc != null) cc.enabled = false;
                player.position = new Vector3(p.x, player.position.y, p.z);
                if (cc != null) cc.enabled = true;
            }
            _report.AppendLine("Cutting quality:");
            Time.captureFramerate = 30;

            // A) Start / stop.
            Teleport(origin + new Vector3(size.x * 0.25f, 0f, size.y * 0.75f));
            yield return FaceFieldCentre(manager);
            _input.TrimmerHeld = false;
            yield return Frames(20);
            _input.TrimmerHeld = true;
            int toFull = 0;
            while (tb.Rpm01 < 0.95f && toFull < 90) { yield return null; toFull++; }
            yield return Frames(15);
            _input.TrimmerHeld = false;
            long atRelease = f.Remaining;
            float angleAtRelease = tb.RotorAngle;
            int toStop = 0;
            while (tb.Rpm01 > 0.02f && toStop < 90) { _input.Move = new Vector2(0f, 0.6f); yield return null; toStop++; }
            for (int i = 0; i < 20; i++) { _input.Move = new Vector2(0f, 0.6f); yield return null; }
            _input.Move = Vector2.zero;
            float angleAfter = tb.RotorAngle;
            yield return Frames(10);
            _report.AppendLine($"  start: 95% speed after {toFull} frames ({toFull * 1000f / 30f:F0} ms); release: stopped after {toStop} frames " +
                               $"({toStop * 1000f / 30f:F0} ms); units cut after release while walking on: {atRelease - f.Remaining} (must be 0); " +
                               $"rotor kept its angle after stopping: {Mathf.Abs(Mathf.DeltaAngle(angleAfter, tb.RotorAngle)) < 0.01f}");

            // Rapid clicking while walking: 12 quick on/off clicks.
            long before = f.Remaining;
            for (int k = 0; k < 12; k++)
            {
                _input.TrimmerHeld = k % 2 == 0;
                for (int i = 0; i < 4; i++) { _input.Move = new Vector2(0.4f, 0.5f); yield return null; }
            }
            _input.TrimmerHeld = false; _input.Move = Vector2.zero;
            yield return Frames(10);
            _report.AppendLine($"  rapid clicking (12 clicks of 4 frames while walking): cut {before - f.Remaining:N0} units, blades at rest after: {tb.Rpm01 < 0.02f}");

            // B) Frame rates: walking + turning while cutting, exact under the blades at each rate.
            foreach (int fps in new[] { 30, 60, 120, 240 })
            {
                Time.captureFramerate = fps;
                Teleport(origin + new Vector3(size.x * (0.2f + 0.15f * (fps / 60)), 0f, size.y * 0.35f));
                _input.TrimmerHeld = true;
                yield return Frames(fps / 5);
                ResetProbe(); _probeActive = true;
                for (int i = 0; i < fps * 3 / 2; i++) { _input.Move = new Vector2(0.3f, 0.8f); _input.Look = new Vector2(90f / fps, 0f); yield return null; }
                _input.Move = Vector2.zero; _input.Look = Vector2.zero; _probeActive = false;
                _report.AppendLine($"  {fps} fps, walking + turning 90 deg/s for 1.5 s: {ProbeSummary()}");
            }
            Time.captureFramerate = 30;
            _input.TrimmerHeld = false;
            yield return Frames(10);

            // C) Path shapes on fresh grass, photographed from straight above.
            Vector3 area = origin + new Vector3(size.x * 0.72f, 0f, size.y * 0.72f);
            Teleport(area);
            yield return FaceFieldCentre(manager);
            _input.TrimmerHeld = true;
            yield return Frames(8);
            for (int i = 0; i < 40; i++) { _input.Move = new Vector2(0.7f, 0.7f); yield return null; }           // diagonal
            for (int i = 0; i < 60; i++) { _input.Move = new Vector2(0f, 0.55f); _input.Look = new Vector2(6f, 0f); yield return null; } // circle
            for (int i = 0; i < 90; i++) { _input.Move = new Vector2(0f, 0.6f); _input.Look = new Vector2(4.5f * Mathf.Sin(i * 0.07f), 0f); yield return null; } // S-curve
            _input.Move = Vector2.zero; _input.Look = Vector2.zero; _input.TrimmerHeld = false;
            yield return Frames(15);
            Vector3 headNow = vm.HeadPosition;
            {
                var go = new GameObject("TopDownTestCamera");
                var top = go.AddComponent<Camera>();
                top.CopyFrom(_cam);
                top.orthographic = true;
                top.orthographicSize = 4.5f;
                top.transform.SetPositionAndRotation(new Vector3((headNow.x + area.x) * 0.5f, area.y + 25f, (headNow.z + area.z) * 0.5f), Quaternion.Euler(90f, 0f, 0f));
                top.targetTexture = _rt;
                var keep = _cam;
                _cam = top;
                yield return Shot("24_cut_paths_top_down");
                _cam = keep;
                Destroy(go);
            }

            // D) Fine mask alignment: under the head it must read cut, at the mirrored spot (wrong orientation) not.
            var mask = fr != null ? fr.CutMask : null;
            if (mask != null && mask.Texture != null)
            {
                Vector3 l = manager.FieldTransform.InverseTransformPoint(headNow);
                float u = l.x / size.x, v = l.z / size.y;
                float ReadMask(float uu, float vv)
                {
                    var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    var prev = RenderTexture.active;
                    RenderTexture.active = mask.Texture;
                    tex.ReadPixels(new Rect(Mathf.Clamp(uu, 0f, 0.9999f) * mask.Width, Mathf.Clamp(vv, 0f, 0.9999f) * mask.Height, 1, 1), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    float r = tex.GetPixel(0, 0).r;
                    Destroy(tex);
                    return r;
                }
                float here = ReadMask(u, v), mirrored = ReadMask(u, 1f - v);
                _report.AppendLine($"  cut mask {mask.Width}x{mask.Height} ({size.x / mask.Width * 100f:F1} cm per texel): under the head {here:F2} (must be 1), " +
                                   $"at the mirrored spot {mirrored:F2} (0 unless cut there too); strokes drawn last frame {mask.LastStrokes}");
            }
            else _report.AppendLine("  cut mask: off");

            // E) Upgrades: a wider cut, then extra heads.
            Teleport(origin + new Vector3(size.x * 0.4f, 0f, size.y * 0.15f));
            yield return FaceFieldCentre(manager);
            manager.AddRadiusBoost(0.6f, 4f);
            _input.TrimmerHeld = true;
            yield return Frames(10);
            ResetProbe(); _probeActive = true;
            before = f.Remaining;
            for (int i = 0; i < 40; i++) { _input.Move = new Vector2(0f, 0.6f); yield return null; }
            _probeActive = false; _input.Move = Vector2.zero;
            _report.AppendLine($"  upgrade: wider cut (radius {cutter.CutRadius:F2} m): cut {before - f.Remaining:N0} units; {ProbeSummary()}");
            manager.Modifiers.Clear();
            manager.AddBlades(2, 4f);
            yield return Frames(10);
            before = f.Remaining;
            int strokes = 0;
            for (int i = 0; i < 40; i++) { _input.Move = new Vector2(0f, 0.6f); yield return null; strokes = Mathf.Max(strokes, GrassCutter.FrameStrokes.Count); }
            _input.Move = Vector2.zero;
            _report.AppendLine($"  upgrade: +2 heads: cut {before - f.Remaining:N0} units, up to {strokes} cut strokes per frame (3 heads)");
            manager.Modifiers.Clear();
            _input.TrimmerHeld = false;
            yield return Frames(15);

            // F) All five levels: heights, counter exact (remaining + cut + gift removals - additions = initial).
            int keepLevel = manager.Progression.CurrentLevelIndex + 1;
            for (int lv = 1; lv <= 5; lv++)
            {
                manager.StartLevel(lv);
                yield return Frames(5);
                f = manager.Field;
                Teleport(origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f));
                _input.TrimmerHeld = true;
                yield return Frames(8);
                for (int i = 0; i < 40; i++) { _input.Move = new Vector2(0f, 0.6f); yield return null; }
                _input.Move = Vector2.zero; _input.TrimmerHeld = false;
                yield return Frames(6);
                long sum = f.Remaining + f.CutByMower + f.RemovedByGifts - f.AddedByGifts;
                _report.AppendLine($"  level {lv}: grass {f.CurrentMinHeightCm:0}-{f.CurrentMaxHeightCm:0} cm, cut {f.CutByMower:N0} of {f.InitialUnits:N0}; " +
                                   $"remaining + cut = {sum:N0} (must equal {f.InitialUnits:N0})");
                if (lv == 5) yield return Shot("25_level5_cut");
            }
            manager.StartLevel(keepLevel);
            yield return Frames(5);
            Step("cutting quality done");
        }

        /// <summary>
        /// Where do square / rectangular artifacts and dark patches come from? Cuts straight, diagonal, curved and
        /// circular paths on a fresh lawn, then photographs them from above and from first person with: everything on,
        /// grass shadows off, blades off (ground only), the cut mask vs the 10 cm cells, and blades tinted by detail
        /// level. Then simulates a loaded save (mask rebuilt from the cells) and sends gifts, and photographs the
        /// whole lawn's cut state from above. Run alone with -captureGrid.
        /// </summary>
        private IEnumerator GridDiagnostics(GrassCutter cutter, GrassLevelManager manager, GrassFieldRenderer fr)
        {
            var vm = FindAnyObjectByType<TrimmerViewmodel>();
            if (vm == null || cutter == null || fr == null) { _report.AppendLine("grid diagnostics: player parts missing"); yield break; }
            _probeVm = vm; _probeCutter = cutter; _probeManager = manager;
            var player = vm.transform;
            var cc = player.GetComponent<CharacterController>();
            manager.StartLevel(1);
            yield return Frames(5);
            var f = manager.Field;
            Vector2 size = new Vector2(f.CellsX * f.CellSizeX, f.CellsZ * f.CellSizeZ);
            Vector3 origin = manager.FieldTransform.position;
            Vector3 c = origin + new Vector3(size.x * 0.5f, 0f, size.y * 0.5f);
            _report.AppendLine("Grid artifact diagnostics (level 1, fresh lawn):");
            Time.captureFramerate = 30;

            IEnumerator Go(Vector3 p, float yaw)
            {
                _input.TrimmerHeld = false;
                yield return Frames(6);
                if (cc != null) cc.enabled = false;
                player.position = new Vector3(p.x, player.position.y, p.z);
                if (cc != null) cc.enabled = true;
                _input.Look = new Vector2(Mathf.DeltaAngle(player.eulerAngles.y, yaw), 0f);
                yield return null;
                _input.Look = Vector2.zero;
                yield return Frames(4);
            }
            IEnumerator Cut(int frames, Vector2 move, Func<int, float> turn)
            {
                _input.TrimmerHeld = true;
                yield return Frames(6);
                for (int i = 0; i < frames; i++) { _input.Move = move; _input.Look = new Vector2(turn(i), 0f); yield return null; }
                _input.Move = Vector2.zero; _input.Look = Vector2.zero;
                _input.TrimmerHeld = false;
                yield return Frames(6);
            }

            // Paths, side by side (the head runs ~3 m ahead of the player).
            yield return Go(c + new Vector3(-6f, 0f, -3f), 0f);
            yield return Cut(60, new Vector2(0f, 0.6f), i => 0f);                            // straight
            yield return Go(c + new Vector3(-3.5f, 0f, -3f), 40f);
            yield return Cut(60, new Vector2(0f, 0.6f), i => 0f);                            // diagonal
            yield return Go(c + new Vector3(1.5f, 0f, -3f), 0f);
            yield return Cut(90, new Vector2(0f, 0.6f), i => 4.5f * Mathf.Sin(i * 0.09f));   // S-curve
            yield return Go(c + new Vector3(6f, 0f, -2f), 0f);
            yield return Cut(64, new Vector2(0f, 0.5f), i => 5.8f);                          // circle
            yield return Go(c + new Vector3(0f, 0f, -6.5f), 0f);                             // stand behind the paths
            yield return Frames(20);
            _report.AppendLine($"  paths cut: {f.CutByMower:N0} units");

            var playerCam = _cam;
            IEnumerator View(Vector3 pos, Quaternion rot, bool ortho, float orthoSize, float fov, string name)
            {
                var go = new GameObject("DiagCamera");
                var cam = go.AddComponent<Camera>();
                cam.CopyFrom(playerCam);
                cam.orthographic = ortho;
                if (ortho) cam.orthographicSize = orthoSize; else cam.fieldOfView = fov;
                cam.nearClipPlane = 0.1f;
                cam.transform.SetPositionAndRotation(pos, rot);
                cam.targetTexture = _rt;
                fr.LodCamera = cam;
                _cam = cam;
                yield return Frames(2);
                yield return Shot(name);
                _cam = playerCam;
                fr.LodCamera = null;
                Destroy(go);
            }
            IEnumerator Variants(string prefix, Vector3 pos, Quaternion rot, bool ortho, float orthoSize, float fov, bool all)
            {
                yield return View(pos, rot, ortho, orthoSize, fov, prefix + "_a_normal");
                GrassFieldRenderer.DebugNoBladeShadows = true;
                yield return View(pos, rot, ortho, orthoSize, fov, prefix + "_b_no_grass_shadows");
                GrassFieldRenderer.DebugNoBladeShadows = false;
                GrassFieldRenderer.DebugView = 2;
                yield return View(pos, rot, ortho, orthoSize, fov, prefix + "_c_detail_levels");
                GrassFieldRenderer.DebugView = 3;
                yield return View(pos, rot, ortho, orthoSize, fov, prefix + "_f_zones_chunks_culling");
                GrassFieldRenderer.DebugView = 0;
                if (!all) yield break;
                GrassFieldRenderer.DebugHideBlades = true;
                yield return View(pos, rot, ortho, orthoSize, fov, prefix + "_d_ground_only");
                GrassFieldRenderer.DebugHideBlades = false;
                GrassFieldRenderer.DebugView = 1;
                yield return View(pos, rot, ortho, orthoSize, fov, prefix + "_e_cut_mask_vs_cells");
                GrassFieldRenderer.DebugView = 0;
            }

            Quaternion down = Quaternion.Euler(90f, 0f, 0f);
            Vector3 topPos = c + new Vector3(0f, 7f, 1.5f);
            Vector3 fpPos = c + new Vector3(0f, 1.95f, -6.5f);
            Quaternion fpRot = Quaternion.Euler(35f, 0f, 0f);
            yield return Variants("G1_top", topPos, down, true, 5.5f, 60f, true);
            yield return Variants("G2_fp", fpPos, fpRot, false, 0f, playerCam.fieldOfView, true);
            // Wide view across the lawn (detail-level bands at 13 / 28 m on High).
            yield return Variants("G3_wide", c + new Vector3(0f, 3.2f, -16f), Quaternion.Euler(14f, 0f, 0f), false, 0f, 62f, false);
            // Elevated views (where the blade / flat-ground boundary showed): 9 m up looking across, and 40 m up.
            yield return Variants("G6_elevated", c + new Vector3(0f, 9f, -30f), Quaternion.Euler(24f, 0f, 0f), false, 0f, 62f, true);
            yield return Variants("G8_high", c + new Vector3(0f, 40f, -22f), Quaternion.Euler(62f, 0f, 0f), false, 0f, 62f, false);

            // A loaded save / new level rebuilds the fine mask from the 10 cm cells.
            fr.DebugRebuildMask();
            yield return Frames(3);
            GrassFieldRenderer.DebugView = 1;
            yield return View(topPos, down, true, 5.5f, 60f, "G4_top_after_reload_e_cut_mask_vs_cells");
            GrassFieldRenderer.DebugView = 0;
            yield return View(topPos, down, true, 5.5f, 60f, "G4_top_after_reload_a_normal");
            yield return View(fpPos, fpRot, false, 0f, playerCam.fieldOfView, "G4_fp_after_reload_a_normal");

            // Gifts as on a live: two "Ice Cream Cone" removals, a partial "Finger Heart" regrow, then one huge removal
            // (more than the round patches hold). Whole lawn from above (cut state) plus a close-up of a gift patch.
            Vector3 PatchSpot()
            {
                Vector3 best = c;
                float bestD = -1f;
                for (int i = 0; i < f.CellCount; i += 97)
                {
                    if (!f.IsCellCut(i)) continue;
                    Vector3 w = origin + new Vector3((i % f.CellsX + 0.5f) * f.CellSizeX, 0f, (i / f.CellsX + 0.5f) * f.CellSizeZ);
                    float d = Vector3.Distance(w, c);
                    if (d > 14f && (bestD < 0f || d < bestD)) { bestD = d; best = w; }
                }
                return best;
            }
            IEnumerator Lawn(string name)
            {
                yield return Frames(30);
                GrassFieldRenderer.DebugView = 1;
                yield return View(c + new Vector3(0f, 60f, 0f), down, true, size.y * 0.52f, 60f, name + "_whole_lawn_cut_state");
                Vector3 spot = PatchSpot();
                yield return View(spot + new Vector3(0f, 9f, 0f), down, true, 6f, 60f, name + "_patch_cut_state");
                GrassFieldRenderer.DebugView = 0;
                yield return View(spot + new Vector3(0f, 9f, 0f), down, true, 6f, 60f, name + "_patch_normal");
                yield return View(spot + new Vector3(0f, 1.95f, -6f), Quaternion.Euler(30f, 0f, 0f), false, 0f, playerCam.fieldOfView, name + "_patch_first_person");
            }
            long r1 = manager.RemoveGrass(15000), r2 = manager.RemoveGrass(15000);
            yield return Lawn("G5a_after_two_removals");
            long a1 = manager.AddGrass(10000);
            yield return Lawn("G5b_after_partial_regrow");
            long r3 = manager.RemoveGrass(250000);
            yield return Lawn("G5c_after_huge_removal");
            _report.AppendLine($"  gifts: removed {r1:N0} + {r2:N0}, regrew {a1:N0}, then removed {r3:N0} at once");

            // Title screen over a lawn with saved cuts: the menu must show an untouched lawn, and the cuts must still be
            // there (in the cells and on screen) once play resumes.
            if (MainMenu.Instance != null)
            {
                long remainingBefore = f.Remaining;
                MainMenu.Instance.Show();
                yield return Frames(20);
                var mapCam = MainMenu.Instance.MenuCamera;
                if (mapCam != null)
                {
                    mapCam.targetTexture = _rt;
                    _cam = mapCam;
                    yield return Shot("G7_menu_a");
                    yield return Frames(240); // 8 s of orbit
                    yield return Shot("G7_menu_b");
                    GrassFieldRenderer.DebugView = 3;
                    yield return Frames(2);
                    yield return Shot("G7_menu_c_zones_chunks_culling");
                    GrassFieldRenderer.DebugView = 0;
                    mapCam.targetTexture = null;
                    _cam = playerCam;
                }
                MainMenu.Instance.Play();
                yield return Frames(10);
                _report.AppendLine($"  title screen over saved cuts: grass left before {remainingBefore:N0}, after {f.Remaining:N0} (must be equal)");
                yield return View(c + new Vector3(0f, 60f, 0f), down, true, size.y * 0.52f, 60f, "G7_after_menu_whole_lawn_normal");
            }
            Time.captureFramerate = 0;
            Step("grid diagnostics done");
        }

        private IEnumerator CutAccuracyTest(GrassCutter cutter, GrassLevelManager manager)
        {
            _probeVm = FindAnyObjectByType<TrimmerViewmodel>();
            _probeCutter = cutter;
            _probeManager = manager;
            if (_probeVm == null || cutter == null) { _report.AppendLine("Cut accuracy: no trimmer/cutter, skipped"); yield break; }
            var f = manager.Field;
            _report.AppendLine($"Cut accuracy (30 fps, cut radius {cutter.CutRadius:F2} m = blade tips, cell {f.CellSizeX * 100f:F0} cm):");
            Time.captureFramerate = 30;

            // Fresh grass: step sideways with the trimmer off, look a little down (default view).
            _input.TrimmerHeld = false;
            _input.Look = Vector2.zero;
            _input.Move = new Vector2(1f, 0f);
            yield return Frames(45);
            _input.Move = Vector2.zero;
            yield return Frames(30); // stop, blades spin down

            // 1) Standing still: a clean disc under the blades and nothing outside it.
            ResetProbe();
            _input.TrimmerHeld = true;
            yield return Frames(20);
            _probeActive = true;
            yield return Frames(5);
            _probeActive = false;
            {
                Vector3 local = manager.FieldTransform.InverseTransformPoint(_probeVm.HeadPosition);
                float r = cutter.CutRadius, cs = f.CellSizeX, half = cs * 0.7072f;
                int over = 0, inside = 0;
                for (int z = 0; z < f.CellsZ; z++)
                {
                    float dz = (z + 0.5f) * cs - local.z;
                    if (Mathf.Abs(dz) > r + 0.6f) continue;
                    for (int x = 0; x < f.CellsX; x++)
                    {
                        float dx = (x + 0.5f) * cs - local.x;
                        if (Mathf.Abs(dx) > r + 0.6f || !f.IsCellCut(f.CellIndex(x, z))) continue;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d > r + half) over++; else inside++;
                    }
                }
                _report.AppendLine($"  standing: {ProbeSummary()}; cut cells inside the blade circle {inside}, outside it {over}");
            }
            yield return Shot("13_cut_standing");

            // 2) Spinning on the spot at 360 deg/s while cutting.
            ResetProbe(); _probeActive = true;
            for (int i = 0; i < 30; i++) { _input.Look = new Vector2(12f, 0f); yield return null; }
            _input.Look = Vector2.zero; _probeActive = false;
            _report.AppendLine($"  fast turn 360 deg/s: {ProbeSummary()}");

            // 3) Looking around while cutting: at the default angle and at both look limits (plus a flick past each limit,
            //    which must be held), the head must stay on the ground, level, at cutting height, fully in view, and cut
            //    exactly under the blades while walking, strafing and turning.
            var fpc = _probeVm.GetComponent<FirstPersonController>();
            var rig = fpc != null ? fpc.Rig : null;
            yield return FaceFieldCentre(manager);
            {
                float up = rig != null ? rig.MaxLookUp : 15f, down = rig != null ? -rig.MaxLookDown : -30f;
                float def = rig != null ? rig.DefaultPitch : -18f;
                if (rig != null)
                    _report.AppendLine($"  look limits: default {def:F0} deg, up to +{up:F0}, down to {down:F0}; vertical look locked: {rig.VerticalLookLocked}");
                foreach (var (label, target, shot) in new[]
                         {
                             ("default angle", def, "14_look_default"), ("max up", up, "15_look_max_up"), ("max down", down, "15b_look_max_down"),
                         })
                {
                    for (int i = 0; i < 10; i++) { _input.Look = new Vector2(0f, (target - fpc.Pitch) / (10 - i)); yield return null; }
                    // A further flick past the limit: the camera must stay at the limit.
                    _input.Look = new Vector2(0f, target > def ? 30f : target < def ? -30f : 0f);
                    yield return null;
                    _input.Look = Vector2.zero;
                    yield return null;
                    float camPitch = Camera.main != null ? -Mathf.DeltaAngle(0f, Camera.main.transform.eulerAngles.x) : float.NaN;
                    long before = f.Remaining;
                    ResetProbe(); _probeActive = true;
                    for (int i = 0; i < 40; i++) { _input.Move = new Vector2(0f, 0.6f); yield return null; }
                    _input.Move = Vector2.zero; _probeActive = false;
                    _report.AppendLine($"  {label} (camera at {camPitch:F1} deg after a flick past it): walking: cut {before - f.Remaining:N0} units; " +
                                       $"{ProbeSummary()}; {ScreenSummary()}");
                    yield return Shot(shot);
                    ResetProbe(); _probeActive = true;
                    for (int i = 0; i < 40; i++) { _input.Move = new Vector2(1f, 0f); yield return null; }
                    _input.Move = Vector2.zero; _probeActive = false;
                    _report.AppendLine($"    strafing right: {ProbeSummary()}; {ScreenSummary()}");
                    ResetProbe(); _probeActive = true;
                    for (int i = 0; i < 40; i++) { _input.Move = new Vector2(0f, 0.6f); _input.Look = new Vector2(4f, 0f); yield return null; }
                    _input.Move = Vector2.zero; _input.Look = Vector2.zero; _probeActive = false;
                    _report.AppendLine($"    walking + turning 120 deg/s: {ProbeSummary()}; {ScreenSummary()}");
                    yield return FaceFieldCentre(manager);
                }
                for (int i = 0; i < 10; i++) { _input.Look = new Vector2(0f, (def - fpc.Pitch) / (10 - i)); yield return null; }
                _input.Look = Vector2.zero;

                // 3a) Lift sweep: every 2-3 degrees from max down to max up. At each angle: where the head shows on
                //     screen, how high it is lifted, whether the blade disc is level and fully in view, and whether it
                //     cuts while walking (it must stop cutting once lifted out of the grass).
                if (rig != null)
                {
                    _report.AppendLine("  lift sweep (camera deg: head centre from screen top, lowest blade point above screen bottom, lift cm, disc tilt deg, cut while walking):");
                    var camL = Camera.main;
                    float rad = _probeCutter != null ? _probeCutter.CutRadius : 0.5f;
                    int steps = 10;
                    for (int s = 0; s <= steps; s++)
                    {
                        float target = Mathf.Lerp(down, up, s / (float)steps);
                        for (int i = 0; i < 8; i++) { _input.Look = new Vector2(0f, (target - fpc.Pitch) / (8 - i)); yield return null; }
                        _input.Look = Vector2.zero;
                        for (int i = 0; i < 12; i++) yield return null; // let the lift settle (no input)
                        Vector3 hp = _probeVm.HeadPosition;
                        Vector3 vpC = camL.WorldToViewportPoint(hp);
                        float lowest = 1f;
                        for (int k = 0; k < 16; k++)
                        {
                            float a = k * Mathf.PI / 8f;
                            Vector3 rimP = hp + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * rad;
                            lowest = Mathf.Min(lowest, camL.WorldToViewportPoint(rimP).y);
                        }
                        float tilt = _probeVm.HeadRoot != null ? Vector3.Angle(_probeVm.HeadRoot.up, Vector3.up) : float.NaN;
                        long before = f.Remaining;
                        for (int i = 0; i < 15; i++) { _input.Move = new Vector2(0f, 0.6f); yield return null; }
                        _input.Move = Vector2.zero;
                        _report.AppendLine($"    {fpc.Pitch,6:F1}: head {(1f - vpC.y) * 100f:F0}% down, lowest blade {lowest * 100f:F0}% up, lift {_probeVm.LiftHeight * 100f:F0} cm, " +
                                           $"tilt {tilt:F1}, cut {before - f.Remaining:N0}");
                        if (s == steps / 2) yield return Shot("15c_look_middle_up");
                    }
                    for (int i = 0; i < 10; i++) { _input.Look = new Vector2(0f, (def - fpc.Pitch) / (10 - i)); yield return null; }
                    _input.Look = Vector2.zero;
                }
            }

            // 3b) Blade rotor: spin slowly, then at full speed. The rotor must turn rigidly about the head's axis: no
            //     tilt against the fixed housing, no movement of its centre, even angle steps; shots of the slow spin.
            {
                var tb = _probeVm.GetComponent<TrimmerBlades>();
                var rotor = tb != null && tb.Spinners.Count > 0 ? tb.Spinners[0] : null;
                if (rotor != null && rotor.parent != null)
                {
                    var housing = rotor.parent;
                    {
                        // Blades in the rotor's own space: equal reach, flat (height range = blade thickness), centred.
                        var parts = rotor.GetComponentsInChildren<MeshFilter>();
                        var bb = new Bounds(); bool any = false; int bladeCount = 0;
                        foreach (var pmf in parts)
                        {
                            if (pmf.sharedMesh == null || !pmf.name.StartsWith("Blade")) continue;
                            bladeCount++;
                            var mb = pmf.sharedMesh.bounds;
                            for (int k = 0; k < 8; k++)
                            {
                                Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                                Vector3 q = rotor.InverseTransformPoint(pmf.transform.TransformPoint(c));
                                if (!any) { bb = new Bounds(q, Vector3.zero); any = true; } else bb.Encapsulate(q);
                            }
                        }
                        float sc = rotor.lossyScale.x;
                        _report.AppendLine($"  rotor '{rotor.name}' (child of '{housing.name}', {rotor.childCount} parts, {bladeCount} blades): reach x {bb.extents.x * sc * 100f:F1} / " +
                                           $"z {bb.extents.z * sc * 100f:F1} cm (equal = same length), blade height range {bb.size.y * sc * 100f:F2} cm " +
                                           $"(= blade thickness when flat), centre offset {new Vector2(bb.center.x, bb.center.z).magnitude * sc * 1000f:F2} mm");
                    }
                    foreach (bool slow in new[] { true, false })
                    {
                        tb.SlowSpinPreview = slow;
                        _input.TrimmerHeld = true;
                        yield return Frames(slow ? 45 : 60);
                        Vector3 pos0 = rotor.localPosition;
                        float maxTilt = 0f, maxDrift = 0f, minStep = float.MaxValue, maxStep = 0f, last = tb.RotorAngle;
                        int n = slow ? 90 : 60;
                        for (int i = 0; i < n; i++)
                        {
                            yield return null;
                            maxTilt = Mathf.Max(maxTilt, Vector3.Angle(rotor.up, housing.up));
                            maxDrift = Mathf.Max(maxDrift, (rotor.localPosition - pos0).magnitude * housing.lossyScale.x);
                            float step = Mathf.Repeat(tb.RotorAngle - last, 360f);
                            last = tb.RotorAngle;
                            minStep = Mathf.Min(minStep, step); maxStep = Mathf.Max(maxStep, step);
                            if (slow && i % 3 == 0 && i <= 9) yield return Shot($"19_rotor_slow_{i / 3}");
                        }
                        _report.AppendLine($"  rotor {(slow ? "slow check" : "full speed")}: tilt against the housing max {maxTilt:F3} deg, centre moved max " +
                                           $"{maxDrift * 1000f:F2} mm, angle step per frame {minStep:F2}-{maxStep:F2} deg (steady = equal)");
                        yield return SideShot(rotor, slow ? "19_rotor_side_slow" : "19_rotor_side_full");
                    }
                    tb.SlowSpinPreview = false;
                    _input.TrimmerHeld = false;
                    yield return Frames(15);
                    yield return SideShot(rotor, "19_rotor_side_stopped");
                }
                else _report.AppendLine("  rotor: no spinning part found");
            }

            // 4) Walking backwards while cutting.
            ResetProbe(); _probeActive = true;
            for (int i = 0; i < 40; i++) { _input.Move = new Vector2(0f, -1f); yield return null; }
            _input.Move = Vector2.zero; _probeActive = false;
            _report.AppendLine($"  walking backwards: {ProbeSummary()}");

            // 5) Sprint-strafing diagonally while turning.
            ResetProbe(); _probeActive = true;
            _input.Sprint = true;
            for (int i = 0; i < 45; i++) { _input.Move = new Vector2(1f, 0.6f); _input.Look = new Vector2(2f, 0f); yield return null; }
            _input.Sprint = false; _input.Move = Vector2.zero; _input.Look = Vector2.zero; _probeActive = false;
            _report.AppendLine($"  sprint-strafe + turn: {ProbeSummary()}");

            // 5b) Uneven ground: a 12 cm step and a 9 deg ramp placed in front of the player (test-only colliders,
            //     drawn so they show in the shot). The head must climb and come down without sinking in or floating.
            yield return FaceFieldCentre(manager);
            {
                var pt = _probeVm.transform;
                var bump = new GameObject("TestBump");
                Vector3 c0 = pt.position + pt.forward * 3.5f;
                var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
                step.transform.SetParent(bump.transform, false);
                step.transform.SetPositionAndRotation(c0 + Vector3.up * 0.06f, pt.rotation);
                step.transform.localScale = new Vector3(5f, 0.12f, 2.5f);
                var ramp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ramp.transform.SetParent(bump.transform, false);
                ramp.transform.SetPositionAndRotation(c0 + pt.forward * 3.4f + Vector3.up * 0.05f, pt.rotation * Quaternion.Euler(-9f, 0f, 0f));
                ramp.transform.localScale = new Vector3(5f, 0.3f, 4f);
                yield return null;
                ResetProbe(); _probeActive = true;
                for (int i = 0; i < 150; i++)
                {
                    _input.Move = new Vector2(0f, 0.55f);
                    if (i == 45) { _probeActive = false; yield return Shot("17_trimmer_on_bump"); _probeActive = true; }
                    yield return null;
                }
                _input.Move = Vector2.zero; _probeActive = false;
                _report.AppendLine($"  over a 12 cm step and a 9 deg ramp: blades {_probeMinGap * 100f:F1}-{_probeMaxGap * 100f:F1} cm above the ground " +
                                   $"(cutting height {_probeVm.CuttingHeight * 100f:F0} cm; must never be below 0), head ahead {_probeMinAhead:F2}-{_probeMaxAhead:F2} m");
                Destroy(bump);
                yield return null;
            }

            // 6) Walk into the fence with the trimmer running: the grass right at the lawn edge must still be cut
            //    (the fence wall stops the player and the head, but not short of the edge).
            {
                var player = _probeVm.transform;
                _input.Look = new Vector2(Mathf.DeltaAngle(player.eulerAngles.y, 180f), 0f); // face the near (z = 0) fence
                yield return null;
                _input.Look = Vector2.zero;
                _input.Move = new Vector2(0f, 1f);
                // Walk until the fence stops the player (wherever the earlier steps left them), at most 25 s.
                float walkStart = Time.time, lastZ = float.MaxValue, lastCheck = Time.time;
                while (Time.time - walkStart < 25f)
                {
                    yield return null;
                    if (Time.time - lastCheck < 0.5f) continue;
                    float z = player.position.z;
                    if (Mathf.Abs(lastZ - z) < 0.01f) break;
                    lastZ = z; lastCheck = Time.time;
                }
                _input.Move = Vector2.zero;
                yield return Frames(10);
                Vector3 local = manager.FieldTransform.InverseTransformPoint(_probeVm.HeadPosition);
                float cs = f.CellSizeX;
                int uncut = 0, checkedCells = 0;
                for (int cz = 0; cz < 2; cz++)
                    for (int cx = 0; cx < f.CellsX; cx++)
                    {
                        float x = (cx + 0.5f) * cs;
                        if (Mathf.Abs(x - local.x) > 0.3f) continue;
                        checkedCells++;
                        if (!f.IsCellCut(f.CellIndex(cx, cz))) uncut++;
                    }
                Vector3 pl = manager.FieldTransform.InverseTransformPoint(player.position);
                _report.AppendLine($"  walked into the fence: player stopped {pl.z:F2} m from the lawn edge, head centre at {local.z:F2} m; " +
                                   $"uncut edge cells in front of the blades: {uncut}/{checkedCells} (must be 0)");
                yield return Shot("16_lawn_edge_at_fence");

                // Look along the fence into the corner (the forest must be as dense there), level view.
                Vector3 corner = manager.FieldTransform.TransformPoint(new Vector3(-6f, 0f, -6f)) - player.position;
                _input.Look = new Vector2(Mathf.DeltaAngle(player.eulerAngles.y, Mathf.Atan2(corner.x, corner.z) * Mathf.Rad2Deg), 2f - fpc.Pitch);
                yield return null;
                _input.Look = Vector2.zero;
                yield return Frames(3);
                yield return Shot("18_forest_corner");
                _input.Look = new Vector2(0f, -18f - fpc.Pitch);
                yield return null;
                _input.Look = Vector2.zero;
            }
            _input.TrimmerHeld = false;
            Time.captureFramerate = 0;
            yield return Frames(5);
            Step("cut accuracy measured");
        }

        private void LateUpdate()
        {
            if (_probeActive && _probeVm != null) ProbeCut();
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

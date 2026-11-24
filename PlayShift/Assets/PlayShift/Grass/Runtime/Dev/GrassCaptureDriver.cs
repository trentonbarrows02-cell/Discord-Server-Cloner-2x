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
            _report.AppendLine("Player position after settling: " + fp.transform.position.ToString("F2"));
            yield return Shot("01_uncut_trimmer_off");

            _input.TrimmerHeld = true;
            yield return GameWait(1.0f);
            yield return Shot("02_trimmer_spinning");

            if (fieldRenderer != null)
            {
                string[] statNames = { "Draw Calls Count", "SetPass Calls Count", "Triangles Count", "Shadow Casters Count" };
                var stats = new Unity.Profiling.ProfilerRecorder[statNames.Length];
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
                    yield return Shot("09_quality_" + q.ToString().ToLowerInvariant());
                }
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
        }

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

            // 3) Looking around while cutting and walking: the head must stay on the ground at the same spot ahead
            //    (it follows yaw only) and cut exactly under the blades at every pitch. Absolute pitch targets.
            var fpc = _probeVm.GetComponent<FirstPersonController>();
            yield return FaceFieldCentre(manager);
            foreach (var (label, target, shot) in new[]
                     {
                         ("straight ahead", 0f, "14_cut_looking_straight_ahead"), ("slightly up", 15f, "15_cut_looking_slightly_up"),
                         ("slightly down", -15f, "15b_cut_looking_slightly_down"), ("steeply down", -55f, null), ("well up", 35f, null),
                     })
            {
                for (int i = 0; i < 10; i++) { _input.Look = new Vector2(0f, (target - fpc.Pitch) / (10 - i)); yield return null; }
                _input.Look = Vector2.zero;
                long before = f.Remaining;
                ResetProbe(); _probeActive = true;
                for (int i = 0; i < 40; i++) { _input.Move = new Vector2(0f, 0.6f); yield return null; }
                _input.Move = Vector2.zero; _probeActive = false;
                _report.AppendLine($"  looking {label} (pitch {fpc.Pitch:F0} deg), walking: cut {before - f.Remaining:N0} units; {ProbeSummary()}");
                if (shot != null) yield return Shot(shot);
            }
            for (int i = 0; i < 10; i++) { _input.Look = new Vector2(0f, (-18f - fpc.Pitch) / (10 - i)); yield return null; }
            _input.Look = Vector2.zero;

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

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
        private float _probeMaxLift, _probeMaxSide, _probeMinAhead, _probeMaxAhead;

        private void ResetProbe()
        {
            _probeFrames = _probeGroundFrames = _probeMissFrames = _probeMaxMiss = _probeTotalMiss = 0;
            _probeMaxLift = _probeMaxSide = 0f;
            _probeMinAhead = float.MaxValue; _probeMaxAhead = float.MinValue;
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
            $"; head ahead {_probeMinAhead:F2}-{_probeMaxAhead:F2} m, max sideways {_probeMaxSide * 100f:F0} cm";

        /// <summary>
        /// Moves the trimmer the way a player does (standing, spinning fast, looking down and up, sprint-strafing, looking
        /// up with it running) at a fixed 30 fps and checks every frame that the grass is cut exactly under the blades,
        /// not outside them, and not at all while the head is lifted.
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

            // 3) Looking down and up while cutting: the head slides in and out across the lawn. Absolute pitch targets
            //    (the start pitch depends on the window shape), from steeply down to just above the horizon and back.
            var fpc = _probeVm.GetComponent<FirstPersonController>();
            ResetProbe(); _probeActive = true;
            float minP = float.MaxValue, maxP = float.MinValue;
            foreach (var (target, frames) in new[] { (-60f, 15), (5f, 40), (-30f, 25) })
                for (int i = 0; i < frames; i++)
                {
                    _input.Look = new Vector2(0f, (target - fpc.Pitch) / (frames - i));
                    yield return null;
                    minP = Mathf.Min(minP, fpc.Pitch); maxP = Mathf.Max(maxP, fpc.Pitch);
                }
            _input.Look = Vector2.zero; _probeActive = false;
            _report.AppendLine($"  looking down/up (pitch {minP:F0} to {maxP:F0} deg): {ProbeSummary()}");
            yield return Shot("14_cut_after_pitch_sweep");

            // 4) Sprint-strafing diagonally while turning.
            ResetProbe(); _probeActive = true;
            _input.Sprint = true;
            for (int i = 0; i < 45; i++) { _input.Move = new Vector2(1f, 0.6f); _input.Look = new Vector2(2f, 0f); yield return null; }
            _input.Sprint = false; _input.Move = Vector2.zero; _input.Look = Vector2.zero; _probeActive = false;
            _report.AppendLine($"  sprint-strafe + turn: {ProbeSummary()}");

            // 5) Looking up with the trimmer running: the head lifts off the lawn and must not cut anything.
            for (int i = 0; i < 20; i++) { _input.Look = new Vector2(0f, 3f); yield return null; }
            _input.Look = Vector2.zero;
            yield return Frames(3);
            long beforeUp = f.Remaining;
            ResetProbe(); _probeActive = true;
            for (int i = 0; i < 30; i++) { _input.Move = new Vector2(0f, 1f); yield return null; }
            _input.Move = Vector2.zero; _probeActive = false;
            _report.AppendLine($"  looking up, trimmer on, walking: cut {beforeUp - f.Remaining:N0} units (must be 0); {ProbeSummary()}");
            yield return Shot("15_looking_up_lifted");

            // Back to the normal view.
            for (int i = 0; i < 20; i++) { _input.Look = new Vector2(0f, -3f); yield return null; }
            _input.Look = Vector2.zero;

            // 6) Walk into the hedge with the trimmer running: the grass right at the lawn edge must still be cut
            //    (the hedge wall stops the player and the head, but not short of the edge).
            {
                var player = _probeVm.transform;
                _input.Look = new Vector2(Mathf.DeltaAngle(player.eulerAngles.y, 180f), 0f); // face the near (z = 0) hedge
                yield return null;
                _input.Look = Vector2.zero;
                _input.Move = new Vector2(0f, 1f);
                yield return Frames(240);
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
                _report.AppendLine($"  walked into the hedge: player stopped {pl.z:F2} m from the lawn edge, head centre at {local.z:F2} m; " +
                                   $"uncut edge cells in front of the blades: {uncut}/{checkedCells} (must be 0)");
                yield return Shot("16_lawn_edge_at_hedge");
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

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace PlayShift.Grass
{
    /// <summary>
    /// Scene entry point for the five-level grass campaign. Owns the <see cref="GrassProgression"/>,
    /// feeds it time, binds the renderer and HUD, and moves between levels.
    ///
    /// The GameObject's transform is the lawn's origin corner: the lawn spans local X [0, size.x], Z [0, size.y].
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class GrassLevelManager : MonoBehaviour
    {
        [SerializeField] private GrassProgressionSettings settings;
        [SerializeField] private GrassFieldRenderer fieldRenderer;
        [Tooltip("Optional. Created automatically when empty.")]
        [SerializeField] private GrassHud hud;

        [Tooltip("1-based level to start on (handy for testing taller levels).")]
        [Min(1)] [SerializeField] private int startLevel = 1;
        [SerializeField] private bool autoStart = true;

        [Header("Save")]
        [Tooltip("Save which grass has been cut (and the level/timer), and restore it on the next start.")]
        [SerializeField] private bool saveProgress = true;
        [Tooltip("Autosave interval in seconds (only when something changed). Also saved on quit.")]
        [Min(5f)] [SerializeField] private float autosaveSeconds = 20f;

        [Header("Events")]
        public LevelStartedEvent onLevelStarted = new LevelStartedEvent();
        public LevelClearedEvent onLevelCleared = new LevelClearedEvent();
        public UnityEvent onAllLevelsCompleted = new UnityEvent();

        [Serializable] public sealed class LevelStartedEvent : UnityEvent<int> { }
        [Serializable] public sealed class LevelClearedEvent : UnityEvent<int, float, long> { }

        public static GrassLevelManager Instance { get; private set; }

        public GrassProgressionSettings Settings => settings;
        public GrassProgression Progression { get; private set; }

        /// <summary>Timed trimmer modifiers (speed, radius, slow, multi-blade); gifts and power-ups add to this.</summary>
        public TrimmerModifiers Modifiers { get; } = new TrimmerModifiers();
        public GrassField Field => Progression?.Field;
        public GrassHud Hud => hud;

        /// <summary>Lawn space. Cutters convert world positions into this transform's local space.</summary>
        public Transform FieldTransform => fieldRenderer != null ? fieldRenderer.transform : transform;

        private void Awake()
        {
            if (Instance != null && Instance != this)
                Debug.LogWarning("[PlayShift Grass] More than one GrassLevelManager in the scene; cutters will use the latest one.", this);
            Instance = this;

            if (settings == null)
            {
                Debug.LogWarning("[PlayShift Grass] No GrassProgressionSettings assigned; using the default five levels.", this);
                settings = GrassProgressionSettings.CreateRuntimeDefault();
            }

            Progression = new GrassProgression(settings.BuildLevels(), settings.BuildLayout(), settings.seed);
            Progression.LevelStarted += HandleLevelStarted;
            Progression.LevelCleared += HandleLevelCleared;
            Progression.AllLevelsCompleted += HandleAllLevelsCompleted;

            if (fieldRenderer == null) fieldRenderer = GetComponentInChildren<GrassFieldRenderer>();
            if (fieldRenderer == null) fieldRenderer = gameObject.AddComponent<GrassFieldRenderer>();

            if (hud == null) hud = FindExisting<GrassHud>();
            if (hud == null) hud = new GameObject("GrassHud").AddComponent<GrassHud>();
        }

        private void Start()
        {
            fieldRenderer.Bind(Progression.Field);
            hud.Bind(this);
            if (autoStart && !TryLoadProgress()) StartLevel(startLevel);
        }

        // ------------------------------------------------------------------ save / load

        private const uint SaveMagic = 0x564C5350; // "PSLV"
        private int _savedVersion = -1;
        private float _nextAutosave;

        /// <summary>Main-thread ms spent encoding the last save, and its size (profiling).</summary>
        public float LastSaveMs { get; private set; }
        public int LastSaveBytes { get; private set; }

        /// <summary>Batch-mode (captures/tests) and disabled saving never touch the save file.</summary>
        private bool SavingEnabled => saveProgress && !Application.isBatchMode;

        public string SavePath => System.IO.Path.Combine(Application.persistentDataPath, "PlayShiftGrass",
            "progress_" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + ".bin");

        /// <summary>Saves the current level, timer and every cell's state. Encoding is on this thread (a few ms for
        /// 1M targets); the file write happens on a worker thread so it never stalls a frame.</summary>
        public void SaveProgress()
        {
            if (!SavingEnabled || Progression == null || Progression.State != ProgressionState.Playing || Field.IsCleared) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var ms = new System.IO.MemoryStream(64 * 1024);
            using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(SaveMagic);
                w.Write(Progression.CurrentLevelIndex);
                w.Write(Progression.ElapsedSeconds);
            }
            Field.WriteState(ms);
            byte[] bytes = ms.ToArray();
            LastSaveMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            LastSaveBytes = bytes.Length;
            _savedVersion = Field.Version;
            string path = SavePath;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    string tmp = path + ".tmp";
                    System.IO.File.WriteAllBytes(tmp, bytes);
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    System.IO.File.Move(tmp, path); // never leaves a half-written save
                }
                catch (System.Exception e) { Debug.LogWarning("[PlayShift Grass] Could not save progress: " + e.Message); }
            });
        }

        /// <summary>Restores saved progress for this scene, if any and if it matches the levels/layout. False = start fresh.</summary>
        private bool TryLoadProgress()
        {
            if (!SavingEnabled || !System.IO.File.Exists(SavePath)) return false;
            try
            {
                using (var fs = System.IO.File.OpenRead(SavePath))
                using (var r = new System.IO.BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    if (r.ReadUInt32() != SaveMagic) return false;
                    int level = r.ReadInt32();
                    double elapsed = r.ReadDouble();
                    if (level < 0 || level >= Progression.LevelCount) return false;
                    Progression.StartLevel(level);
                    if (!Field.TryReadState(fs)) { Debug.LogWarning("[PlayShift Grass] Saved progress does not match this field; starting fresh."); return false; }
                    Progression.RestoreElapsed(elapsed);
                    _savedVersion = Field.Version;
                    Debug.Log($"[PlayShift Grass] Progress restored: level {level + 1}, {Field.Remaining:N0} grass left.");
                    return true;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PlayShift Grass] Could not load progress (" + e.Message + "); starting fresh.");
                return false;
            }
        }

        private void LateUpdate()
        {
            if (!SavingEnabled || Progression == null || Time.unscaledTime < _nextAutosave) return;
            _nextAutosave = Time.unscaledTime + autosaveSeconds;
            if (Field != null && Field.Version != _savedVersion) SaveProgress();
        }

        private void OnApplicationQuit() => SaveProgress();

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (Progression == null) return;
            Progression.LevelStarted -= HandleLevelStarted;
            Progression.LevelCleared -= HandleLevelCleared;
            Progression.AllLevelsCompleted -= HandleAllLevelsCompleted;
        }

        private void Update()
        {
            if (Progression == null) return;
            // Runs before the cutters (DefaultExecutionOrder -100): cut cells get stamped with this clock and
            // the grass shader animates them shortening from that moment.
            Progression.Field.Clock = Time.time;
            float dt = HoldTimer ? 0f : Time.deltaTime;
            Progression.Tick(dt);
            Modifiers.Tick(dt);
        }

        /// <summary>True while the title screen is up: the level timer and timed modifiers don't advance.</summary>
        public bool HoldTimer { get; set; }

        /// <summary>Starts a level by 1-based number.</summary>
        public void StartLevel(int levelNumber)
        {
            int index = Mathf.Clamp(levelNumber - 1, 0, Progression.LevelCount - 1);
            Progression.StartLevel(index);
        }

        /// <summary>Hooked to the NEXT LEVEL button.</summary>
        public void NextLevel()
        {
            if (!Progression.NextLevel())
                Debug.LogWarning("[PlayShift Grass] NextLevel ignored: the current level is not cleared or it is the final level.", this);
        }

        /// <summary>Hooked to PLAY AGAIN on the final victory screen.</summary>
        public void RestartCampaign() => Progression.RestartCampaign();

        public void RestartCurrentLevel() => Progression.StartLevel(Mathf.Max(0, Progression.CurrentLevelIndex));

        // Gameplay modifiers (usable from gifts, power-ups or UnityEvents).
        public void AddSpeedBoost(float fraction, float seconds) => Modifiers.Add(TrimmerModifierType.SpeedBoost, fraction, seconds);
        public void AddRadiusBoost(float fraction, float seconds) => Modifiers.Add(TrimmerModifierType.RadiusBoost, fraction, seconds);
        public void SlowTrimmer(float fraction, float seconds) => Modifiers.Add(TrimmerModifierType.Slow, fraction, seconds);
        public void AddBlades(int count, float seconds) => Modifiers.Add(TrimmerModifierType.MultiBlade, count, seconds);
        /// <summary>Regrows previously cut grass (same as an Add Grass gift).</summary>
        public long RegrowGrass(long units) => Progression.AddGrass(units);

        // Gift / scripting entry points (also usable from UnityEvents).
        public long AddGrass(long units) => Progression.AddGrass(units);
        public long RemoveGrass(long units) => Progression.RemoveGrass(units);
        public float IncreaseHeight(float cm) => Progression.IncreaseHeight(cm);

        /// <summary>World-space point → lawn-local metres (X, Z).</summary>
        public Vector2 WorldToField(Vector3 world)
        {
            Vector3 local = FieldTransform.InverseTransformPoint(world);
            return new Vector2(local.x, local.z);
        }

        private void HandleLevelStarted(int index)
        {
            hud.ShowPlaying(index, Progression.LevelCount, Progression.CurrentLevel);
            onLevelStarted.Invoke(index + 1);
        }

        private void HandleLevelCleared(LevelResult result)
        {
            Modifiers.Clear();
            if (!result.IsFinalLevel)
                hud.ShowLevelCleared(result, Progression.NextLevelDefinition);
            onLevelCleared.Invoke(result.LevelNumber, (float)result.CompletionSeconds, result.TotalGrassCut);
        }

        private void HandleAllLevelsCompleted(IReadOnlyList<LevelResult> results)
        {
            hud.ShowAllLevelsCompleted(results);
            onAllLevelsCompleted.Invoke();
        }

        private static T FindExisting<T>() where T : UnityEngine.Object
        {
#if UNITY_2023_1_OR_NEWER
            return FindAnyObjectByType<T>();
#else
            return FindObjectOfType<T>();
#endif
        }
    }
}

using System;
using System.Collections.Generic;

namespace PlayShift.Grass
{
    public enum ProgressionState
    {
        NotStarted,
        Playing,
        LevelCleared,
        AllLevelsCompleted,
    }

    /// <summary>Stats captured the moment a level's GRASS LEFT hits zero.</summary>
    public readonly struct LevelResult
    {
        public readonly int LevelIndex;
        public readonly string LevelName;
        public readonly double CompletionSeconds;
        public readonly long TotalGrassCut;
        public readonly long CutByMower;
        public readonly long RemovedByGifts;
        public readonly long AddedByGifts;
        public readonly bool IsFinalLevel;

        public LevelResult(int levelIndex, string levelName, double completionSeconds, long cutByMower,
            long removedByGifts, long addedByGifts, bool isFinalLevel)
        {
            LevelIndex = levelIndex;
            LevelName = levelName;
            CompletionSeconds = completionSeconds;
            CutByMower = cutByMower;
            RemovedByGifts = removedByGifts;
            AddedByGifts = addedByGifts;
            TotalGrassCut = cutByMower + removedByGifts;
            IsFinalLevel = isFinalLevel;
        }

        public int LevelNumber => LevelIndex + 1;
    }

    /// <summary>
    /// Five-level campaign state machine. One <see cref="GrassField"/> is reused for every level, so the
    /// same optimized cutting/rendering path serves all heights; only the level data changes.
    /// </summary>
    public sealed class GrassProgression
    {
        private readonly List<GrassLevelDefinition> _levels;
        private readonly List<LevelResult> _results = new List<LevelResult>();
        private readonly int _seed;

        public GrassProgression(IEnumerable<GrassLevelDefinition> levels, GrassFieldLayout layout, int seed = 1234)
        {
            if (levels == null) throw new ArgumentNullException(nameof(levels));
            _levels = new List<GrassLevelDefinition>(levels);
            if (_levels.Count == 0) throw new ArgumentException("At least one level is required.", nameof(levels));
            foreach (var level in _levels) level.Validate();

            _seed = seed;
            Field = new GrassField(layout);
            Field.Cleared += OnFieldCleared;
        }

        public event Action<int> LevelStarted;
        public event Action<LevelResult> LevelCleared;
        public event Action<IReadOnlyList<LevelResult>> AllLevelsCompleted;

        public GrassField Field { get; }
        public ProgressionState State { get; private set; } = ProgressionState.NotStarted;
        public int CurrentLevelIndex { get; private set; } = -1;
        public int LevelCount => _levels.Count;
        public IReadOnlyList<GrassLevelDefinition> Levels => _levels;
        public GrassLevelDefinition CurrentLevel => CurrentLevelIndex >= 0 ? _levels[CurrentLevelIndex] : null;
        public bool IsFinalLevel => CurrentLevelIndex == _levels.Count - 1;
        public bool HasNextLevel => CurrentLevelIndex >= 0 && CurrentLevelIndex < _levels.Count - 1;
        public GrassLevelDefinition NextLevelDefinition => HasNextLevel ? _levels[CurrentLevelIndex + 1] : null;

        /// <summary>Level timer. Runs only while Playing, so it stops the instant GRASS LEFT hits zero.</summary>
        public double ElapsedSeconds { get; private set; }

        public IReadOnlyList<LevelResult> Results => _results;
        public bool IsPlaying => State == ProgressionState.Playing;

        public void StartLevel(int index)
        {
            if (index < 0 || index >= _levels.Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (index == 0) _results.Clear();

            CurrentLevelIndex = index;
            ElapsedSeconds = 0;
            State = ProgressionState.Playing;
            Field.Reset(_levels[index], unchecked(_seed + index * 7919));
            LevelStarted?.Invoke(index);
        }

        /// <summary>Advances from the LEVEL CLEARED screen. Returns false if there is no next level or the current one is not cleared.</summary>
        public bool NextLevel()
        {
            if (State != ProgressionState.LevelCleared || !HasNextLevel) return false;
            StartLevel(CurrentLevelIndex + 1);
            return true;
        }

        public void RestartCampaign() => StartLevel(0);

        /// <summary>Restores the level timer after loading saved progress.</summary>
        public void RestoreElapsed(double seconds)
        {
            if (State == ProgressionState.Playing) ElapsedSeconds = Math.Max(0, seconds);
        }

        public void Tick(double deltaSeconds)
        {
            if (State == ProgressionState.Playing && deltaSeconds > 0) ElapsedSeconds += deltaSeconds;
        }

        // Gift entry points: only act while a level is being played.
        public long AddGrass(long units) => IsPlaying ? Field.AddGrass(units) : 0;
        public long RemoveGrass(long units) => IsPlaying ? Field.RemoveGrass(units) : 0;
        public float IncreaseHeight(float cm) => IsPlaying ? Field.IncreaseHeight(cm) : 0f;

        private void OnFieldCleared()
        {
            if (State != ProgressionState.Playing) return;

            bool final = IsFinalLevel;
            var result = new LevelResult(CurrentLevelIndex, CurrentLevel.DisplayName, ElapsedSeconds,
                Field.CutByMower, Field.RemovedByGifts, Field.AddedByGifts, final);
            _results.Add(result);

            State = final ? ProgressionState.AllLevelsCompleted : ProgressionState.LevelCleared;
            LevelCleared?.Invoke(result);
            if (final) AllLevelsCompleted?.Invoke(_results);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Designer-facing configuration: the five levels' heights and unit counts, the lawn layout, and the
    /// TikTok gift table. Create via Assets > Create > PlayShift > Grass Progression Settings.
    /// </summary>
    [CreateAssetMenu(fileName = "GrassProgressionSettings", menuName = "PlayShift/Grass Progression Settings")]
    public sealed class GrassProgressionSettings : ScriptableObject
    {
        [Serializable]
        public sealed class LevelEntry
        {
            public string displayName = "Level";
            [Min(0.5f)] public float minHeightCm = 15f;
            [Min(0.5f)] public float maxHeightCm = 25f;
            [Tooltip("GRASS LEFT at the start of the level.")]
            [Min(1)] public long startingUnits = GrassLevelDefinition.DefaultStartingUnits;
            [Tooltip("Fraction of the renderer's blades per cell drawn on this level (taller levels are thicker).")]
            [Range(0.05f, 1f)] public float bladeDensity = 1f;
        }

        [Serializable]
        public sealed class GiftRuleEntry
        {
            [Tooltip("TikTok gift name exactly as the LIVE connector reports it (case-insensitive).")]
            public string giftName = "Rose";
            public GiftActionType action = GiftActionType.AddGrass;
            [Tooltip("Grass units for Add/Remove Grass, centimetres for Increase Height. Per gift.")]
            public double amount = 1000;
            [Tooltip("A combo of N gifts applies the amount N times (modifiers: the duration N times).")]
            public bool scaleByRepeatCount = true;
            [Tooltip("Seconds, for the timed trimmer modifiers (Speed Boost, Radius Boost, Slow Trimmer, Multi Blade).")]
            public float durationSeconds = 10f;
        }

        [Header("Levels (played in order; the last one is the final level)")]
        public List<LevelEntry> levels = CreateDefaultLevelEntries();

        [Header("Lawn layout (shared by every level)")]
        [Tooltip("Lawn size in metres (X, Z).")]
        public Vector2 fieldSizeMeters = new Vector2(50f, 50f);
        [Tooltip("Logical cutting grid. 250 x 250 = 62,500 cells = 16 units per cell for 1,000,000 units.")]
        public Vector2Int cellCount = new Vector2Int(250, 250);
        [Tooltip("Gift height increases never push grass above this.")]
        public float absoluteMaxHeightCm = 220f;
        [Tooltip("Once every cell is grown, Add Grass gifts can thicken cells up to this multiple of normal.")]
        [Min(1)] public int maxUnitsPerCellMultiplier = 4;
        [Tooltip("Size of natural tall/short patches, in cells.")]
        [Min(1f)] public float heightNoiseCells = 14f;
        public int seed = 1234;

        [Header("TikTok gifts")]
        public bool ignoreStreakInProgress = true;
        public List<GiftRuleEntry> giftRules = CreateDefaultGiftEntries();

        public List<GrassLevelDefinition> BuildLevels()
        {
            var result = new List<GrassLevelDefinition>(levels.Count);
            for (int i = 0; i < levels.Count; i++)
            {
                var e = levels[i];
                string name = string.IsNullOrWhiteSpace(e.displayName) ? $"Level {i + 1}" : e.displayName;
                result.Add(new GrassLevelDefinition(name, e.minHeightCm, Mathf.Max(e.minHeightCm, e.maxHeightCm), e.startingUnits, Mathf.Clamp(e.bladeDensity, 0.05f, 1f)));
            }
            return result;
        }

        public GrassFieldLayout BuildLayout()
        {
            return new GrassFieldLayout
            {
                CellsX = Mathf.Max(1, cellCount.x),
                CellsZ = Mathf.Max(1, cellCount.y),
                SizeX = Mathf.Max(0.1f, fieldSizeMeters.x),
                SizeZ = Mathf.Max(0.1f, fieldSizeMeters.y),
                AbsoluteMaxHeightCm = absoluteMaxHeightCm,
                MaxUnitsPerCellMultiplier = maxUnitsPerCellMultiplier,
                HeightNoiseCells = heightNoiseCells,
            };
        }

        public List<GiftRule> BuildGiftRules()
        {
            var result = new List<GiftRule>(giftRules.Count);
            foreach (var g in giftRules)
                if (g != null && !string.IsNullOrWhiteSpace(g.giftName))
                    result.Add(new GiftRule(g.giftName, g.action, g.amount, g.scaleByRepeatCount, g.durationSeconds));
            return result;
        }

        private void OnValidate()
        {
            foreach (var e in levels)
            {
                if (e == null) continue;
                if (e.maxHeightCm < e.minHeightCm) e.maxHeightCm = e.minHeightCm;
                if (e.startingUnits < 1) e.startingUnits = 1;
            }
            float tallest = 0f;
            foreach (var e in levels) if (e != null) tallest = Mathf.Max(tallest, e.maxHeightCm);
            if (absoluteMaxHeightCm < tallest) absoluteMaxHeightCm = tallest;
        }

        public static List<LevelEntry> CreateDefaultLevelEntries()
        {
            var list = new List<LevelEntry>();
            foreach (var d in GrassLevelDefinition.CreateDefaultLevels())
                list.Add(new LevelEntry { displayName = d.DisplayName, minHeightCm = d.MinHeightCm, maxHeightCm = d.MaxHeightCm, startingUnits = d.StartingUnits, bladeDensity = d.BladeDensity });
            return list;
        }

        public static List<GiftRuleEntry> CreateDefaultGiftEntries()
        {
            var list = new List<GiftRuleEntry>();
            foreach (var r in TikTokGiftRouter.CreateDefaultRules())
                list.Add(new GiftRuleEntry { giftName = r.GiftName, action = r.Action, amount = r.Amount, scaleByRepeatCount = r.ScaleByRepeatCount, durationSeconds = r.DurationSeconds });
            return list;
        }

        /// <summary>Used when no asset is assigned so the system still runs with the spec defaults.</summary>
        public static GrassProgressionSettings CreateRuntimeDefault()
        {
            var s = CreateInstance<GrassProgressionSettings>();
            s.name = "GrassProgressionSettings (runtime default)";
            return s;
        }
    }
}

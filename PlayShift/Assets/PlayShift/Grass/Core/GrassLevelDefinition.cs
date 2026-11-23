using System;
using System.Collections.Generic;

namespace PlayShift.Grass
{
    /// <summary>
    /// Data for one level: how much logical grass it starts with and how tall that grass is.
    /// Engine-free so it can be unit tested and shared between gameplay, rendering and UI.
    /// </summary>
    [Serializable]
    public sealed class GrassLevelDefinition
    {
        public const long DefaultStartingUnits = 1_000_000;

        public string DisplayName;
        public float MinHeightCm;
        public float MaxHeightCm;
        public long StartingUnits;

        public GrassLevelDefinition(string displayName, float minHeightCm, float maxHeightCm, long startingUnits = DefaultStartingUnits)
        {
            DisplayName = displayName;
            MinHeightCm = minHeightCm;
            MaxHeightCm = maxHeightCm;
            StartingUnits = startingUnits;
        }

        public void Validate()
        {
            if (MinHeightCm <= 0f) throw new ArgumentException($"{DisplayName}: MinHeightCm must be > 0 (was {MinHeightCm}).");
            if (MaxHeightCm < MinHeightCm) throw new ArgumentException($"{DisplayName}: MaxHeightCm ({MaxHeightCm}) must be >= MinHeightCm ({MinHeightCm}).");
            if (StartingUnits <= 0) throw new ArgumentException($"{DisplayName}: StartingUnits must be > 0 (was {StartingUnits}).");
        }

        /// <summary>The five shipped levels. Heights are defaults only; the Unity settings asset can override every value.</summary>
        public static List<GrassLevelDefinition> CreateDefaultLevels()
        {
            return new List<GrassLevelDefinition>
            {
                new GrassLevelDefinition("Level 1", 15f, 25f),
                new GrassLevelDefinition("Level 2", 30f, 45f),
                new GrassLevelDefinition("Level 3", 50f, 70f),
                new GrassLevelDefinition("Level 4", 80f, 110f),
                new GrassLevelDefinition("Level 5", 120f, 150f),
            };
        }
    }

    /// <summary>
    /// Physical layout of the lawn. Shared by every level, so the renderer builds its blade buffer once.
    /// Logical grass units live in cells; a cell is the smallest area the mower can cut.
    /// </summary>
    [Serializable]
    public sealed class GrassFieldLayout
    {
        public int CellsX = 250;
        public int CellsZ = 250;
        public float SizeX = 50f;
        public float SizeZ = 50f;

        /// <summary>Hard cap for gift height increases, in cm.</summary>
        public float AbsoluteMaxHeightCm = 220f;

        /// <summary>"Add grass" gifts may stack a cell up to this multiple of its normal unit count once every cell has regrown.</summary>
        public int MaxUnitsPerCellMultiplier = 4;

        /// <summary>Size of the smooth height patches, in cells. Bigger = broader tall/short patches.</summary>
        public float HeightNoiseCells = 14f;

        public int CellCount => CellsX * CellsZ;
        public float CellSizeX => SizeX / CellsX;
        public float CellSizeZ => SizeZ / CellsZ;

        public void Validate()
        {
            if (CellsX <= 0 || CellsZ <= 0) throw new ArgumentException("Grass field must have at least one cell on each axis.");
            if (SizeX <= 0f || SizeZ <= 0f) throw new ArgumentException("Grass field size must be > 0.");
            if (AbsoluteMaxHeightCm <= 0f) throw new ArgumentException("AbsoluteMaxHeightCm must be > 0.");
            if (MaxUnitsPerCellMultiplier < 1) throw new ArgumentException("MaxUnitsPerCellMultiplier must be >= 1.");
        }
    }
}

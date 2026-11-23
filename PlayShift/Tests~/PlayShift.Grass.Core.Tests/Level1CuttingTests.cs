using System;
using PlayShift.Grass;
using Xunit;

namespace PlayShift.Grass.Tests
{
    /// <summary>Level 1 first: the counting and cutting must be exact before anything is layered on top.</summary>
    public class Level1CuttingTests
    {
        private static GrassLevelDefinition Level1 => GrassLevelDefinition.CreateDefaultLevels()[0];

        private static GrassField NewLevel1Field(GrassFieldLayout layout = null)
        {
            var field = new GrassField(layout ?? new GrassFieldLayout());
            field.Reset(Level1, seed: 42);
            return field;
        }

        private static long SumUnits(GrassField f)
        {
            long s = 0;
            for (int i = 0; i < f.CellCount; i++) s += f.GetUnits(i);
            return s;
        }

        [Fact]
        public void Level1_StartsWithExactlyOneMillionUnits()
        {
            var f = NewLevel1Field();
            Assert.Equal(1_000_000, f.Remaining);
            Assert.Equal(1_000_000, f.InitialUnits);
            Assert.Equal(1_000_000, SumUnits(f));
            Assert.False(f.IsCleared);
        }

        [Theory]
        [InlineData(250, 250)]
        [InlineData(333, 333)] // 110,889 cells: 1,000,000 does not divide evenly
        [InlineData(97, 211)]
        [InlineData(1500, 1000)] // more cells than units
        public void ExactUnitTotal_ForAnyGridSize(int cx, int cz)
        {
            var f = NewLevel1Field(new GrassFieldLayout { CellsX = cx, CellsZ = cz });
            Assert.Equal(1_000_000, SumUnits(f));
            Assert.Equal(1_000_000, f.Remaining);
        }

        [Fact]
        public void Level1_HeightsAreWithin15To25Cm()
        {
            var f = NewLevel1Field();
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < f.CellCount; i++)
            {
                float h = f.GetHeightCm(i);
                Assert.InRange(h, 15f, 25f);
                min = Math.Min(min, h);
                max = Math.Max(max, h);
            }
            // Natural variation across the range, not one flat value.
            Assert.True(max - min > 5f, $"height spread too small: {min}..{max}");
        }

        [Fact]
        public void CutCircle_RemovesExactlyTheUnitsOfCoveredCells()
        {
            var f = NewLevel1Field();
            long before = f.Remaining;
            long cut = f.CutCircle(25f, 25f, 1.0f);

            Assert.True(cut > 0);
            Assert.Equal(before - cut, f.Remaining);
            Assert.Equal(f.Remaining, SumUnits(f));
            Assert.Equal(cut, f.CutByMower);

            // Every cell whose centre is inside the circle is cut; every cell clearly outside is untouched.
            for (int cz = 0; cz < f.CellsZ; cz++)
                for (int cx = 0; cx < f.CellsX; cx++)
                {
                    float px = (cx + 0.5f) * f.CellSizeX - 25f, pz = (cz + 0.5f) * f.CellSizeZ - 25f;
                    float d = MathF.Sqrt(px * px + pz * pz);
                    int i = f.CellIndex(cx, cz);
                    if (d <= 0.999f) Assert.True(f.IsCellCut(i));
                    if (d >= 1.001f) Assert.False(f.IsCellCut(i));
                }
        }

        [Fact]
        public void CuttingSameSpotTwice_DoesNotDoubleCount()
        {
            var f = NewLevel1Field();
            long first = f.CutCircle(10f, 10f, 0.8f);
            long second = f.CutCircle(10f, 10f, 0.8f);
            Assert.True(first > 0);
            Assert.Equal(0, second);
            Assert.Equal(1_000_000 - first, f.Remaining);
        }

        [Fact]
        public void CutCapsule_FastMoverLeavesNoGaps()
        {
            // One frame covering 20 m must cut the same strip as many small steps.
            var swept = NewLevel1Field();
            var stepped = NewLevel1Field();
            long a = swept.CutCapsule(5f, 20f, 25f, 20f, 0.6f);
            long b = 0;
            for (int s = 0; s < 400; s++)
            {
                float x0 = 5f + 20f * s / 400f, x1 = 5f + 20f * (s + 1) / 400f;
                b += stepped.CutCapsule(x0, 20f, x1, 20f, 0.6f);
            }
            Assert.Equal(b, a);
            for (int i = 0; i < swept.CellCount; i++) Assert.Equal(stepped.IsCellCut(i), swept.IsCellCut(i));
        }

        [Fact]
        public void CutOutsideTheLawn_DoesNothing()
        {
            var f = NewLevel1Field();
            Assert.Equal(0, f.CutCircle(-10f, -10f, 2f));
            Assert.Equal(0, f.CutCircle(100f, 25f, 2f));
            Assert.Equal(1_000_000, f.Remaining);
        }

        [Fact]
        public void MowingTheWholeLawn_ReachesZeroAndFiresClearedOnce()
        {
            var f = NewLevel1Field();
            int clearedCount = 0;
            long lastReported = -1;
            f.Cleared += () => clearedCount++;
            f.RemainingChanged += r => lastReported = r;

            // Mow in lanes like a player would, 1 m wide passes.
            const float radius = 0.6f;
            bool forward = true;
            for (float z = 0.5f; z < 50f + radius; z += 1.0f)
            {
                if (forward) f.CutCapsule(-1f, z, 51f, z, radius);
                else f.CutCapsule(51f, z, -1f, z, radius);
                forward = !forward;
            }

            Assert.Equal(0, f.Remaining);
            Assert.Equal(0, SumUnits(f));
            Assert.Equal(0, lastReported);
            Assert.Equal(1_000_000, f.CutByMower);
            Assert.True(f.IsCleared);
            Assert.Equal(1, clearedCount);

            // Counter is frozen after clearing.
            Assert.Equal(0, f.CutCircle(25f, 25f, 5f));
            Assert.Equal(1, clearedCount);
        }

        [Fact]
        public void DirtyRange_CoversCutCellsOnly()
        {
            var f = NewLevel1Field();
            Assert.True(f.TryConsumeDirtyRange(out _, out int initialCount));
            Assert.Equal(f.CellCount, initialCount);
            Assert.False(f.TryConsumeDirtyRange(out _, out _));

            f.CutCircle(10f, 10f, 0.5f);
            Assert.True(f.TryConsumeDirtyRange(out int start, out int count));
            Assert.True(count < f.CellCount / 10, "only a small slice should need uploading");
            for (int i = 0; i < f.CellCount; i++)
                if (f.IsCellCut(i)) Assert.InRange(i, start, start + count - 1);

            var heights = new float[f.CellCount];
            f.CopyRenderHeightsMeters(heights, 0, f.CellCount);
            for (int i = 0; i < f.CellCount; i++)
            {
                if (f.IsCellCut(i)) Assert.Equal(0f, heights[i]);
                else Assert.InRange(heights[i], 0.15f, 0.25f);
            }
        }
    }
}

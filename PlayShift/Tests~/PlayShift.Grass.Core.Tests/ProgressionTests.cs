using System.Collections.Generic;
using System.Linq;
using PlayShift.Grass;
using Xunit;

namespace PlayShift.Grass.Tests
{
    public class ProgressionTests
    {
        internal static GrassProgression NewCampaign() =>
            new GrassProgression(GrassLevelDefinition.CreateDefaultLevels(), new GrassFieldLayout(), seed: 99);

        internal static void MowEverything(GrassField f)
        {
            for (float z = 0.5f; z < f.Layout.SizeZ + 1f; z += 1f)
                f.CutCapsule(-1f, z, f.Layout.SizeX + 1f, z, 0.6f);
        }

        [Fact]
        public void DefaultLevels_MatchSpecHeights()
        {
            var levels = GrassLevelDefinition.CreateDefaultLevels();
            var expected = new (float min, float max)[] { (15, 25), (35, 50), (60, 80), (90, 110), (120, 150) };
            Assert.Equal(5, levels.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(expected[i].min, levels[i].MinHeightCm);
                Assert.Equal(expected[i].max, levels[i].MaxHeightCm);
                Assert.Equal(1_000_000, levels[i].StartingUnits);
                if (i > 0) Assert.True(levels[i].BladeDensity > levels[i - 1].BladeDensity, "each level must be denser");
            }
        }

        [Fact]
        public void EveryLevel_StartsAtOneMillion_WithItsOwnHeightRange()
        {
            var p = NewCampaign();
            p.StartLevel(0);
            for (int level = 0; level < 5; level++)
            {
                Assert.Equal(ProgressionState.Playing, p.State);
                Assert.Equal(level, p.CurrentLevelIndex);
                Assert.Equal(1_000_000, p.Field.Remaining);

                var def = p.CurrentLevel;
                for (int i = 0; i < p.Field.CellCount; i++)
                    Assert.InRange(p.Field.GetHeightCm(i), def.MinHeightCm, def.MaxHeightCm);

                MowEverything(p.Field);
                if (level < 4) Assert.True(p.NextLevel());
            }
        }

        [Fact]
        public void ClearingLevel1_StopsTimerAndCounter_AndReportsResult()
        {
            var p = NewCampaign();
            var cleared = new List<LevelResult>();
            p.LevelCleared += cleared.Add;
            p.StartLevel(0);

            p.Tick(30.0);
            p.Field.CutCircle(10f, 10f, 3f);
            p.Tick(42.5);
            MowEverything(p.Field);

            Assert.Equal(ProgressionState.LevelCleared, p.State);
            Assert.Single(cleared);
            Assert.Equal(1, cleared[0].LevelNumber);
            Assert.Equal(72.5, cleared[0].CompletionSeconds, 6);
            Assert.Equal(1_000_000, cleared[0].TotalGrassCut);
            Assert.False(cleared[0].IsFinalLevel);

            // Timer and GRASS LEFT are frozen on the LEVEL CLEARED screen.
            p.Tick(100.0);
            Assert.Equal(72.5, p.ElapsedSeconds, 6);
            Assert.Equal(0, p.Field.Remaining);
            Assert.Equal(0, p.Field.CutCircle(25f, 25f, 10f));
            Assert.Equal(0, p.AddGrass(5000));
            Assert.Equal(0, p.Field.Remaining);
        }

        [Fact]
        public void NextLevel_LoadsTallerGrass_AndResetsCounterAndTimer()
        {
            var p = NewCampaign();
            p.StartLevel(0);
            p.Tick(12);
            float maxLevel1 = Enumerable.Range(0, p.Field.CellCount).Max(i => p.Field.GetHeightCm(i));
            MowEverything(p.Field);

            int started = -1;
            p.LevelStarted += i => started = i;
            Assert.True(p.NextLevel());

            Assert.Equal(1, started);
            Assert.Equal(ProgressionState.Playing, p.State);
            Assert.Equal(1_000_000, p.Field.Remaining);
            Assert.Equal(0, p.ElapsedSeconds);
            Assert.False(p.Field.IsCleared);
            float minLevel2 = Enumerable.Range(0, p.Field.CellCount).Min(i => p.Field.GetHeightCm(i));
            Assert.True(minLevel2 > maxLevel1, "level 2 grass must be taller than all of level 1");
        }

        [Fact]
        public void NextLevel_IsRejectedWhilePlaying()
        {
            var p = NewCampaign();
            p.StartLevel(0);
            Assert.False(p.NextLevel());
            Assert.Equal(0, p.CurrentLevelIndex);
        }

        [Fact]
        public void Level5_IsFinal_AndRaisesAllLevelsCompleted()
        {
            var p = NewCampaign();
            IReadOnlyList<LevelResult> final = null;
            int clearedEvents = 0;
            p.LevelCleared += _ => clearedEvents++;
            p.AllLevelsCompleted += r => final = r;

            p.StartLevel(0);
            for (int i = 0; i < 5; i++)
            {
                p.Tick(10 + i);
                MowEverything(p.Field);
                if (i < 4)
                {
                    Assert.Null(final);
                    Assert.True(p.NextLevel());
                }
            }

            Assert.Equal(ProgressionState.AllLevelsCompleted, p.State);
            Assert.Equal(5, clearedEvents);
            Assert.NotNull(final);
            Assert.Equal(5, final.Count);
            Assert.True(final[4].IsFinalLevel);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, final.Select(r => r.LevelNumber));
            Assert.False(p.NextLevel()); // nothing after level 5

            p.RestartCampaign();
            Assert.Equal(0, p.CurrentLevelIndex);
            Assert.Equal(ProgressionState.Playing, p.State);
            Assert.Empty(p.Results);
        }

        [Fact]
        public void CustomHeights_AreHonoured()
        {
            var levels = new List<GrassLevelDefinition>
            {
                new GrassLevelDefinition("Short", 5, 6),
                new GrassLevelDefinition("Huge", 190, 200, 250_000),
            };
            var p = new GrassProgression(levels, new GrassFieldLayout { CellsX = 100, CellsZ = 100, SizeX = 20, SizeZ = 20 });
            p.StartLevel(1);
            Assert.Equal(250_000, p.Field.Remaining);
            for (int i = 0; i < p.Field.CellCount; i++) Assert.InRange(p.Field.GetHeightCm(i), 190f, 200f);
        }

        [Fact]
        public void InvalidHeights_AreRejected()
        {
            var bad = new List<GrassLevelDefinition> { new GrassLevelDefinition("Bad", 40, 30) };
            Assert.Throws<System.ArgumentException>(() => new GrassProgression(bad, new GrassFieldLayout()));
        }
    }
}

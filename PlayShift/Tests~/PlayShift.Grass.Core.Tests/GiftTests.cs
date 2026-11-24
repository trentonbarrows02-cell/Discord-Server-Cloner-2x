using System.Linq;
using PlayShift.Grass;
using Xunit;

namespace PlayShift.Grass.Tests
{
    public class GiftTests
    {
        private static long SumUnits(GrassField f)
        {
            long s = 0;
            for (int i = 0; i < f.CellCount; i++) s += f.GetUnits(i);
            return s;
        }

        private static GrassProgression Playing(int levelIndex = 0)
        {
            var p = ProgressionTests.NewCampaign();
            p.StartLevel(levelIndex);
            return p;
        }

        [Fact]
        public void AddGrass_RegrowsCutCells_ExactAmount()
        {
            var p = Playing();
            p.Field.CutCircle(25f, 25f, 6f);
            long afterCut = p.Field.Remaining;

            long added = p.AddGrass(12_345);
            Assert.Equal(12_345, added);
            Assert.Equal(afterCut + 12_345, p.Field.Remaining);
            Assert.Equal(p.Field.Remaining, SumUnits(p.Field));
            Assert.Equal(12_345, p.Field.AddedByGifts);
        }

        [Fact]
        public void AddGrass_ScatteredCuts_AllRegrown()
        {
            var p = Playing();
            var f = p.Field;
            // Many small cuts all over the lawn: more than MaxPatchRounds patches can reach; the last patch keeps growing.
            for (int i = 0; i < 60; i++) f.CutCircle(2f + (i * 7.3f) % 46f, 2f + (i * 3.1f) % 46f, 0.4f);
            long missing = f.InitialUnits - f.Remaining;
            Assert.True(missing > 0);

            long added = p.AddGrass(missing);
            Assert.Equal(missing, added);
            Assert.Equal(f.InitialUnits, f.Remaining);
            Assert.Equal(f.Remaining, SumUnits(f));
            for (int i = 0; i < f.CellCount; i++) Assert.False(f.IsCellCut(i));
        }

        [Fact]
        public void RemoveThenAdd_Repeatedly_CounterStaysExact()
        {
            var p = Playing();
            var f = p.Field;
            for (int round = 0; round < 20; round++)
            {
                p.RemoveGrass(25_000);
                f.CutCapsule(5f, 5f + round, 40f, 5f + round, 0.65f);
                p.AddGrass(25_000);
                Assert.Equal(f.Remaining, SumUnits(f));
            }
        }

        [Fact]
        public void AddGrass_OnUncutLawn_ThickensUpToCap()
        {
            var p = Playing();
            Assert.Equal(50_000, p.AddGrass(50_000));
            Assert.Equal(1_050_000, p.Field.Remaining);
            Assert.Equal(1_050_000, SumUnits(p.Field));

            // Cap = 4x base units per cell => at most 4,000,000 total on the default layout.
            long more = p.AddGrass(10_000_000);
            Assert.Equal(4_000_000, p.Field.Remaining);
            Assert.Equal(4_000_000 - 1_050_000, more);
            Assert.Equal(0, p.AddGrass(1));
        }

        [Fact]
        public void RemoveGrass_RemovesExactAmount()
        {
            var p = Playing();
            Assert.Equal(7_777, p.RemoveGrass(7_777));
            Assert.Equal(1_000_000 - 7_777, p.Field.Remaining);
            Assert.Equal(p.Field.Remaining, SumUnits(p.Field));
            Assert.Equal(7_777, p.Field.RemovedByGifts);
            Assert.Equal(0, p.Field.CutByMower);
        }

        [Fact]
        public void RemoveGrass_IsAPatch_NotScatteredNoise()
        {
            var p = Playing();
            p.RemoveGrass(400); // ~400 cells of 5 cm
            var cut = Enumerable.Range(0, p.Field.CellCount).Where(p.Field.IsCellCut).ToList();
            int minX = cut.Min(i => i % p.Field.CellsX), maxX = cut.Max(i => i % p.Field.CellsX);
            int minZ = cut.Min(i => i / p.Field.CellsX), maxZ = cut.Max(i => i / p.Field.CellsX);
            Assert.True(maxX - minX < 40 && maxZ - minZ < 40, "gift removal should be one compact patch");
        }

        /// <summary>Connected groups of cut cells (4-neighbour flood fill).</summary>
        private static int CutBlobs(GrassField f)
        {
            int w = f.CellsX, h = f.CellsZ;
            var seen = new bool[f.CellCount];
            var stack = new System.Collections.Generic.Stack<int>();
            int blobs = 0;
            for (int i = 0; i < f.CellCount; i++)
            {
                if (seen[i] || !f.IsCellCut(i)) continue;
                blobs++;
                seen[i] = true;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int c = stack.Pop(), x = c % w, z = c / w;
                    void Try(int nx, int nz)
                    {
                        if (nx < 0 || nz < 0 || nx >= w || nz >= h) return;
                        int n = nz * w + nx;
                        if (seen[n] || !f.IsCellCut(n)) return;
                        seen[n] = true;
                        stack.Push(n);
                    }
                    Try(x + 1, z); Try(x - 1, z); Try(x, z + 1); Try(x, z - 1);
                }
            }
            return blobs;
        }

        [Fact]
        public void RemoveGrass_BigGift_StaysAFewRoundPatches_NoScatteredTiles()
        {
            // More than the round patches hold: the rest must keep growing as a round patch, not fill square tiles
            // dotted around the lawn (those show as rectangles).
            var p = Playing();
            Assert.Equal(250_000, p.RemoveGrass(250_000));
            int blobs = CutBlobs(p.Field);
            Assert.True(blobs <= 9, $"expected at most 9 round patches, got {blobs} separate cut areas");
        }

        [Fact]
        public void RemoveGrass_ThatEmptiesTheLawn_ClearsTheLevel()
        {
            var p = Playing();
            LevelResult? result = null;
            p.LevelCleared += r => result = r;
            MowEverythingButStrip(p.Field);
            long left = p.Field.Remaining;
            Assert.True(left > 0);

            Assert.Equal(left, p.RemoveGrass(left * 10)); // never removes more than remains
            Assert.Equal(0, p.Field.Remaining);
            Assert.Equal(ProgressionState.LevelCleared, p.State);
            Assert.NotNull(result);
            Assert.Equal(1_000_000, result.Value.TotalGrassCut);
            Assert.Equal(left, result.Value.RemovedByGifts);
        }

        [Fact]
        public void IncreaseHeight_RaisesAllGrass_AndRegrowthUsesNewRange()
        {
            var p = Playing(); // level 1: 15-25 cm
            var before = Enumerable.Range(0, p.Field.CellCount).Select(p.Field.GetHeightCm).ToArray();

            Assert.Equal(10f, p.IncreaseHeight(10f));
            Assert.Equal(25f, p.Field.CurrentMinHeightCm);
            Assert.Equal(35f, p.Field.CurrentMaxHeightCm);
            for (int i = 0; i < p.Field.CellCount; i++) Assert.Equal(before[i] + 10f, p.Field.GetHeightCm(i), 3);

            p.Field.CutCircle(25f, 25f, 3f);
            p.AddGrass(5_000);
            for (int i = 0; i < p.Field.CellCount; i++)
                if (!p.Field.IsCellCut(i)) Assert.InRange(p.Field.GetHeightCm(i), 25f, 35.001f);
        }

        [Fact]
        public void IncreaseHeight_IsCappedAtAbsoluteMax()
        {
            var p = Playing(4); // 120-150 cm, cap 220 cm
            Assert.Equal(70f, p.IncreaseHeight(500f));
            Assert.Equal(0f, p.IncreaseHeight(5f));
            for (int i = 0; i < p.Field.CellCount; i++) Assert.True(p.Field.GetHeightCm(i) <= 220f);
        }

        [Fact]
        public void HeightBonus_ResetsOnNextLevel()
        {
            var p = Playing();
            p.IncreaseHeight(15f);
            ProgressionTests.MowEverything(p.Field);
            p.NextLevel();
            Assert.Equal(0f, p.Field.HeightBonusCm);
            for (int i = 0; i < p.Field.CellCount; i++) Assert.InRange(p.Field.GetHeightCm(i), 35f, 50f);
        }

        [Fact]
        public void GiftsWork_OnEveryLevel()
        {
            for (int level = 0; level < 5; level++)
            {
                var p = Playing(level);
                Assert.Equal(1000, p.RemoveGrass(1000));
                Assert.Equal(1000, p.AddGrass(1000));
                Assert.True(p.IncreaseHeight(5f) > 0f);
                Assert.Equal(1_000_000, p.Field.Remaining);
            }
        }

        // ---------------------------------------------------------------- router

        [Fact]
        public void Router_AppliesRulesCaseInsensitively_ScaledByCombo()
        {
            var p = Playing();
            var router = new TikTokGiftRouter(p, new[]
            {
                new GiftRule("Rose", GiftActionType.RemoveGrass, 100),
                new GiftRule("Galaxy", GiftActionType.IncreaseHeight, 5, scaleByRepeatCount: false),
            });

            var r1 = router.Handle(new TikTokGiftEvent("ann", "rose", repeatCount: 7));
            Assert.Equal(GiftOutcome.Applied, r1.Outcome);
            Assert.Equal(700, r1.Applied);
            Assert.Equal(1_000_000 - 700, p.Field.Remaining);
            Assert.Equal("@ann sent rose x7: -700 grass", r1.Describe());

            var r2 = router.Handle(new TikTokGiftEvent("bo", "Galaxy", repeatCount: 3));
            Assert.Equal(5.0, r2.Applied, 3);

            Assert.Equal(GiftOutcome.UnknownGift, router.Handle(new TikTokGiftEvent("x", "Mystery")).Outcome);
        }

        [Fact]
        public void Router_IgnoresStreakInProgress_AppliesOnStreakEnd()
        {
            var p = Playing();
            var router = new TikTokGiftRouter(p, new[] { new GiftRule("Rose", GiftActionType.AddGrass, 10) });
            p.Field.CutCircle(25f, 25f, 5f);
            long start = p.Field.Remaining;

            for (int n = 1; n <= 5; n++)
                Assert.Equal(GiftOutcome.IgnoredStreakInProgress, router.Handle(new TikTokGiftEvent("c", "Rose", n, isStreakInProgress: true)).Outcome);
            Assert.Equal(start, p.Field.Remaining);

            var end = router.Handle(new TikTokGiftEvent("c", "Rose", 5, isStreakInProgress: false));
            Assert.Equal(GiftOutcome.Applied, end.Outcome);
            Assert.Equal(start + 50, p.Field.Remaining);
        }

        [Fact]
        public void Router_DoesNothingWhenNotPlaying()
        {
            var p = Playing();
            var router = new TikTokGiftRouter(p);
            ProgressionTests.MowEverything(p.Field);
            Assert.Equal(GiftOutcome.NotPlaying, router.Handle(new TikTokGiftEvent("d", "Rose")).Outcome);
            Assert.Equal(0, p.Field.Remaining);
        }

        [Fact]
        public void DefaultRules_CoverAllThreeActions()
        {
            var rules = TikTokGiftRouter.CreateDefaultRules();
            Assert.Contains(rules, r => r.Action == GiftActionType.AddGrass);
            Assert.Contains(rules, r => r.Action == GiftActionType.RemoveGrass);
            Assert.Contains(rules, r => r.Action == GiftActionType.IncreaseHeight);
        }

        private static void MowEverythingButStrip(GrassField f)
        {
            for (float z = 0.5f; z < f.Layout.SizeZ - 3f; z += 1f)
                f.CutCapsule(-1f, z, f.Layout.SizeX + 1f, z, 0.6f);
        }
    }
}

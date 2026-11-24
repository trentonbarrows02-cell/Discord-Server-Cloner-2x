using System.IO;
using PlayShift.Grass;
using Xunit;

namespace PlayShift.Grass.Tests
{
    public class SaveLoadTests
    {
        private static GrassProgression PlayedLevel(out byte[] save)
        {
            var p = ProgressionTests.NewCampaign();
            p.StartLevel(0);
            var f = p.Field;
            f.Clock = 5f;
            for (int i = 0; i < 40; i++) f.CutCapsule(5f + i, 10f, 6f + i, 12f, 0.65f); // a mowed path
            f.CutCircle(30f, 30f, 2f);
            p.RemoveGrass(12_345);
            p.AddGrass(2_000);
            p.IncreaseHeight(7f);
            using (var ms = new MemoryStream()) { f.WriteState(ms); save = ms.ToArray(); }
            return p;
        }

        [Fact]
        public void RoundTrip_RestoresEveryCellAndTheCounter()
        {
            var a = PlayedLevel(out var save);
            var b = ProgressionTests.NewCampaign();
            b.StartLevel(0);
            Assert.True(b.Field.TryReadState(new MemoryStream(save)));

            Assert.Equal(a.Field.Remaining, b.Field.Remaining);
            Assert.Equal(a.Field.CutByMower, b.Field.CutByMower);
            Assert.Equal(a.Field.RemovedByGifts, b.Field.RemovedByGifts);
            Assert.Equal(a.Field.AddedByGifts, b.Field.AddedByGifts);
            Assert.Equal(a.Field.HeightBonusCm, b.Field.HeightBonusCm);
            for (int i = 0; i < a.Field.CellCount; i++)
            {
                Assert.Equal(a.Field.GetUnits(i), b.Field.GetUnits(i));
                Assert.Equal(a.Field.GetHeightCm(i), b.Field.GetHeightCm(i), 3);
            }
            Assert.Equal(b.Field.TileCount, b.Field.DirtyTileCount); // renderer re-uploads everything
        }

        /// <summary>
        /// The renderers draw a cell as grown when its cut time is negative. After loading, every cut cell must render
        /// as cut and every grown cell as grown, otherwise restored cut grass shows up as standing grass that the
        /// trimmer cannot cut (the bug seen in play).
        /// </summary>
        [Fact]
        public void RoundTrip_RendersExactlyTheCutCellsAsCut()
        {
            PlayedLevel(out var save);
            var b = ProgressionTests.NewCampaign();
            b.StartLevel(0);
            Assert.True(b.Field.TryReadState(new MemoryStream(save)));
            var render = new float[b.Field.CellCount * 2];
            b.Field.CopyRenderCells(render, 0, b.Field.CellCount);
            int cut = 0;
            for (int i = 0; i < b.Field.CellCount; i++)
            {
                bool drawnCut = render[2 * i + 1] >= 0f;
                Assert.Equal(b.Field.IsCellCut(i), drawnCut);
                if (drawnCut) cut++;
            }
            Assert.True(cut > 0);
        }

        [Fact]
        public void LevelWithFewerUnitsThanCells_EmptyCellsRenderAsCut()
        {
            var level = new GrassLevelDefinition("Sparse", 15, 25, 1000);
            var f = new GrassField(new GrassFieldLayout());
            f.Reset(level, 3);
            var render = new float[f.CellCount * 2];
            f.CopyRenderCells(render, 0, f.CellCount);
            for (int i = 0; i < f.CellCount; i++) Assert.Equal(f.IsCellCut(i), render[2 * i + 1] >= 0f);
        }

        [Fact]
        public void TypicalSave_IsSmall()
        {
            PlayedLevel(out var save);
            Assert.True(save.Length < 200_000, $"save was {save.Length:N0} bytes");
        }

        [Fact]
        public void SaveFromAnotherLevel_IsRejected_AndLeavesTheFieldAlone()
        {
            PlayedLevel(out var save);
            var b = ProgressionTests.NewCampaign();
            b.StartLevel(1);
            long before = b.Field.Remaining;
            Assert.False(b.Field.TryReadState(new MemoryStream(save)));
            Assert.Equal(before, b.Field.Remaining);
        }

        [Fact]
        public void DamagedSave_IsRejected()
        {
            PlayedLevel(out var save);
            var cut = new byte[save.Length / 2];
            System.Array.Copy(save, cut, cut.Length);
            var b = ProgressionTests.NewCampaign();
            b.StartLevel(0);
            long before = b.Field.Remaining;
            Assert.False(b.Field.TryReadState(new MemoryStream(cut)));
            Assert.Equal(before, b.Field.Remaining);
        }
    }
}

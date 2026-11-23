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

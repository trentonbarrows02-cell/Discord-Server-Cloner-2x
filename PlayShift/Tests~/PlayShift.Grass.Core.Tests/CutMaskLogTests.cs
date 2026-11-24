using PlayShift.Grass;
using Xunit;

namespace PlayShift.Grass.Tests
{
    /// <summary>
    /// The renderer's fine cut mask mirrors cell changes that are not trimmer strokes (gifts, new level, load).
    /// These tests pin down what the field reports for it.
    /// </summary>
    public class CutMaskLogTests
    {
        private static GrassField NewField()
        {
            var f = new GrassField(new GrassFieldLayout { CellsX = 200, CellsZ = 200, SizeX = 20f, SizeZ = 20f });
            f.Reset(new GrassLevelDefinition("L1", 15f, 25f, 40_000), 7);
            return f;
        }

        [Fact]
        public void NewLevel_AsksForAFullRebuild()
        {
            var f = NewField();
            Assert.True(f.MaskRebuildNeeded);
            f.AcknowledgeMaskRebuild();
            Assert.False(f.MaskRebuildNeeded);
            f.Reset(new GrassLevelDefinition("L2", 35f, 50f, 40_000), 8);
            Assert.True(f.MaskRebuildNeeded);
        }

        [Fact]
        public void TrimmerCuts_AreNotLogged_GiftsAre()
        {
            var f = NewField();
            f.AcknowledgeMaskRebuild();
            long cut = f.CutCapsule(5f, 5f, 8f, 5f, 0.6f);
            Assert.True(cut > 0);
            Assert.Empty(f.ExternalCellChanges);

            long removed = f.RemoveGrass(2_000);
            Assert.True(removed > 0);
            Assert.NotEmpty(f.ExternalCellChanges);
            foreach (int i in f.ExternalCellChanges) Assert.InRange(i, 0, 200 * 200 - 1);
            f.ClearExternalCellChanges();

            long added = f.AddGrass(1_000);
            Assert.True(added > 0);
            Assert.NotEmpty(f.ExternalCellChanges);
        }

        [Fact]
        public void LoggedGiftCells_MatchTheCellsThatChanged()
        {
            var f = NewField();
            f.AcknowledgeMaskRebuild();
            var before = new bool[200 * 200];
            for (int i = 0; i < before.Length; i++) before[i] = f.IsCellCut(i);
            f.RemoveGrass(3_000);
            var logged = new System.Collections.Generic.HashSet<int>(f.ExternalCellChanges);
            for (int i = 0; i < before.Length; i++)
                if (before[i] != f.IsCellCut(i)) Assert.Contains(i, logged);
        }
    }
}

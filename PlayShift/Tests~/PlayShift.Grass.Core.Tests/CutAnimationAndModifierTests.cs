using PlayShift.Grass;
using Xunit;

namespace PlayShift.Grass.Tests
{
    public class CutAnimationAndModifierTests
    {
        [Fact]
        public void CutCells_RecordTheClock_ForTheShorteningAnimation()
        {
            var p = ProgressionTests.NewCampaign();
            p.StartLevel(0);
            var f = p.Field;
            f.Clock = 12.5f;
            f.CutCircle(10f, 10f, 0.5f);
            f.Clock = 20f;
            f.CutCircle(30f, 30f, 0.5f);

            var cells = new float[f.CellCount * 2];
            f.CopyRenderCells(cells, 0, f.CellCount);
            int early = 0, late = 0;
            for (int i = 0; i < f.CellCount; i++)
            {
                Assert.InRange(cells[2 * i], 0.15f, 0.25f); // grown height is kept even after cutting
                if (!f.IsCellCut(i)) { Assert.Equal(-1f, cells[2 * i + 1]); continue; }
                if (cells[2 * i + 1] == 12.5f) early++;
                else if (cells[2 * i + 1] == 20f) late++;
                else Assert.Fail("unexpected cut time");
            }
            Assert.True(early > 0 && late > 0);
        }

        [Fact]
        public void RegrownCells_AreNoLongerMarkedCut()
        {
            var p = ProgressionTests.NewCampaign();
            p.StartLevel(0);
            p.Field.Clock = 3f;
            p.Field.CutCircle(25f, 25f, 2f);
            p.AddGrass(1_000_000);
            for (int i = 0; i < p.Field.CellCount; i++) Assert.Equal(-1f, p.Field.GetCutTime(i));
        }

        [Fact]
        public void Modifiers_StackAndExpire()
        {
            var m = new TrimmerModifiers();
            m.Add(TrimmerModifierType.SpeedBoost, 0.5f, 10f);
            m.Add(TrimmerModifierType.RadiusBoost, 1f, 5f);
            m.Add(TrimmerModifierType.MultiBlade, 1f, 8f);
            Assert.Equal(1.5f, m.MoveSpeedMultiplier, 3);
            Assert.Equal(2f, m.RadiusMultiplier, 3);
            Assert.Equal(1, m.ExtraBlades);

            m.Tick(6f);
            Assert.Equal(1f, m.RadiusMultiplier, 3);
            Assert.Equal(1.5f, m.MoveSpeedMultiplier, 3);
            m.Tick(5f);
            Assert.Equal(1f, m.MoveSpeedMultiplier, 3);
            Assert.Equal(0, m.ExtraBlades);
            Assert.Equal(0, m.ActiveCount);
        }

        [Fact]
        public void Slow_NeverStopsTheTrimmer_AndBladesAreCapped()
        {
            var m = new TrimmerModifiers();
            m.Add(TrimmerModifierType.Slow, 0.9f, 10f);
            m.Add(TrimmerModifierType.Slow, 0.9f, 10f);
            Assert.Equal(TrimmerModifiers.MinSpeedMultiplier, m.MoveSpeedMultiplier, 3);
            for (int i = 0; i < 10; i++) m.Add(TrimmerModifierType.MultiBlade, 1f, 10f);
            Assert.Equal(TrimmerModifiers.MaxExtraBlades, m.ExtraBlades);
        }

        [Fact]
        public void Router_AppliesModifierGifts_DurationScaledByCombo()
        {
            var p = ProgressionTests.NewCampaign();
            p.StartLevel(0);
            var mods = new TrimmerModifiers();
            var router = new TikTokGiftRouter(p, new[] { new GiftRule("Corgi", GiftActionType.MultiBlade, 1, durationSeconds: 20f) }, mods);

            var r = router.Handle(new TikTokGiftEvent("ann", "Corgi", repeatCount: 3));
            Assert.Equal(GiftOutcome.Applied, r.Outcome);
            Assert.Equal(60f, r.Duration, 3);
            Assert.Equal(1, mods.ExtraBlades);
            Assert.Equal(60f, mods.RemainingSeconds(TrimmerModifierType.MultiBlade), 3);
            Assert.Equal("@ann sent Corgi x3: +1 BLADE 60s", r.Describe());
        }

        [Fact]
        public void Router_ModifierWithoutModifierSet_HasNoEffect()
        {
            var p = ProgressionTests.NewCampaign();
            p.StartLevel(0);
            var router = new TikTokGiftRouter(p, new[] { new GiftRule("Snail", GiftActionType.SlowTrimmer, 0.4) });
            Assert.Equal(GiftOutcome.NoEffect, router.Handle(new TikTokGiftEvent("x", "Snail")).Outcome);
        }
    }
}

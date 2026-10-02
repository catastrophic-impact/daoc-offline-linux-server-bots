using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    public class UT_PlayerTaxiSynchronization
    {
        [TestCase(typeof(GameTaxi), true)]
        [TestCase(typeof(GameBotTaxi), false)]
        [TestCase(typeof(GameNPC), false)]
        public void OnlyHumanStableHorsesEnterNewSynchronization(System.Type mountType, bool expected)
        {
            Assert.That(PlayerTaxiSynchronization.IsPlayerTaxiType(mountType), Is.EqualTo(expected));
        }
        [Test]
        public void FreshPositionPreventsCorrectionAfterDelayedPreviousUpdate()
        {
            Assert.That(PlayerTaxiSynchronization.NeedsCorrection(0, 0, 0, 1500, 0, 0), Is.True);
            Assert.That(PlayerTaxiSynchronization.NeedsCorrection(1490, 0, 0, 1500, 0, 0), Is.False);
        }
        [TestCase(999, false)]
        [TestCase(1000, false)]
        [TestCase(1001, true)]
        public void ExistingDistanceToleranceIsPreserved(int distance, bool expected) =>
            Assert.That(PlayerTaxiSynchronization.NeedsCorrection(distance, 0, 0, 0, 0, 0), Is.EqualTo(expected));
        [Test]
        public void InvalidPositionsCannotBypassCorrection() =>
            Assert.That(PlayerTaxiSynchronization.NeedsCorrection(float.NaN, 0, 0, 0, 0, 0), Is.True);
    }
}

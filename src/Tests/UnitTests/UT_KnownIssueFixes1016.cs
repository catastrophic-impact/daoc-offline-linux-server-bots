using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Known issues #4 (2026-10-16): siege announcement wording and the Mularn bindstone.</summary>
    [TestFixture]
    public class UT_KnownIssueFixes1016
    {
        [TestCase("keep Caer Sursbrooke", "Caer Sursbrooke")]
        [TestCase("relic keep Dun Dagda", "Dun Dagda")]
        [TestCase("Dun Bolg", "Dun Bolg")]
        [TestCase("Keep Caer Erasleigh", "Caer Erasleigh")]
        public void SiegeAnnouncementsUseThePlainKeepName(string objective, string expected) =>
            Assert.That(AutonomousRvrEventLayer.Place(objective), Is.EqualTo(expected));

        [Test]
        public void TeleporterStandOffsKeepClearOfTheMularnBindstone()
        {
            const ushort region = 60_000; // test-only region id
            BotReleaseBindPoints.Replace(region, new[] { new DbBindPoint { X = 804487, Y = 701766, Z = 4956, Region = region } });
            Assert.That(BotReleaseBindPoints.IsNearBindstone(region, 804530, 701780, 4956, AutonomousTownTeleporters.BindstoneClearance), Is.True);
            Assert.That(BotReleaseBindPoints.IsNearBindstone(region, 804813, 701830, 4956, AutonomousTownTeleporters.BindstoneClearance), Is.False);
            Assert.That(BotReleaseBindPoints.IsNearBindstone(region, 804530, 701780, 5500, AutonomousTownTeleporters.BindstoneClearance), Is.False);
        }
    }
}

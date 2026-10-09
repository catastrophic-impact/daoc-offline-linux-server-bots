using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_BotExcludedSpawns
    {
        [Test]
        public void HighRiskShrillerAndMoherSpawnsAreExcludedForBotsOnly()
        {
            Assert.Multiple(() =>
            {
                // Hungry shriller next to the Caillte Garran pollen spores.
                Assert.That(AutonomousAuditedCampPolicy.IsBotExcludedSpawn("c0502019-4ad3-41e7-9cee-eb68fcaba396"), Is.True);
                // Cliffs of Moher bantam spectre 71 units from a level 38-40 grovewood.
                Assert.That(AutonomousAuditedCampPolicy.IsBotExcludedSpawn("1baf88ca-140e-4424-9d0d-bd66be4a2aeb"), Is.True);
                // Koalinth sentinel 206 units from a level 36-38 cliff dweller; ids match case-insensitively.
                Assert.That(AutonomousAuditedCampPolicy.IsBotExcludedSpawn("738DCC64-F1D5-4644-B8F5-B10C3608BC7B"), Is.True);

                // Lower-risk spawns of the same monsters stay valid bot goals.
                Assert.That(AutonomousAuditedCampPolicy.IsBotExcludedSpawn("6e442f6d-704f-4057-9dee-c15eedc92160"), Is.False);
                Assert.That(AutonomousAuditedCampPolicy.IsBotExcludedSpawn("906e344a-5bc3-4124-ba1d-91408c1195c9"), Is.False);
                Assert.That(AutonomousAuditedCampPolicy.IsBotExcludedSpawn("765dcb48-776b-43d4-8e6b-7077d4beec12"), Is.False);
                Assert.That(AutonomousAuditedCampPolicy.IsBotExcludedSpawn(null), Is.False);
                Assert.That(AutonomousAuditedCampPolicy.IsBotExcludedSpawn(string.Empty), Is.False);
            });
        }

        [TestCase("30a66daf-1655-4961-b140-bc8269481fd8")]
        [TestCase("2559e2ef-7151-4cc7-a1bf-b89e1ed5e254")]
        [TestCase("fdad6d7b-3c93-475a-8fba-8adcb7d49440")]
        public void LevelZeroBogOfCullenWormsAreNotBotGoals(string wormSpawnId) =>
            Assert.That(AutonomousAuditedCampPolicy.IsBotExcludedSpawn(wormSpawnId), Is.True);
    }
}

using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Gamebots' realm rank follows their realm points, with the player table.</summary>
    [TestFixture]
    public class UT_BotRealmPointsOct6
    {
        [Test]
        public void RealmLevelFollowsThePlayerTable()
        {
            long[] table = GamePlayer.REALMPOINTS_FOR_LEVEL;
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousBotRealmPointRewards.RealmLevelFor(0), Is.EqualTo(0));
                Assert.That(AutonomousBotRealmPointRewards.RealmLevelFor(table[2]), Is.EqualTo(2));
                Assert.That(AutonomousBotRealmPointRewards.RealmLevelFor(table[10]), Is.EqualTo(10));
                Assert.That(AutonomousBotRealmPointRewards.RealmLevelFor(table[10] - 1), Is.EqualTo(9));
                Assert.That(AutonomousBotRealmPointRewards.RealmLevelFor(long.MaxValue), Is.EqualTo(table.Length - 1));
            });
        }

        [Test]
        public void ABotsShareFollowsThePlayerFormula() =>
            Assert.That(AutonomousBotRealmPointRewards.CalculateRealmPointReward(900, 0, 900, 0, 1, 1, 1.0, true), Is.EqualTo(900));
    }
}

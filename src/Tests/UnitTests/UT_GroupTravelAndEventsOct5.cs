using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Solo camp floor at 50, no repeated realm event, followers catching up.</summary>
    [TestFixture]
    public class UT_GroupTravelAndEventsOct5
    {
        [TestCase(50, 35, ExpectedResult = false, TestName = "Grey at 50")]
        [TestCase(50, 37, ExpectedResult = false, TestName = "Low green at 50")]
        [TestCase(50, 38, ExpectedResult = true, TestName = "Top green at 50")]
        [TestCase(50, 44, ExpectedResult = true, TestName = "Blue at 50")]
        [TestCase(49, 30, ExpectedResult = true, TestName = "Below 50 unchanged")]
        public bool Level50SoloBotsSkipLowMonsters(int botLevel, int monsterLevel) =>
            AutonomousSoloCampFloor.Allows(botLevel, monsterLevel);

        [TestCase(50, 1, 10, ExpectedResult = false, TestName = "One rare spawn among greys")]
        [TestCase(50, 4, 10, ExpectedResult = false, TestName = "Less than half")]
        [TestCase(50, 5, 10, ExpectedResult = true, TestName = "Half of the camp")]
        [TestCase(50, 2, 3, ExpectedResult = false, TestName = "Fewer than three")]
        [TestCase(50, 3, 3, ExpectedResult = true, TestName = "Small full camp")]
        [TestCase(40, 1, 10, ExpectedResult = true, TestName = "Not level 50")]
        public bool Level50SoloCampsNeedEnoughRealTargets(int botLevel, int qualifying, int total) =>
            AutonomousSoloCampFloor.CampQualifies(botLevel, qualifying, total);

        [Test]
        public void ARealmNeverRunsTheSameAutomaticEventTwiceInARow()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousRealmRaid.MayStartAutomatic("dragon-midgard", "dragon-midgard"), Is.False);
                Assert.That(AutonomousRealmRaid.MayStartAutomatic("epic-midgard", "dragon-midgard"), Is.True);
                Assert.That(AutonomousRealmRaid.MayStartAutomatic("epic-midgard", null), Is.True, "first event of the run");
            });
        }

        [TestCase(0f, 1f)]
        [TestCase(120f, 1f)]
        [TestCase(570f, 1.125f)]
        [TestCase(1020f, 1.25f)]
        [TestCase(5000f, 1.25f)]
        public void FollowersBehindTheirFormationSpotRunFasterToCatchUp(float distance, float factor) =>
            Assert.That(AutonomousGroupPace.CatchUpFactor(distance), Is.EqualTo(factor).Within(0.001f));
    }
}

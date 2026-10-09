using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>A far or stale member fight no longer freezes the whole group.</summary>
    [TestFixture]
    public class UT_GroupCombatReachOct5
    {
        [TestCase(true, false, 0L, 100_000L, ExpectedResult = true, TestName = "Real combat always counts")]
        [TestCase(false, false, 0L, 100_000L, ExpectedResult = false, TestName = "No fight")]
        [TestCase(false, true, 50_000L, 100_000L, ExpectedResult = true, TestName = "Fresh aggro counts")]
        [TestCase(false, true, 40_000L, 100_000L, ExpectedResult = false, TestName = "60 s of aggro without a blow stops counting")]
        public bool OnlyRealOrRecentFightsHoldTheGroup(bool realCombat, bool engaged, long since, long now) =>
            AutonomousGroupCombat.CountsAsFighting(realCombat, engaged, since, now);

        [Test]
        public void HelpReachesBeyondAssistRange()
        {
            Assert.That(AutonomousGroupCombat.HelpRadius, Is.GreaterThan(DOL.AI.Brain.BotBrain.GROUP_DEFENSE_ASSIST_RADIUS));
        }
    }
}

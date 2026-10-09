using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_CompanionFrontierAggro
    {
        [Test]
        public void AggressiveReachesFurtherThanDefensiveAndBothFarBeyondPveDefensive()
        {
            int aggressive = CompanionPvpEngagement.FrontierRaidAggroRadius(false);
            int defensive = CompanionPvpEngagement.FrontierRaidAggroRadius(true);
            Assert.That(aggressive, Is.GreaterThan(defensive));
            Assert.That(defensive, Is.GreaterThanOrEqualTo(CompanionEngagementMode.DefensiveRadius * 2));
            // Never beyond the assist radius the raid uses, and inside the recall leash.
            Assert.That(aggressive, Is.LessThanOrEqualTo(DOL.AI.Brain.BotBrain.GROUP_DEFENSE_ASSIST_RADIUS));
            Assert.That(aggressive, Is.LessThan(TemporaryCompanionRecovery.MaximumLeaderDistance));
        }

        [Test]
        public void NoRaidMeansNoFrontierAggro()
        {
            Assert.That(CompanionPvpEngagement.FrontierRaidAggro(null), Is.False);
        }
    }
}

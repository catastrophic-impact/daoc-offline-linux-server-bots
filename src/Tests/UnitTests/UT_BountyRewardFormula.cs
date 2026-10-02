using DOL.GS;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public class UT_BountyRewardFormula
{
    [Test]
    public void KillRequirementGrowsFromFiveToFiftyBeforeEpicLevel()
    {
        Assert.That(BountyQuest.RequiredKillsForLevel(1), Is.EqualTo(5));
        Assert.That(BountyQuest.RequiredKillsForLevel(49), Is.EqualTo(50));
        Assert.That(BountyQuest.RequiredKillsForLevel(50), Is.EqualTo(1));
        for (int level = 2; level < 50; level++)
            Assert.That(BountyQuest.RequiredKillsForLevel(level),
                Is.GreaterThanOrEqualTo(BountyQuest.RequiredKillsForLevel(level - 1)));
    }

    [TestCase(1, false, 1.0, 10L)]
    [TestCase(1, true, 1.0, 5L)]
    [TestCase(1, false, 3.0, 30L)]
    [TestCase(1, false, 5.0, 50L)]
    [TestCase(1, false, 10.0, 100L)]
    [TestCase(49, false, 1.0, 6_400_000_000L)]
    [TestCase(49, true, 1.0, 3_200_000_000L)]
    [TestCase(49, false, 3.0, 19_200_000_000L)]
    [TestCase(50, false, 10.0, 0L)]
    public void RewardUsesAssignedLevelBulbAndServerRateExactlyOnce(
        int level, bool rerolled, double rate, long expected)
    {
        Assert.That(BountyRewardService.CalculateExperienceReward((byte)level, rerolled, rate),
            Is.EqualTo(expected));
    }
}

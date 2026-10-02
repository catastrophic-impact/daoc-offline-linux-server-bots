using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousAiBudget
{
    [Test]
    public void CombatThinksFasterThanTravelAndPlanning()
    {
        Assert.That(AutonomousAiBudget.IntervalMilliseconds(eAutonomousThinkMode.Combat),
            Is.LessThan(AutonomousAiBudget.IntervalMilliseconds(eAutonomousThinkMode.Travel)));
        Assert.That(AutonomousAiBudget.IntervalMilliseconds(eAutonomousThinkMode.Travel),
            Is.LessThan(AutonomousAiBudget.IntervalMilliseconds(eAutonomousThinkMode.Planning)));
    }

    [Test]
    public void StableOffsetsSpreadBotsWithinTheirInterval()
    {
        int first = AutonomousAiBudget.StableOffsetMilliseconds(1, eAutonomousThinkMode.Planning);
        int second = AutonomousAiBudget.StableOffsetMilliseconds(2, eAutonomousThinkMode.Planning);
        Assert.That(first, Is.Not.EqualTo(second));
        Assert.That(first, Is.InRange(0, 2499));
    }

    [Test]
    public void ExhaustedFrameDefersWorkWithoutSimulatingIt()
    {
        Assert.That(AutonomousAiBudget.MaximumDecisionsThisTick(5, 5, 100), Is.Zero);
        Assert.That(AutonomousAiBudget.MaximumDecisionsThisTick(2, 5, 100), Is.GreaterThan(0));
    }
}

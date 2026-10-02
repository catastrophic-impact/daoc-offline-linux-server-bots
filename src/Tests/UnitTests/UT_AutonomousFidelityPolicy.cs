using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousFidelityPolicy
{
    [Test]
    public void NearbyHumanGetsHighestFidelity()
    {
        Assert.That(AutonomousFidelityPolicy.Select(true, true, 2500), Is.EqualTo(eAutonomousFidelity.NearbyHuman));
        Assert.That(AutonomousFidelityPolicy.Select(true, false, 2500), Is.EqualTo(eAutonomousFidelity.Efficient));
    }

    [Test]
    public void RemoteCombatStillRunsFrequentlyEnoughToProgress()
    {
        int remote = AutonomousFidelityPolicy.IntervalMilliseconds(eAutonomousThinkMode.Combat, eAutonomousFidelity.Efficient);
        Assert.That(remote, Is.LessThanOrEqualTo(500));
        Assert.That(AutonomousFidelityPolicy.CandidateLimit(eAutonomousFidelity.Efficient), Is.GreaterThan(0));
    }

    [Test]
    public void RemotePlanningIsCheaperThanNearbyPlanning()
    {
        int nearby = AutonomousFidelityPolicy.IntervalMilliseconds(eAutonomousThinkMode.Planning, eAutonomousFidelity.NearbyHuman);
        int remote = AutonomousFidelityPolicy.IntervalMilliseconds(eAutonomousThinkMode.Planning, eAutonomousFidelity.Efficient);
        Assert.That(remote, Is.GreaterThan(nearby));
    }

    [Test]
    public void PopulationPressureNeverSlowsCombatReaction()
    {
        int ordinary = AutonomousFidelityPolicy.IntervalMilliseconds(
            eAutonomousThinkMode.Combat, eAutonomousFidelity.Efficient, 300);
        int targetPopulation = AutonomousFidelityPolicy.IntervalMilliseconds(
            eAutonomousThinkMode.Combat, eAutonomousFidelity.Efficient, 1_500);

        Assert.That(targetPopulation, Is.EqualTo(ordinary));
        Assert.That(targetPopulation, Is.LessThanOrEqualTo(500));
    }

    [Test]
    public void TargetPopulationPlanningRemainsBoundedAndStaggered()
    {
        int interval = AutonomousFidelityPolicy.IntervalMilliseconds(
            eAutonomousThinkMode.Planning, eAutonomousFidelity.Efficient, 1_500);
        int first = AutonomousFidelityPolicy.Stagger(interval, 101);
        int second = AutonomousFidelityPolicy.Stagger(interval, 102);

        Assert.Multiple(() =>
        {
            Assert.That(interval, Is.LessThanOrEqualTo(12_000));
            Assert.That(first, Is.GreaterThanOrEqualTo(interval));
            Assert.That(first, Is.LessThan(interval + interval / 8));
            Assert.That(second, Is.Not.EqualTo(first));
        });
    }
}

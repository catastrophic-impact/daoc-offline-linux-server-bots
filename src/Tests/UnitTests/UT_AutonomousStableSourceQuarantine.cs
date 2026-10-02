using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public sealed class UT_AutonomousStableSourceQuarantine
{
    [Test]
    public void TimeAloneCannotReenableAnotherMasterFromTheSameSource()
    {
        var quarantine = new AutonomousStableSourceQuarantine();
        quarantine.Mark(1, 2, new(1000, 1000, 100));

        Assert.That(quarantine.IsActive(1, 2, new(1000, 1000, 100)), Is.True);
        Assert.That(quarantine.IsActive(1, 2, new(1500, 1000, 100)), Is.True);
        Assert.That(quarantine.IsActive(1, 2, new(2201, 1000, 100)), Is.False);
    }

    [Test]
    public void RegionZoneAndSuccessfulBoardingClearTheQuarantine()
    {
        var quarantine = new AutonomousStableSourceQuarantine();
        quarantine.Mark(1, 2, Vector3.Zero);
        Assert.That(quarantine.IsActive(1, 3, Vector3.Zero), Is.False);

        quarantine.Mark(1, 2, Vector3.Zero);
        Assert.That(quarantine.IsActive(2, 2, Vector3.Zero), Is.False);

        quarantine.Mark(1, 2, Vector3.Zero);
        quarantine.Clear();
        Assert.That(quarantine.IsActive(1, 2, Vector3.Zero), Is.False);
    }
}

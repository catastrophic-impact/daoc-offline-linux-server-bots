using System;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousStuckWatchdog
{
    [Test]
    public void RecoversAfterFifteenMinutesWithoutMovement()
    {
        DateTime now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        Assert.That(AutonomousStuckWatchdog.ShouldRecoverMovement(now.AddMinutes(-14.9), now, true, false, false), Is.False);
        Assert.That(AutonomousStuckWatchdog.ShouldRecoverMovement(now.AddMinutes(-15), now, true, false, false), Is.True);
        Assert.That(AutonomousStuckWatchdog.ShouldRecoverMovement(now.AddHours(-1), now, true, true, false), Is.False);
        Assert.That(AutonomousStuckWatchdog.ShouldRecoverMovement(now.AddHours(-1), now, false, false, false), Is.False);
    }

    [Test]
    public void StablemasterHorseTravelNeverCountsAsStuckTime()
    {
        DateTime now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        Assert.That(AutonomousStuckWatchdog.ShouldRecoverMovement(now.AddHours(-3), now, true, false, true), Is.False);
    }

    [TestCase("Formed up at rendezvous")]
    [TestCase("Holding group formation")]
    [TestCase("formed up at RENDEZVOUS")]
    public void LegitimatePartialGroupFormationIsRecognizedAsAStationaryHold(string activity)
    {
        Assert.That(AutonomousStuckWatchdog.IsGroupFormationHold(activity), Is.True);
        Assert.That(AutonomousStuckWatchdog.IsGroupFormationHold("Traveling to group rendezvous"), Is.False);
    }

    [Test]
    public void GoalRecoveryIgnoresMovementAndTriggersAtFortyFiveMinutes()
    {
        DateTime now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        Assert.That(AutonomousStuckWatchdog.ShouldRecoverGoal(now.AddMinutes(-44.9), now, true, false, false), Is.False);
        Assert.That(AutonomousStuckWatchdog.ShouldRecoverGoal(now.AddMinutes(-45), now, true, false, false), Is.True);
        Assert.That(AutonomousStuckWatchdog.ShouldRecoverGoal(now.AddHours(-2), now, true, true, false), Is.False);
        Assert.That(AutonomousStuckWatchdog.ShouldRecoverGoal(now.AddHours(-2), now, true, false, true), Is.False);
    }

    [Test]
    public void RvrRoamingIsExcludedFromThePveGoalStallClock()
    {
        DateTime now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        Assert.That(AutonomousStuckWatchdog.ShouldRecoverGoal(
            now.AddHours(-3), now, true, false, false, true), Is.False);
        Assert.That(AutonomousStuckWatchdog.ShouldRecoverGoal(
            now.AddMinutes(-45), now, true, false, false, false), Is.True);
    }

    [Test]
    public void OnlyCompletedOutcomesResetTheFortyFiveMinuteClock()
    {
        Assert.That(AutonomousStuckWatchdog.CountsAsGoalProgress(eAutonomousProgressKind.Experience), Is.True);
        Assert.That(AutonomousStuckWatchdog.CountsAsGoalProgress(eAutonomousProgressKind.SiegeParticipation), Is.True);
        Assert.That(AutonomousStuckWatchdog.CountsAsGoalProgress(eAutonomousProgressKind.Loot), Is.True);
        Assert.That(AutonomousStuckWatchdog.CountsAsGoalProgress(eAutonomousProgressKind.Training), Is.True);
        Assert.That(AutonomousStuckWatchdog.CountsAsGoalProgress(eAutonomousProgressKind.Movement), Is.False);
        Assert.That(AutonomousStuckWatchdog.CountsAsGoalProgress(eAutonomousProgressKind.Combat), Is.True);
        Assert.That(AutonomousStuckWatchdog.CountsAsGoalProgress(eAutonomousProgressKind.Objective), Is.False);
        Assert.That(AutonomousStuckWatchdog.CountsAsGoalProgress(eAutonomousProgressKind.StableTravel), Is.False);
    }

    [TestCase(eRealm.Albion, "Camelot", 10)]
    [TestCase(eRealm.Midgard, "Jordheim", 101)]
    [TestCase(eRealm.Hibernia, "Tir na Nog", 201)]
    public void RecoveryAlwaysUsesTheBotsOwnCapital(eRealm realm, string name, int regionId)
    {
        AutonomousStuckWatchdog.CapitalLocation capital = AutonomousStuckWatchdog.CapitalFor(realm);
        Assert.That(capital.Name, Is.EqualTo(name));
        Assert.That(capital.RegionId, Is.EqualTo(regionId));
    }

    [Test]
    public void RecoveryGeneratesANonEmptyFreshLiveGoal()
    {
        string first = AutonomousStuckWatchdog.GenerateFreshGoal(7, 1);
        string second = AutonomousStuckWatchdog.GenerateFreshGoal(7, 2);
        Assert.That(first, Is.Not.Empty);
        Assert.That(second, Is.Not.EqualTo(first));
        Assert.That(first, Does.Contain("XP").Or.Contain("training").Or.Contain("group"));
    }

    [Test]
    public void RepeatedCampChangesWithinAssignmentCannotPostponeGoalRecovery()
    {
        DateTime now = new(2026, 8, 30, 22, 0, 0, DateTimeKind.Utc);
        DateTime last = now.AddMinutes(-45);
        Assert.That(AutonomousStuckWatchdog.GoalClockForAssignment(last, "pve-123", "pve-123", now), Is.EqualTo(last));
        Assert.That(AutonomousStuckWatchdog.GoalClockForAssignment(last, "pve-123", "pve-124", now), Is.EqualTo(now));
        Assert.That(AutonomousStuckWatchdog.GoalClockForAssignment(last, null, "", now), Is.EqualTo(last));
    }
}

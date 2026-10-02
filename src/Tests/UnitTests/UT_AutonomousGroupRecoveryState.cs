using System;
using NUnit.Framework;
using Member = DOL.GS.AutonomousGroupRecoveryState.Member;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousGroupRecoveryState
{
    private static Member[] Ready() => [new(1, 0, true), new(2, 0, true), new(3, 0, true)];

    [TestCase(false)]
    [TestCase(true)]
    public void PartialAndFullWipesStartOneRecoveryEpisode(bool fullWipe)
    {
        var recovery = new AutonomousGroupRecoveryState();
        Member[] members = Ready();
        recovery.Observe(members, false);
        members[0] = members[0] with { Alive = false, Deaths = 1 };
        if (fullWipe)
            for (int i = 1; i < members.Length; i++) members[i] = members[i] with { Alive = false, Deaths = 1 };
        Assert.That(recovery.HasCasualty(members), Is.True);
        Assert.That(recovery.Observe(members, true), Is.True);
        Assert.That(recovery.Observe(members, true), Is.False, "No repeated penalty/log for the same recovery episode");
        Assert.That(recovery.TryComplete(members, 9_000_000), Is.False);
        Assert.That(recovery.IsRegrouping, Is.True, "A wipe does not vote to disband");
    }

    [Test]
    public void DeathFollowedByResurrectionBetweenPulsesStillTriggersRecovery()
    {
        var recovery = new AutonomousGroupRecoveryState();
        Member[] members = Ready();
        recovery.Observe(members, false);
        members[1] = members[1] with { Deaths = 1 };
        Assert.That(recovery.HasCasualty(members), Is.True);
        Assert.That(recovery.Observe(members, true), Is.True);
    }

    [Test]
    public void InitialAssemblyDoesNotStartARecoveryTaskOrClock()
    {
        var recovery = new AutonomousGroupRecoveryState();
        Member[] members = Ready();
        members[1] = members[1] with { Alive = false };
        Assert.That(recovery.Observe(members, false), Is.False);
        Assert.That(recovery.IsRegrouping, Is.False);
    }

    [TestCase("dead")]
    [TestCase("returning")]
    [TestCase("riding")]
    [TestCase("distant")]
    [TestCase("combat")]
    [TestCase("resources")]
    public void EveryMemberMustBeRecoveredBeforeDeparture(string obstacle)
    {
        var recovery = new AutonomousGroupRecoveryState();
        Member[] members = Ready();
        members[0] = members[0] with { Alive = false };
        recovery.Observe(members, true);
        members = Ready();
        members[1] = obstacle switch
        {
            "dead" => members[1] with { Alive = false },
            "returning" => members[1] with { Returning = true },
            "riding" => members[1] with { Riding = true },
            "distant" => members[1] with { AtRendezvous = false },
            "combat" => members[1] with { Busy = true },
            _ => members[1] with { ResourcesReady = false },
        };
        Assert.That(recovery.TryComplete(members, 0), Is.False);
        Assert.That(recovery.TryComplete(members, 60_000), Is.False);
        members = Ready();
        Assert.That(recovery.TryComplete(members, 61_000), Is.False);
        Assert.That(recovery.TryComplete(members, 65_999), Is.False);
        Assert.That(recovery.TryComplete(members, 66_000), Is.True);
        Assert.That(recovery.IsRegrouping, Is.False);
    }

    [Test]
    public void NewCombatResetsSettlingAndLaterCasualtyStartsANewEpisode()
    {
        var recovery = new AutonomousGroupRecoveryState();
        Member[] members = Ready();
        members[0] = members[0] with { Alive = false, Deaths = 1 };
        recovery.Observe(members, true);
        members[0] = members[0] with { Alive = true };
        recovery.TryComplete(members, 0);
        members[2] = members[2] with { Busy = true };
        Assert.That(recovery.TryComplete(members, 4_000), Is.False);
        members[2] = members[2] with { Busy = false };
        Assert.That(recovery.TryComplete(members, 6_000), Is.False);
        Assert.That(recovery.TryComplete(members, 11_000), Is.True);
        Assert.That(recovery.Observe(members, true), Is.False);
        members[0] = members[0] with { Deaths = 2 };
        Assert.That(recovery.Observe(members, true), Is.True);
    }

    [TestCase(eAutonomousObjectiveKind.GroupPve)]
    [TestCase(eAutonomousObjectiveKind.RvR)]
    public void SharedTaskStillExpiresWhileTheWholeGroupIsRecovering(eAutonomousObjectiveKind kind)
    {
        var recovery = new AutonomousGroupRecoveryState();
        var clock = new AutonomousGroupTaskClock(kind, new Random(7));
        clock.Start(100, DateTime.UtcNow);
        long deadline = clock.DeadlineTick.Value;
        Member[] members = [new(1, 1, false), new(2, 1, false)];
        recovery.Observe(members, clock.HasStarted);
        Assert.That(recovery.TryComplete(members, deadline), Is.False);
        Assert.That(clock.Start(deadline, DateTime.UtcNow), Is.False);
        Assert.That(clock.DeadlineTick, Is.EqualTo(deadline));
        Assert.That(clock.HasExpired(deadline), Is.True);
        Assert.That(clock.RemainingMilliseconds(deadline), Is.Zero);
    }

    [Test]
    public void AnotherDeathDuringRecoveryRestartsSettlingEvenIfAlreadyResurrected()
    {
        var recovery = new AutonomousGroupRecoveryState();
        Member[] members = Ready();
        members[0] = members[0] with { Alive = false, Deaths = 1 };
        recovery.Observe(members, true);
        members[0] = members[0] with { Alive = true };
        recovery.TryComplete(members, 0);
        members[1] = members[1] with { Deaths = 1 };
        Assert.That(recovery.Observe(members, true), Is.False);
        Assert.That(recovery.TryComplete(members, 6_000), Is.False);
        Assert.That(recovery.TryComplete(members, 11_000), Is.True);
    }

    [Test]
    public void DisconnectedMemberLeavingDoesNotRestartTheRemainingGroupsTask()
    {
        var recovery = new AutonomousGroupRecoveryState();
        var clock = new AutonomousGroupTaskClock(eAutonomousObjectiveKind.GroupPve, new Random(7));
        clock.Start(100, DateTime.UtcNow);
        long deadline = clock.DeadlineTick.Value;
        Member[] original = Ready();
        recovery.Observe(original, true);
        original[2] = original[2] with { Deaths = 1, Returning = true,
            AtRendezvous = false };
        Assert.That(recovery.Observe(original, true), Is.True);
        Assert.That(recovery.TryComplete(original, 1_000), Is.False);

        // Coordinator may remove an unreachable member only after its bounded
        // route retry and viable-composition check. Recovery then settles the
        // remaining roster without reviving the old clock or stale member.
        Member[] remaining = [original[0], original[1]];
        Assert.That(recovery.TryComplete(remaining, 1_000), Is.False);
        Assert.That(recovery.TryComplete(remaining, 6_000), Is.True);
        Assert.That(clock.DeadlineTick, Is.EqualTo(deadline));
        Assert.That(clock.Start(6_000, DateTime.UtcNow), Is.False);
    }

    [TestCase(90, 80, 80, true, true)]
    [TestCase(89, 100, 100, true, false)]
    [TestCase(100, 79, 100, true, false)]
    [TestCase(100, 100, 79, true, false)]
    [TestCase(90, 0, 80, false, true)]
    public void RecoveryUsesRealClassResources(int health, int power, int endurance, bool caster, bool ready) =>
        Assert.That(AutonomousGroupRecoveryState.ResourcesReady(health, power, endurance, caster), Is.EqualTo(ready));

    private sealed class FixedRoll(double roll) : Random
    {
        public override double NextDouble() => roll;
    }

    [TestCase(0, eAutonomousObjectiveKind.SoloPve)]
    [TestCase(.199999, eAutonomousObjectiveKind.SoloPve)]
    [TestCase(.20, eAutonomousObjectiveKind.GroupPve)]
    [TestCase(.599999, eAutonomousObjectiveKind.GroupPve)]
    [TestCase(.60, eAutonomousObjectiveKind.RvR)]
    [TestCase(.999999, eAutonomousObjectiveKind.RvR)]
    public void LevelFiftyRollUsesTwentyFortyForty(double roll, eAutonomousObjectiveKind expected) =>
        Assert.That(AutonomousObjectiveAssignments.RollLevelFiftyObjective(new FixedRoll(roll)), Is.EqualTo(expected));
}

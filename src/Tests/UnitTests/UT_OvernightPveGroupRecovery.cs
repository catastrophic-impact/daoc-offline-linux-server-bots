using DOL.GS;
using System.Numerics;
using NUnit.Framework;
using Member = DOL.GS.AutonomousGroupRecoveryState.Member;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_OvernightPveGroupRecovery
{
    [Test]
    public void MatchmakingSkipsActiveSoloTravelCombatAndLongCommutes()
    {
        Assert.That(AutonomousBotGroupCoordinator.CanRecruitPveMember(false, false, false, false, false, 30_000), Is.True);
        Assert.That(AutonomousBotGroupCoordinator.CanRecruitPveMember(false, false, false, false, false, 30_001), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.CanRecruitPveMember(true, false, false, false, false, 0), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.CanRecruitPveMember(false, true, false, false, false, 0), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.CanRecruitPveMember(false, false, true, false, false, 0), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.CanRecruitPveMember(false, false, false, true, false, 0), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.CanRecruitPveMember(false, false, false, false, true, 0), Is.False);
    }

    [Test]
    public void ReducedPartyCanFightOnlyWithItsLockedViableCore()
    {
        Assert.That(AutonomousBotGroupCoordinator.CanUsePveCombatRoster(8, 8, 2, 2, 3), Is.True);
        Assert.That(AutonomousBotGroupCoordinator.CanUsePveCombatRoster(7, 7, 1, 1, 3), Is.True);
        Assert.That(AutonomousBotGroupCoordinator.CanUsePveCombatRoster(8, 7, 1, 1, 3), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.CanUsePveCombatRoster(7, 7, 0, 1, 3), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.CanUsePveCombatRoster(3, 3, 1, 1, 1), Is.True);
        Assert.That(AutonomousBotGroupCoordinator.CanUsePveCombatRoster(2, 2, 1, 1, 0), Is.False);
    }

    [Test]
    public void NearbyResurrectionResumesTheExistingCampWithoutARegroupEpisode()
    {
        var recovery = new AutonomousGroupRecoveryState();
        Member[] members = [new(1, 0, true), new(2, 0, true), new(3, 0, true)];
        recovery.Observe(members, false);
        members[1] = members[1] with { Alive = false, Deaths = 1 };
        members[1] = members[1] with { Alive = true };
        Assert.That(recovery.TryResumeLocally(members, together: true), Is.True);
        Assert.That(recovery.Observe(members, taskStarted: true), Is.False);
        Assert.That(recovery.IsRegrouping, Is.False);
    }

    [Test]
    public void ANearbyRevivalAcrossAWallDoesNotCountAsLocalReunion()
    {
        Assert.That(AutonomousBotGroupCoordinator.CanCountLocalRevival(20, true, true, true, false),
            Is.False, "proximity cannot override a disconnected corridor");
        Assert.That(AutonomousBotGroupCoordinator.CanCountLocalRevival(20, true, true, false, true),
            Is.False, "missing navmesh proof must use normal regroup");
        Assert.That(AutonomousBotGroupCoordinator.CanCountLocalRevival(200, true, false, true, true), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.CanCountLocalRevival(1_801, true, true, true, true), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.CanCountLocalRevival(1_800, true, true, true, true), Is.True);
    }

    [Test]
    public void PartialPathNearAnOppositeRoomIsNotProofOfReunion()
    {
        Vector3 start = new(100, 100, 0), end = new(120, 100, 0);
        WrappedPathfindingNode[] nodes = [
            new(start, (EDtPolyFlags)0), new(new Vector3(118, 100, 0), (EDtPolyFlags)0)];
        Assert.That(AutonomousBotGroupCoordinator.IsCompleteLocalReunionSegment(
            new(PathfindingStatus.PartialPathFound, 2), nodes, start, end), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.IsCompleteLocalReunionSegment(
            new(PathfindingStatus.PathFound, 2), nodes, start, end), Is.True);
    }

    [Test]
    public void DistantOrReleasedCasualtyStillStartsRegroup()
    {
        var recovery = new AutonomousGroupRecoveryState();
        Member[] members = [new(1, 0, true), new(2, 0, true), new(3, 0, true)];
        recovery.Observe(members, false);
        members[1] = members[1] with { Deaths = 1, Returning = true };
        Assert.That(recovery.TryResumeLocally(members, together: true), Is.False);
        Assert.That(recovery.Observe(members, taskStarted: true), Is.True);
        Assert.That(recovery.IsRegrouping, Is.True);
    }

    [Test]
    public void AnotherCasualtyDuringRegroupReturnsToRegroupPhase()
    {
        Assert.That(AutonomousBotGroupCoordinator.PhaseAfterCasualty("Regrouping", true),
            Is.EqualTo("Regrouping"));
        Assert.That(AutonomousBotGroupCoordinator.PhaseAfterCasualty("Grinding", false),
            Is.EqualTo("Grinding"));
    }

    [Test]
    public void RegroupTravelTakesPriorityOverBetweenPullRecovery()
    {
        Assert.That(AutonomousBotGroupCoordinator.ShouldPauseForGroupPullRecovery("Regrouping", false, true),
            Is.False, "A survivor must leave the dungeon to rejoin released members");
        Assert.That(AutonomousBotGroupCoordinator.ShouldPauseForGroupPullRecovery("Regrouping", true, true),
            Is.False, "Local defensive combat is handled before regroup routing");
        Assert.That(AutonomousBotGroupCoordinator.ShouldPauseForGroupPullRecovery("Grinding", false, true),
            Is.True, "Ordinary between-pull recovery still protects the active camp");
        Assert.That(AutonomousBotGroupCoordinator.ShouldPauseForGroupPullRecovery("Grinding", true, false),
            Is.True, "Group combat still pauses a non-engaged member at the active camp");
        Assert.That(AutonomousBotGroupCoordinator.ShouldPauseForGroupPullRecovery("Waiting for resurrection", false, true),
            Is.True, "A nearby corpse still gets the normal resurrection window");
        Assert.That(AutonomousBotGroupCoordinator.ShouldPauseForGroupPullRecovery("Meeting up", false, true),
            Is.False, "Initial assembly still uses its normal travel route");
    }
}

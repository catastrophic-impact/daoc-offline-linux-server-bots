using System;
using System.Numerics;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousRouteRecoveryPolicy
{
    private sealed class RecoveryMesh : PathfindingMgrBase
    {
        public Vector3 Goal;
        public Vector3 GoalEndpoint;
        public PathfindingStatus GoalStatus = PathfindingStatus.PathFound;
        public Vector3 LastQueryTarget;
        public int Queries;

        public override bool IsAvailable => true;
        public override bool HasNavmesh(Zone zone) => true;

        public override PathfindingResult GetPathStraight(Zone zone, Vector3 from, Vector3 to,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> nodes)
        {
            Queries++;
            LastQueryTarget = to;
            nodes[0] = new(from, EDtPolyFlags.Walk);
            if (to != Goal)
                return new(PathfindingStatus.PartialPathFound, 1);
            nodes[1] = new(GoalEndpoint, EDtPolyFlags.Walk);
            return new(GoalStatus, 2);
        }
    }

    private static Zone TestZone(ushort id) =>
        new(null, id, "recovery test", 0, 0, 65536, 65536, id, false, 0, false, 0, 0, 0, 0, 0);

    [Test]
    public void FailedShortCornerUsesExactNativePathToRealGoalForSideStep()
    {
        Zone zone = TestZone(2);
        Vector3 current = new(378829, 497501, 5434);
        Vector3 goal = new(380326, 493976, 5070);
        Vector3 failedCorner = new(378793, 497461, 5433);
        var mesh = new RecoveryMesh { Goal = goal, GoalEndpoint = goal };
        Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[2];

        Assert.That(mesh.GetPathStraight(zone, current, failedCorner, mesh.DefaultFilters, nodes).Status,
            Is.EqualTo(PathfindingStatus.PartialPathFound));
        Assert.That(AutonomousCorridorRecovery.ShouldBypassFailedCorner(mesh, zone, zone,
            current, goal, failedCorner, failedCorner), Is.True);
        Assert.That(mesh.LastQueryTarget, Is.EqualTo(goal), "the failed corner must not replace the camp goal");
        Assert.That(AutonomousCorridorRecovery.IsOutsideFailedCorner(failedCorner, failedCorner), Is.False);
        Assert.That(AutonomousCorridorRecovery.IsOutsideFailedCorner(
            failedCorner + new Vector3(40, 0, 1000), failedCorner), Is.False,
            "a height difference cannot reuse the same blocked XY pocket");
        Assert.That(AutonomousCorridorRecovery.IsOutsideFailedCorner(
            failedCorner + new Vector3(120, 0, 0), failedCorner), Is.True);
    }

    [Test]
    public void CornerBypassRequiresTheActiveFailedOrderAndAFullSameZoneGoalPath()
    {
        Zone zone = TestZone(2);
        Zone otherZone = TestZone(3);
        Vector3 current = new(380006, 498099, 5338);
        Vector3 goal = new(376188, 494993, 5121);
        Vector3 failedCorner = new(380046, 498156, 5328);
        var mesh = new RecoveryMesh { Goal = goal, GoalEndpoint = goal };

        Assert.That(AutonomousCorridorRecovery.ShouldBypassFailedCorner(mesh, zone, otherZone,
            current, goal, failedCorner, failedCorner), Is.False, "cross-zone seams keep their old recovery target");
        Assert.That(AutonomousCorridorRecovery.ShouldBypassFailedCorner(mesh, zone, zone,
            current, goal, null, failedCorner), Is.False, "ordinary route failures have no failed recovery corner");
        Assert.That(AutonomousCorridorRecovery.ShouldBypassFailedCorner(mesh, zone, zone,
            current, goal, failedCorner, goal), Is.False, "only failure of the active corner qualifies");
        Vector3 nearbyGoal = failedCorner + new Vector3(80, 0, 0);
        Assert.That(AutonomousCorridorRecovery.ShouldBypassFailedCorner(mesh, zone, zone,
            current, nearbyGoal, failedCorner, failedCorner), Is.False,
            "a near-goal intermediate remains on the ordinary arrival path");
        Assert.That(mesh.Queries, Is.Zero, "unrelated failures must not add a native path query");

        mesh.GoalStatus = PathfindingStatus.PartialPathFound;
        Assert.That(AutonomousCorridorRecovery.ShouldBypassFailedCorner(mesh, zone, zone,
            current, goal, failedCorner, failedCorner), Is.False);
        mesh.GoalStatus = PathfindingStatus.PathFound;
        mesh.GoalEndpoint = goal + new Vector3(100, 0, 0);
        Assert.That(AutonomousCorridorRecovery.ShouldBypassFailedCorner(mesh, zone, zone,
            current, goal, failedCorner, failedCorner), Is.False,
            "a full status without an endpoint at the goal cannot bypass the corner");
    }

    [Test]
    public void SideStepAloneDoesNotResetTheRecoveryBudget()
    {
        Assert.That(AutonomousRouteRecoveryPolicy.HasMeaningfulForwardProgress(5_000, 4_900), Is.False);
        Assert.That(AutonomousRouteRecoveryPolicy.HasMeaningfulForwardProgress(5_000, 4_760), Is.True);
    }

    [Test]
    public void ExactlyThreeLocalRecoveriesAreAllowedBeforeGoalSwap()
    {
        Assert.That(AutonomousRouteRecoveryPolicy.ShouldAbandon(1), Is.False);
        Assert.That(AutonomousRouteRecoveryPolicy.ShouldAbandon(3), Is.False);
        Assert.That(AutonomousRouteRecoveryPolicy.ShouldAbandon(4), Is.True);
    }

    [Test]
    public void ContinuousMovementKeepsOwnershipOfTheExistingOrder()
    {
        Assert.That(AutonomousRouteRecoveryPolicy.ShouldRetainMovementOrder(true, 50_000, 0), Is.True);
        Assert.That(AutonomousRouteRecoveryPolicy.ShouldRetainMovementOrder(false, 1_000, 1_500), Is.True);
        Assert.That(AutonomousRouteRecoveryPolicy.ShouldRetainMovementOrder(false, 1_500, 1_500), Is.False);
        Assert.That(AutonomousRouteRecoveryPolicy.ShouldRecoverActiveRouteStall(1_000, 6_000, 5_000), Is.True,
            "a collision-stopped route must enter bounded recovery even when IsMoving is false");
        Assert.That(AutonomousRouteRecoveryPolicy.ShouldRecoverActiveRouteStall(1_000, 5_999, 5_000), Is.False);
    }

    [Test]
    public void ImmediateRouteFailureGetsAQuietCooldownBeforeAnotherGoal()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRouteRecoveryPolicy.ReplanDelayMilliseconds(false, 17),
                Is.EqualTo(AutonomousRouteRecoveryPolicy.ImmediateRouteFailureCooldownMilliseconds + 17));
            Assert.That(AutonomousRouteRecoveryPolicy.ReplanDelayMilliseconds(true, 17), Is.EqualTo(2_517));
        });
    }

    [Test]
    public void SafeRelocationRequiresRepeatedTerminalFailuresInTheSamePocket()
    {
        var origin = new System.Numerics.Vector3(1000, 1000, 100);
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRouteRecoveryPolicy.IsSameRepeatedFailurePocket(
                1, 1, origin, new(1200, 1100, 900), 30_000), Is.True,
                "Z differences do not hide an isolated XY navigation component");
            Assert.That(AutonomousRouteRecoveryPolicy.IsSameRepeatedFailurePocket(
                1, 1, origin, new(2100, 1000, 100), 30_000), Is.False);
            Assert.That(AutonomousRouteRecoveryPolicy.IsSameRepeatedFailurePocket(
                1, 200, origin, origin, 30_000), Is.False);
            Assert.That(AutonomousRouteRecoveryPolicy.IsSameRepeatedFailurePocket(
                1, 1, origin, origin, AutonomousRouteRecoveryPolicy.RepeatedFailureWindowMilliseconds + 1), Is.False);
            Assert.That(AutonomousRouteRecoveryPolicy.FailuresBeforeSafeRelocation, Is.EqualTo(3));
        });
    }
}

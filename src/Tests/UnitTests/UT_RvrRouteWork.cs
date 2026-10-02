using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public class UT_RvrRouteWork
{
    private sealed class Navigation : PathfindingMgrBase
    {
        public long Clock;
        public int Calls;
        public bool Failed;
        public bool Partial;
        public override bool IsAvailable => true;
        public override bool HasNavmesh(Zone zone) => true;
        public override Vector3? GetClosestPoint(Zone zone, Vector3 point, float x, float y, float z, EDtPolyFlags[] filters)
        { Calls++; Clock += 10; return point; }
        public override Vector3? GetClosestPoint(Zone zone, Vector3 point, EDtPolyFlags[] filters) => point + Vector3.UnitY;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            Calls++; Clock += 10;
            if (Failed) return new(PathfindingStatus.NoPathFound, 0);
            if (Partial && start.X < 100)
            { destination[0] = new(new(100, 0, 0), 0); return new(PathfindingStatus.PartialPathFound, 1); }
            destination[0] = new(end, 0); return new(PathfindingStatus.PathFound, 1);
        }
    }

    [Test]
    public void YieldIsNotRecordedAsFailureAndResumesPartialCorridor()
    {
        var nav = new Navigation { Partial = true };
        var work = new RvrPlanningNavigation(nav, () => nav.Clock);
        work.BeginSlice();
        Assert.Throws<RvrPlanningNavigation.Yield>(() => AutonomousZoneItinerary.HasCompleteCorridor(work, null, Vector3.Zero, new(1000, 0, 0)));
        Assert.That(nav.Calls, Is.EqualTo(1));
        work.BeginSlice();
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(work, null, Vector3.Zero, new(1000, 0, 0)), Is.True);
        Assert.That(nav.Calls, Is.EqualTo(2), "Replay retains the completed native segment");
        work.BeginSlice();
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(work, null, Vector3.Zero, new(1000, 0, 0)), Is.True);
        Assert.That(nav.Calls, Is.EqualTo(2));
    }

    [Test]
    public void FailedCorridorIsMemoizedWithoutChangingOtherEndpoints()
    {
        var nav = new Navigation { Failed = true };
        var work = new RvrPlanningNavigation(nav, () => nav.Clock);
        work.BeginSlice();
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(work, null, Vector3.Zero, Vector3.One), Is.False);
        work.BeginSlice();
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(work, null, Vector3.Zero, Vector3.One), Is.False);
        Assert.That(nav.Calls, Is.EqualTo(1));
        nav.Failed = false;
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(work, null, Vector3.Zero, new(2, 0, 0)), Is.True);
    }

    [Test]
    public void NewRequestDoesNotInheritOldDoorOrFailureAnswers()
    {
        var nav = new Navigation { Failed = true };
        var first = new RvrPlanningNavigation(nav, () => nav.Clock); first.BeginSlice();
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(first, null, Vector3.Zero, Vector3.One), Is.False);
        nav.Failed = false;
        var next = new RvrPlanningNavigation(nav, () => nav.Clock); next.BeginSlice();
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(next, null, Vector3.Zero, Vector3.One), Is.True);
    }

    [Test]
    public void PathWorkAndMemoryHaveAHardQueryCeiling()
    {
        var nav = new Navigation(); var work = new RvrPlanningNavigation(nav, () => nav.Clock);
        for (int i = 0; i < RvrPlanningNavigation.MaximumQueries; i++)
        { work.BeginSlice(); work.GetClosestPoint(null, new(i, 0, 0), 2, 2, 128, work.DefaultFilters); }
        work.BeginSlice();
        Assert.Throws<RvrPlanningNavigation.Limit>(() => work.GetClosestPoint(null, new(-1, 0, 0), 2, 2, 128, work.DefaultFilters));
        Assert.That(nav.Calls, Is.EqualTo(RvrPlanningNavigation.MaximumQueries));
    }

    [Test]
    public void DoorRevisionInvalidatesTheSameRequestsCorridorAnswer()
    {
        var zone=(Zone)RuntimeHelpers.GetUninitializedObject(typeof(Zone));
        var nav=new Navigation { Failed=true };
        var work=new RvrPlanningNavigation(nav,()=>nav.Clock);work.BeginSlice();
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(work,zone,Vector3.Zero,Vector3.One),Is.False);
        nav.Failed=false;NavigationGeometryRevision.Changed(zone);work.BeginSlice();
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(work,zone,Vector3.Zero,Vector3.One),Is.True);
    }

    [Test]
    public void TimeBetweenSlicesDoesNotSpendTheActiveWorkBudget()
    {
        var nav = new Navigation(); var work = new RvrPlanningNavigation(nav, () => nav.Clock);
        work.BeginSlice();
        work.GetClosestPoint(null, Vector3.Zero, 2, 2, 128, work.DefaultFilters);
        work.EndSlice();
        nav.Clock += 4 * 60 * 60_000L;
        work.BeginSlice();
        Assert.DoesNotThrow(() => work.GetClosestPoint(null, Vector3.One, 2, 2, 128, work.DefaultFilters));
        work.EndSlice();
        Assert.That(work.Queries, Is.EqualTo(2));
    }

    [Test]
    public void ActiveWorkBudgetStillStopsAnExpensiveRequest()
    {
        var nav = new Navigation(); var work = new RvrPlanningNavigation(nav, () => nav.Clock);
        work.BeginSlice(); nav.Clock += RvrPlanningNavigation.MaximumActiveMilliseconds; work.EndSlice();
        work.BeginSlice();
        Assert.Throws<RvrPlanningNavigation.Limit>(() => work.GetClosestPoint(null, Vector3.Zero, 2, 2, 128, work.DefaultFilters));
    }

    [Test]
    public void DefaultProjectionDelegatesItsOriginalExtents()
    {
        var nav = new Navigation(); var work = new RvrPlanningNavigation(nav); work.BeginSlice();
        Assert.That(work.GetClosestPoint(null, Vector3.Zero, work.DefaultFilters), Is.EqualTo(Vector3.UnitY));
    }
}

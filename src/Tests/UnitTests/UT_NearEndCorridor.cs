using System;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

/// <summary>
/// A partial path that stops just short of its end is a complete corridor only when open floor joins the two
/// (Vindsaul Faste, 2026-10-17: a seam crossing point 15-35 units past a tower wall passed as reachable).
/// </summary>
[TestFixture]
public class UT_NearEndCorridor
{
    private sealed class Mesh : PathfindingMgrBase
    {
        public PathfindingStatus Status = PathfindingStatus.PartialPathFound;
        public float ShortBy = 30;
        public bool Wall;
        public override bool HasNavmesh(Zone zone) => true;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            destination[0] = new(start, EDtPolyFlags.Walk);
            destination[1] = new(end - new Vector3(ShortBy, 0, 0), EDtPolyFlags.Walk);
            return new(Status, 2);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => Wall ? start : end;
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => !Wall;
    }

    private static readonly Vector3 Start = new(704_000, 737_320, 5_706), End = new(704_416, 737_344, 5_705);

    [Test]
    public void PartialPathStoppedByAWallIsNoCorridor() =>
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(new Mesh { Wall = true }, null, Start, End), Is.False);

    [Test]
    public void PartialPathEndingOnOpenFloorIsACorridor() =>
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(new Mesh { Wall = false }, null, Start, End), Is.True);

    [Test]
    public void PartialPathOnTheEndPointIsACorridorEvenAtAWall() =>
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(new Mesh { Wall = true, ShortBy = 4 }, null, Start, End), Is.True);

    [Test]
    public void FoundPathNearItsEndStaysACorridor() =>
        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(
            new Mesh { Wall = true, Status = PathfindingStatus.PathFound }, null, Start, End), Is.True);
}

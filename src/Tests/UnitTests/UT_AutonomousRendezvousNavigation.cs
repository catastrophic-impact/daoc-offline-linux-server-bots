using System;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public class UT_AutonomousRendezvousNavigation
{
    private sealed class Mesh : PathfindingMgrBase
    {
        public bool Available = true;
        public bool IsolatedAnchor;
        public bool OneWay;
        public int Samples;
        public int Paths;
        public override bool IsAvailable => Available;
        public override bool HasNavmesh(Zone zone) => Available;
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float x, float y, float z, EDtPolyFlags[] filters) => position;
        public override Vector3? GetRandomPoint(Zone zone, Vector3 position, float radius, EDtPolyFlags[] filters)
        {
            Samples++;
            return position + new Vector3(30, 0, 52); // nearby but disconnected prop top
        }
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 from, Vector3 to, EDtPolyFlags[] filters, Span<WrappedPathfindingNode> nodes)
        {
            Paths++;
            bool connected = !IsolatedAnchor && to.Z == from.Z && (!OneWay || to.X >= from.X && to.Y >= from.Y);
            nodes[0] = new(connected ? to : from, EDtPolyFlags.Walk);
            return new(connected ? PathfindingStatus.PathFound : PathfindingStatus.PartialPathFound, 1);
        }
    }
    private static Zone TestZone => new(null, 2, "Town", 0, 0, 65536, 65536, 2, false, 0, false, 0, 0, 0, 0, 0);

    [Test]
    public void DisconnectedRandomPropNeverReplacesConnectedAnchor()
    {
        var nav = new Mesh();
        Vector3 anchor = new(1000, 1000, 0);
        Assert.That(AutonomousRendezvousNavigation.TryChoosePoint(nav, TestZone, anchor, out var chosen), Is.True);
        Assert.That(chosen, Is.EqualTo(anchor));
        Assert.That(nav.Samples, Is.EqualTo(4), "Randomization is bounded and only occurs at session creation");
        Assert.That(nav.Paths, Is.LessThanOrEqualTo(10));
    }

    [Test]
    public void IsolatedAnchorCannotSeedANewGroup()
    {
        var nav = new Mesh { IsolatedAnchor = true };
        Assert.That(AutonomousRendezvousNavigation.TryChoosePoint(nav, TestZone, new(1000, 1000, 0), out _), Is.False);
        Assert.That(nav.Samples, Is.Zero);
        Assert.That(nav.Paths, Is.EqualTo(4));
    }

    [Test]
    public void OneWayDropIsNotAUsableAssemblyExit()
    {
        var nav = new Mesh { OneWay = true };
        Assert.That(AutonomousRendezvousNavigation.TryChoosePoint(nav, TestZone, new(1000, 1000, 0), out _), Is.False);
    }

    [Test]
    public void MissingMeshDoesNotCertifyAnUnsafePoint()
    {
        var nav = new Mesh { Available = false };
        Assert.That(AutonomousRendezvousNavigation.TryChoosePoint(nav, TestZone, new(1000, 1000, 0), out _), Is.False);
        Assert.That(nav.Paths, Is.Zero);
        Assert.That(nav.Samples, Is.Zero);
    }

    [Test]
    public void LocallyWalkableIslandCannotReachAFormationSlot()
    {
        var nav = new Mesh { IsolatedAnchor = true };
        Vector3 member = new(1000, 1000, 0);
        Vector3 slot = new(1120, 1000, 0);

        Assert.That(AutonomousRendezvousNavigation.CanReachFrom(nav, TestZone, member, slot), Is.False);
    }
}

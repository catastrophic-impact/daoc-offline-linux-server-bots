using System;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public class UT_KeepApproachNavigation
{
    private sealed class Mesh : PathfindingMgrBase
    {
        public bool Closed=true;
        public PathfindingStatus Alternative=PathfindingStatus.PartialPathFound;
        public int Alternatives;
        public override PathfindingResult GetPathStraight(Zone zone,Vector3 start,Vector3 end,EDtPolyFlags[] filters,Span<WrappedPathfindingNode> nodes)
        {
            if((filters[1]&EDtPolyFlags.BlockingDoor)!=0)
            {
                Alternatives++;
                nodes[0]=new(end-new Vector3(20,0,0),EDtPolyFlags.Walk);
                return new(Alternative,1);
            }
            nodes[0]=new(Vector3.Zero,Closed?EDtPolyFlags.BlockingDoor:EDtPolyFlags.Door);
            nodes[1]=new(end,EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound,2);
        }
    }
    [Test]
    public void EnemyClosedGateCannotBeValidatedAsAnInteriorApproachEvenTwentyUnitsShort()
    {
        var mesh=new Mesh();var nav=new AutonomousKeepApproachNavigation(mesh,[]);
        var nodes=new WrappedPathfindingNode[8];
        var result=nav.GetPathStraight(null,new(-100,0,0),new(100,0,0),nav.DefaultFilters,nodes);
        Assert.That(result.Status,Is.EqualTo(PathfindingStatus.NoPathFound));
        Assert.That(result.NodeCount,Is.Zero);
    }
    [Test]
    public void OwnGateUsesNativeFriendlyDoorRouteAndAnOpenedGateAllowsEveryone()
    {
        var mesh=new Mesh();var nodes=new WrappedPathfindingNode[8];
        var defender=new AutonomousKeepApproachNavigation(mesh,[Vector3.Zero]);
        Assert.That(defender.GetPathStraight(null,new(-100,0,0),new(100,0,0),defender.DefaultFilters,nodes).Status,Is.EqualTo(PathfindingStatus.PathFound));
        Assert.That(mesh.Alternatives,Is.Zero);
        mesh.Closed=false;
        var attacker=new AutonomousKeepApproachNavigation(mesh,[]);
        Assert.That(attacker.GetPathStraight(null,new(-100,0,0),new(100,0,0),attacker.DefaultFilters,nodes).Status,Is.EqualTo(PathfindingStatus.PathFound));
        Assert.That(mesh.Alternatives,Is.Zero);
    }
    [Test]
    public void CompleteRouteAroundClosedGateIsAllowed()
    {
        var mesh=new Mesh { Alternative=PathfindingStatus.PathFound };
        var nav=new AutonomousKeepApproachNavigation(mesh,[]);
        Assert.That(nav.GetPathStraight(null,new(-100,0,0),new(100,0,0),nav.DefaultFilters,new WrappedPathfindingNode[8]).Status,Is.EqualTo(PathfindingStatus.PathFound));
        Assert.That(mesh.Alternatives,Is.EqualTo(1));
    }
}

using System;
using System.Numerics;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, Explicit("Installed navmesh read-only"), NonParallelizable]
public class UT_PortalExitProbe
{
    [Test]
    public void EmainMidgardExit()
    {
        string prior=Environment.CurrentDirectory;
        Environment.CurrentDirectory=Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        var zone=new Zone(null,214,"Emain",51*8192,35*8192,8*8192,8*8192,214,false,0,false,0,0,0,0,0);
        try
        {
            LocalPathfindingMgr.LoadNavMesh(zone);
            var nav=PathfindingProvider.LocalPathfindingMgr;
            var start=new Vector3(474107,295199,3871);
            foreach(var end in new[]{new Vector3(473683,295773,3848),new Vector3(473508,296058,3848),new Vector3(472900,296700,3848),new Vector3(470000,300000,3871)})
            {
                var floor=nav.GetClosestPoint(zone,end,128,128,4096,nav.DefaultFilters);
                TestContext.WriteLine($"end={end} floor={floor} corridor={floor.HasValue && AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,start,floor.Value)}");
            }
            TestContext.WriteLine($"startFloor={nav.GetClosestPoint(zone,start,64,64,128,nav.DefaultFilters)}");
            foreach(var goal in new[]{new Vector3(444314,352192,6500),new Vector3(427319,318463,4728)})
            {
                TestContext.WriteLine($"goalFloor={nav.GetClosestPoint(zone,goal,64,64,4096,nav.DefaultFilters)}");
                var nodes=new WrappedPathfindingNode[2048];var current=start;
                for(int i=0;i<5;i++)
                {
                    var result=nav.GetPathStraight(zone,current,goal,nav.DefaultFilters,nodes);
                    if(result.NodeCount==0){TestContext.WriteLine($"goal={goal} {result.Status} empty");break;}
                    var last=nodes[result.NodeCount-1].Position;
                    TestContext.WriteLine($"goal={goal} {result.Status} nodes={result.NodeCount} start={current} end={last} distance={Vector3.Distance(last,goal)}");current=last;
                }
            }
        }
        finally {LocalPathfindingMgr.UnloadNavMesh(zone);Environment.CurrentDirectory=prior;}
    }
}

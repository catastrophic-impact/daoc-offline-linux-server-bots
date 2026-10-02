using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, Explicit("Installed frontier route simulation; read only"), NonParallelizable]
public class UT_AllFrontierKeepRoutes
{
    [Test]
    public void AllThreeRealmsReachEveryKeep()
    {
        string previous=Environment.CurrentDirectory;
        var regions=new Dictionary<ushort,Region>();
        var loaded=new List<Zone>();var failures=new List<string>();int tested=0;
        try
        {
            Environment.CurrentDirectory=Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            using var db=new SQLiteConnection($"Data Source={Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH")};Read Only=True;Pooling=False;");db.Open();
            foreach(ushort id in new ushort[]{1,100,200})
            {
                var region=(Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
                typeof(Region).GetField("m_regionData",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(region,new RegionData{Id=id});
                typeof(Region).GetField("m_zones",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(region,new List<Zone>());
                regions[id]=region;
            }
            using(var q=db.CreateCommand())
            {
                q.CommandText="select ZoneID,Name,RegionID,OffsetX,OffsetY,Width,Height from Zones where RegionID in (1,100,200)";
                using var r=q.ExecuteReader();while(r.Read())
                {
                    ushort id=(ushort)r.GetInt32(0);var region=regions[(ushort)r.GetInt32(2)];
                    var z=new Zone(region,id,r.GetString(1),r.GetInt32(3)*8192,r.GetInt32(4)*8192,r.GetInt32(5)*8192,r.GetInt32(6)*8192,id,false,0,false,0,0,0,0,0);
                    region.Zones.Add(z);
                    // Home staging may lie in a homeland zone; include those meshes too.
                    LocalPathfindingMgr.LoadNavMesh(z);loaded.Add(z);
                }
            }
            var keeps=new List<(string Name,ushort Region,Vector3 Point,int Id,eRealm Defender)>();
            using(var q=db.CreateCommand())
            {
                q.CommandText="select Name,Region,X,Y,Z,KeepID,Realm from [Keep] where Region in (1,100,200) and Name not like '%Portal%'";
                using var r=q.ExecuteReader();while(r.Read())keeps.Add((r.GetString(0),(ushort)r.GetInt32(1),new(r.GetInt32(2),r.GetInt32(3),r.GetInt32(4)),r.GetInt32(5),(eRealm)r.GetInt32(6)));
            }
            var nav=PathfindingProvider.LocalPathfindingMgr;
            bool singleKeep=int.TryParse(Environment.GetEnvironmentVariable("OFFLINE_DAOC_KEEP_ID"),out int onlyKeep);
            foreach(var keep in keeps)
            foreach(eRealm realm in new[]{eRealm.Albion,eRealm.Midgard,eRealm.Hibernia})
            {
                bool probe=Environment.GetEnvironmentVariable("OFFLINE_DAOC_RALLY_AXES")=="1";
                if(singleKeep && keep.Id!=onlyKeep)continue;
                if(probe && keep.Id is not (50 or 58))continue;
                var region=regions[keep.Region];
                var arrival=AutonomousFrontierTransport.Destination(realm,keep.Region).Location;
                Vector3 position=new(arrival.X,arrival.Y,arrival.Z);
                Zone zone=region.Zones.First(z=>Contains(z,position));
                var doors=new List<Vector3>();
                using(var q=db.CreateCommand())
                {
                    q.CommandText="select X,Y,Z from Door where abs(X-@x)<2500 and abs(Y-@y)<2500 and IsPostern=0";
                    q.Parameters.AddWithValue("@x",keep.Point.X);q.Parameters.AddWithValue("@y",keep.Point.Y);
                    using var r=q.ExecuteReader();while(r.Read())doors.Add(new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2)));
                }
                var goal=doors.OrderBy(p=>Vector3.DistanceSquared(p,position)).First();
                Zone goalZone=region.Zones.First(z=>Contains(z,goal));bool success=false;string detail="";
                for(int leg=0;leg<10;leg++)
                {
                    if(zone==goalZone){success=AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,position,goal);detail=$"final corridor {zone.ID} from {position} to {goal}";break;}
                    if(!AutonomousZoneItinerary.TryNextStep(region,zone,goalZone,position,goal,nav,out var step,z=>AutonomousRealmBoundary.Allows(realm,keep.Region,z.ID)))
                    {detail=$"seam {zone.ID}->{goalZone.ID} from {position}";break;}
                    position=step.Outside;zone=region.Zones.First(z=>Contains(z,position));
                }
                tested++;TestContext.WriteLine($"{realm} -> {keep.Name}: {(success?"PASS":"FAIL")} {detail}");
                if(!success)failures.Add($"{realm} -> {keep.Name}: {detail}");
                if (Environment.GetEnvironmentVariable("OFFLINE_DAOC_CHECK_RALLY") == "1" && realm != keep.Defender)
                {
                    int cap=keep.Id is 57 or 58 or 82 or 198 or 110 or 111 ? 192:128;
                    eRealm primary=Enumerable.Range(1,3).Select(v=>(eRealm)v).First(v=>v!=keep.Defender);
                    foreach(int orientation in probe ? Enumerable.Range(0,8) : new[]{AutonomousRvrRally.ChooseOrientation(keep.Id,keep.Region,keep.Defender,cap==192,keep.Point,region,nav,primary)})
                    {
                    int good=0;
                    for(int slot=0;slot<cap;slot++)
                    {
                        bool found=false;
                        for(int variation=0;variation<AutonomousRvrRally.PostVariations;variation++)
                        {
                            Vector3 raw=AutonomousRvrRally.AttackerPost(keep.Point,probe ? orientation : AutonomousRvrRally.OrientationFor(orientation,realm==primary),realm==primary,slot,variation);
                            var postZone=region.Zones.FirstOrDefault(z=>Contains(z,raw));
                            if(postZone==null || !AutonomousRealmBoundary.Allows(realm,keep.Region,postZone.ID)) continue;
                            var floor=nav.GetClosestPoint(postZone,raw,72,72,4096,nav.DefaultFilters);
                            if(!floor.HasValue || Vector2.Distance(new(floor.Value.X,floor.Value.Y),new(keep.Point.X,keep.Point.Y))<AutonomousRvrRally.MinimumKeepDistance ||
                                !AutonomousRendezvousNavigation.HasLocalExit(nav,postZone,floor.Value))continue;
                            Vector3 cursor=floor.Value;var cursorZone=postZone;
                            for(int leg=0;leg<10;leg++)
                            {
                                if(cursorZone==goalZone){found=AutonomousZoneItinerary.HasCompleteCorridor(nav,cursorZone,cursor,goal);break;}
                                if(!AutonomousZoneItinerary.TryNextStep(region,cursorZone,goalZone,cursor,goal,nav,out var next,z=>AutonomousRealmBoundary.Allows(realm,keep.Region,z.ID)))break;
                                cursor=next.Outside;cursorZone=region.Zones.First(z=>Contains(z,cursor));
                            }
                            if(found)
                            {
                                found=AutonomousRvrRally.HasRoute(region,nav,realm,new(arrival.X,arrival.Y,arrival.Z),floor.Value);
                                if(singleKeep && !found && slot==2 && variation==0)
                                {
                                    var trace=new WrappedPathfindingNode[512];Vector3 origin=new(arrival.X,arrival.Y,arrival.Z);
                                    for(int i=0;i<8;i++)
                                    {
                                        var result=nav.GetPathStraight(postZone,origin,floor.Value,nav.DefaultFilters,trace);
                                        if(result.NodeCount<1)break;
                                        var last=trace[result.NodeCount-1].Position;
                                        TestContext.WriteLine($"TRACE goal={floor.Value} from={origin} last={last} status={result.Status} nodes={result.NodeCount}");origin=last;
                                    }
                                }
                            }
                            if(found)break;
                        }
                        if(found)good++;
                        else if(singleKeep)TestContext.WriteLine($"FAILED SLOT realm={realm} slot={slot} axis={orientation}");
                    }
                    TestContext.WriteLine($"RALLY {realm} -> {keep.Name}: {good}/{cap} connected slots axis={orientation}");
                    if(good!=cap)failures.Add($"RALLY {realm} -> {keep.Name}: {good}/{cap}");
                    }
                }
            }
            Assert.That(tested,Is.EqualTo(singleKeep?3:Environment.GetEnvironmentVariable("OFFLINE_DAOC_RALLY_AXES")=="1"?6:81));Assert.That(failures,Is.Empty,string.Join(Environment.NewLine,failures));
        }
        finally {foreach(var zone in loaded.Where(z=>z.IsPathfindingEnabled))LocalPathfindingMgr.UnloadNavMesh(zone);Environment.CurrentDirectory=previous;}
    }
    private static bool Contains(Zone z,Vector3 p)=>p.X>=z.XOffset&&p.X<z.XOffset+z.Width&&p.Y>=z.YOffset&&p.Y<z.YOffset+z.Height;
}

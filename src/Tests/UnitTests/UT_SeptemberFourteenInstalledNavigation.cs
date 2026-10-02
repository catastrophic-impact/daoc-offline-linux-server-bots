using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[Explicit("Read-only installed September 14 incident probes"), NonParallelizable]
public class UT_SeptemberFourteenInstalledNavigation
{
    [Test]
    public void CapturedFloorsAndEntryCorridors()
    {
        string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        string previous = Environment.CurrentDirectory;
        var loaded = new List<Zone>();
        var regions = new Dictionary<int, Region>();
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                (name, asm, search) => name == "lib/Detour" ? NativeLibrary.Load(Path.Combine(root, "lib", "Detour.dll")) : IntPtr.Zero);
            Environment.CurrentDirectory = root;
            using var db = new SQLiteConnection($"Data Source={Path.GetFullPath("../data/opendaoc.sqlite3.db")};Read Only=True;Pooling=False;");
            db.Open();
            var nav = PathfindingProvider.LocalPathfindingMgr;
            foreach (var (rid, start, goal, label) in new (int, Vector3, Vector3, string)[] {
                (191,new(32195,30562,16127),new(33700,32300,16600),"Galladoria pile"),
                (160,new(30522,22478,17467),new(30800,22500,17467),"Glacier pocket"),
                (101,new(32020,28294,8803),new(34124,35786,8238),"Jordheim exchange to Bera"),
                (201,new(34041,31717,8051),new(34261,31717,8000),"TNN merchant west"),
                (201,new(34070,31607,8050),new(34261,31717,8000),"TNN merchant northwest"),
                (100,new(803744,722167,4685),new(803738,721911,4688),"Gularg approach"),
                (51,new(530777,543337,3678),new(525872,542106,3173),"Gothwaite seam"),
                (200,new(330600,483400,5200),new(332000,484000,5200),"Connacht seam"),
                (200,new(352160,680000,7224),new(361354,750434,4944),"Lough Gur outbound"),
                (51,new(428242,417459,5757),new(427000,417000,5757),"Sidi muster") })
            {
                if (!regions.TryGetValue(rid,out var region))
                    regions[rid] = region = (Region)typeof(UT_AuditedDungeonInstalledMesh)
                        .GetMethod("BuildRegion",BindingFlags.NonPublic|BindingFlags.Static)
                        .Invoke(null,new object[]{db,rid,loaded});
                Zone zone=region.GetZone((int)start.X,(int)start.Y);
                var floor=nav.GetClosestPoint(zone,start,48,48,96,nav.DefaultFilters);
                var tall=nav.GetClosestPoint(zone,start,48,48,4096,nav.DefaultFilters);
                var wide=nav.GetClosestPoint(zone,start,768,768,768,nav.DefaultFilters);
                bool approach=AutonomousZonePointApproach.TryResolve(nav,zone,start,goal,112,out var point);
                TestContext.WriteLine($"CAPTURE {label} region={rid} start={start} small={floor} tall={tall} wide={wide} approach={approach}:{point}");
                if (label == "Jordheim exchange to Bera")
                {
                    Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[256];
                    var path = nav.GetPathStraight(zone,start,point,nav.DefaultFilters,nodes);
                    TestContext.WriteLine($"JORDHEIM path={path.Status} nodes={path.NodeCount} recovery={AutonomousCorridorRecovery.TryNextCorner(nav,zone,start,point,out var corner)}:{corner}");
                    for(int i=0;i<Math.Min(5,path.NodeCount);i++)
                        TestContext.WriteLine($"JORDHEIM node={nodes[i].Position} ground={AutonomousNavigationSurface.MoveAlongGround(nav,zone,start,nodes[i].Position)} los={nav.HasLineOfSight(zone,start,nodes[i].Position,nav.DefaultFilters)}");
                }
                if (rid == 191 || label == "Sidi muster")
                {
                    Assert.That(AutonomousRouteHotspotRepair.TryResolveFloor(nav,zone,(ushort)rid,start,out var repaired),Is.True,label);
                    Assert.That(AutonomousNavigationSurface.TryFloor(nav,zone,repaired,out _),Is.True,label);
                    Assert.That(AutonomousRouteHotspotRepair.TryResolveFloor(nav,zone,(ushort)rid,start+new Vector3(0,0,600),out _),Is.False,"Do not pull down another floor");
                    TestContext.WriteLine($"REPAIR {label}: {repaired}");
                }
                if(rid is 191 or 160)
                {
                    using var cmd=db.CreateCommand();cmd.CommandText="SELECT TargetX,TargetY,TargetZ FROM ZonePoint WHERE TargetRegion=@region";
                    cmd.Parameters.AddWithValue("@region",rid);
                    using var reader=cmd.ExecuteReader();
                    while(reader.Read())
                    {
                        Vector3 entrance=new(Convert.ToSingle(reader[0]),Convert.ToSingle(reader[1]),Convert.ToSingle(reader[2]));
                        TestContext.WriteLine($"ENTRY {label} {entrance} toRaw={AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,entrance,start)} toTall={tall.HasValue && AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,entrance,tall.Value)}");
                    }
                }
            }
        }
        finally {foreach(var z in loaded)LocalPathfindingMgr.UnloadNavMesh(z);Environment.CurrentDirectory=previous;}
    }
}

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable, Explicit("Read-only installed route probes")]
    public class UT_SeptemberThirteenInstalledRoutes
    {
        [Test]
        public void Probe()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string previous = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                    (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(Path.Combine(root, "lib", "Detour.dll")) : IntPtr.Zero);
                Environment.CurrentDirectory = root;
                using var db = new SQLiteConnection($"Data Source={Path.GetFullPath(Path.Combine(root, "..", "data", "opendaoc.sqlite3.db"))};Read Only=True;Pooling=False;");
                db.Open();
                foreach (var sample in new[] {
                    (200, new Vector3(378702,497483,5434), new Vector3(377976,495314,5198), "dergan"),
                    (181, new Vector3(400589,420259,4598), new Vector3(403982,421754,4406), "sporite"),
                    (100, new Vector3(707427,773530,4624), new Vector3(708401,772052,4553), "blodfelag"),
                    (1, new Vector3(535933,608077,2962), new Vector3(533198,611484,2272), "spider"),
                    (1, new Vector3(655198,292757,4859), new Vector3(605390,293864,4839), "frontier"),
                    (200, new Vector3(474010,318918,5786), new Vector3(476053,343166,4105), "frontier"),
                    (128, new Vector3(31644,32996,16015), Vector3.Zero, "cave spider"),
                    (222, new Vector3(32450,32804,14962), Vector3.Zero, "spraggonix"),
                    (223, new Vector3(27280,32217,17271), Vector3.Zero, "pelagian crab") })
                {
                    var region = (Region)typeof(UT_AuditedDungeonInstalledMesh).GetMethod("BuildRegion", BindingFlags.NonPublic | BindingFlags.Static)
                        .Invoke(null, new object[] { db, sample.Item1, loaded });
                    var zone = region.GetZone((int)sample.Item2.X,(int)sample.Item2.Y);
                    var nav = PathfindingProvider.LocalPathfindingMgr;
                    if (sample.Item1 == 200 && sample.Item4 == "frontier")
                    {
                        Assert.That(AutonomousRouteHotspotRepair.TryGetImmediateEscape(nav,region,200,sample.Item2,out var escape),Is.True);
                        Assert.That(Vector3.Distance(escape,sample.Item2),Is.LessThan(100));
                        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,escape,sample.Item3),Is.True);
                        Assert.That(AutonomousRouteHotspotRepair.TryGetImmediateEscape(nav,region,200,sample.Item2+new Vector3(0,0,200),out _),Is.False);
                        foreach(int radius in new[] { 48, 96, 192, 384 })
                        for(int angle=0;angle<360;angle+=45)
                        {
                            float radians=angle*MathF.PI/180;
                            Vector3 raw=sample.Item2+new Vector3(MathF.Cos(radians)*radius,MathF.Sin(radians)*radius,0);
                            var p=nav.GetClosestPoint(zone,raw,16,16,128,nav.DefaultFilters);
                            if(p.HasValue && AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,p.Value,sample.Item3))
                                TestContext.WriteLine($"EMAINCONNECTED {p.Value} los={nav.HasLineOfSight(zone,sample.Item2,p.Value,nav.DefaultFilters)} ground={AutonomousNavigationSurface.MoveAlongGround(nav,zone,sample.Item2,p.Value)}");
                        }
                    }
                    var targets = new List<Vector3>();
                    if(sample.Item3 != Vector3.Zero) targets.Add(sample.Item3);
                    else
                    {
                        using var cmd=db.CreateCommand();
                        cmd.CommandText="select X,Y,Z from Mob where Region=@r and Name=@n";
                        cmd.Parameters.AddWithValue("@r",sample.Item1);cmd.Parameters.AddWithValue("@n",sample.Item4);
                        using var reader=cmd.ExecuteReader();
                        while(reader.Read()) targets.Add(new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2)));
                        // Test installed bot goals, not legacy inaccessible NPC
                        // rows which are already excluded from the goal catalog.
                        var verified=AutonomousDungeonGoalCatalog.VerifiedPointsForRegion((ushort)sample.Item1);
                        targets=targets.Select(raw=>verified.FirstOrDefault(p=>p.Name==sample.Item4 &&
                            Vector3.DistanceSquared(raw,new(p.Spawn[0],p.Spawn[1],p.Spawn[2]))<=4))
                            .Where(p=>p!=null).Select(p=>p.Position).ToList();
                        Assert.That(targets,Is.Not.Empty);
                    }
                    foreach(var target in targets)
                    {
                        Span<WrappedPathfindingNode> nodes=stackalloc WrappedPathfindingNode[256];
                        var path=nav.GetPathStraight(zone,sample.Item2,target,nav.DefaultFilters,nodes);
                        var sf=nav.GetClosestPoint(zone,sample.Item2,96,96,512,nav.DefaultFilters);
                        var tf=nav.GetClosestPoint(zone,target,96,96,512,nav.DefaultFilters);
                        bool approach=AutonomousZonePointApproach.TryResolve(nav,zone,sample.Item2,target,112,out var ap);
                        bool recovery=AutonomousCorridorRecovery.TryNextCorner(nav,zone,sample.Item2,target,out var corner);
                        if(sample.Item4 != "frontier" || sample.Item1 == 1)
                        {
                            if(sample.Item3 != Vector3.Zero)
                                Assert.That(path.Status,Is.EqualTo(PathfindingStatus.PathFound),$"{sample.Item4} {target}");
                            Assert.That(approach,Is.True,$"Approach {sample.Item4} {target}");
                            if(sample.Item3 != Vector3.Zero) Assert.That(recovery,Is.True,$"Corner {sample.Item4}");
                        }
                        TestContext.WriteLine($"AUDIT {sample.Item1} {sample.Item4} from={sample.Item2} target={target} status={path.Status} nodes={path.NodeCount} startFloor={sf} targetFloor={tf} approach={approach}:{ap}");
                        if(sample.Item3 != Vector3.Zero) TestContext.WriteLine($"CORNER {sample.Item4} {sample.Item1} success={recovery} point={corner}");
                        if(sample.Item3 != Vector3.Zero && !recovery && sample.Item4 != "frontier")
                            for(int i=0;i<Math.Min(4,path.NodeCount);i++)
                                TestContext.WriteLine($"NODE {sample.Item4} {nodes[i].Position} ground={AutonomousNavigationSurface.MoveAlongGround(nav,zone,sample.Item2,nodes[i].Position)} los={nav.HasLineOfSight(zone,sample.Item2,nodes[i].Position,nav.DefaultFilters)}");
                    }
                }
            }
            finally { foreach(var zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone);Environment.CurrentDirectory=previous; }
        }
    }
}

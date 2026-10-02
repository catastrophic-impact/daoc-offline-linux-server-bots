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
    [TestFixture, NonParallelizable, Explicit("Installed read-only route evidence")]
    public class UT_SeptemberTenRouteProbe
    {
        [Test]
        public void ProbeRecoveryCorridors()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string previous = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            try
            {
                string native = Path.Combine(root, "lib", "Detour.dll");
                NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                    (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(native) : IntPtr.Zero);
                Environment.CurrentDirectory = root;
                using var db = new SQLiteConnection($"Data Source={Path.GetFullPath(Path.Combine(root, "..", "data", "opendaoc.sqlite3.db"))};Read Only=True;Pooling=False;");
                db.Open();
                Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[256];
                foreach (var sample in new[] {
                    (21, new Vector3(32585,32067,16041), "decaying spirit"),
                    (128, new Vector3(31724,32976,16004), "roaming corpse"),
                    (129, new Vector3(33612,34704,15889), "lair guard"),
                    (222, new Vector3(32450,32804,14962), "root worm"),
                    (223, new Vector3(27280,32217,17271), ""),
                    (125, new Vector3(31199,28270,16006), "husk"),
                    (126, new Vector3(32692,33403,16627), ""),
                    (1, new Vector3(648601,309258,2967), "") })
                {
                    var region = (Region)typeof(UT_AuditedDungeonInstalledMesh)
                        .GetMethod("BuildRegion", BindingFlags.NonPublic | BindingFlags.Static)
                        .Invoke(null, new object[] { db, sample.Item1, loaded });
                    Zone zone = region.GetZone((int)sample.Item2.X, (int)sample.Item2.Y);
                    var nav = PathfindingProvider.LocalPathfindingMgr;
                    if (sample.Item1 == 125)
                    {
                        using var mobs = db.CreateCommand();
                        mobs.CommandText = "select Name,Level,X,Y,Z,AggroRange from Mob where Region=125 and Level>=30";
                        using var mr = mobs.ExecuteReader();
                        var high = new List<(string Name,int Level,Vector3 Point,int Radius)>();
                        while (mr.Read()) high.Add((mr.GetString(0),mr.GetInt32(1),new(mr.GetInt32(2),mr.GetInt32(3),mr.GetInt32(4)),mr.GetInt32(5)));
                        foreach (var entry in AutonomousDungeonGoalCatalog.VerifiedPointsForRegion(125).Where(p=>p.Name=="husk").Take(1))
                        {
                            Vector3 start = new(entry.Entries[0][0],entry.Entries[0][1],entry.Entries[0][2]);
                            var path=nav.GetPathStraight(zone,start,entry.Position,nav.DefaultFilters,nodes);
                            TestContext.WriteLine($"HUSKACCESS start={start} end={entry.Position} status={path.Status}");
                            foreach(var mob in high)
                                for(int i=0;i<path.NodeCount;i++)
                                    if(Vector3.Distance(nodes[i].Position,mob.Point)<Math.Max(200,mob.Radius) && nav.HasLineOfSight(zone,nodes[i].Position,mob.Point,nav.DefaultFilters))
                                    { TestContext.WriteLine($"HUSKTHREAT {mob.Name} level={mob.Level} position={mob.Point} radius={mob.Radius}"); break; }
                        }
                    }
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "select Name,X,Y,Z from Mob where Region=@r and Name=@n";
                    cmd.Parameters.AddWithValue("@r", sample.Item1);cmd.Parameters.AddWithValue("@n", sample.Item3);
                    using var reader = cmd.ExecuteReader();
                    var targets = new List<Vector3>();
                    while (reader.Read()) targets.Add(new(reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3)));
                    if (sample.Item1 == 1) targets.Add(new(655215,293820,4856));
                    foreach (Vector3 target in targets.OrderBy(p => Vector3.Distance(p,sample.Item2)).Take(3))
                    {
                        var path = nav.GetPathStraight(zone,sample.Item2,target,nav.DefaultFilters,nodes);
                        TestContext.WriteLine($"PROBE {sample.Item1} {sample.Item3} from={sample.Item2} target={target} status={path.Status} nodes={path.NodeCount} floor={nav.GetClosestPoint(zone,target,96,96,512,nav.DefaultFilters)} canReach={AutonomousDungeonTargetRoute.CanReach(nav,zone,sample.Item2,target)}");
                        if (sample.Item1 == 1)
                            TestContext.WriteLine($"PORTER resolves={AutonomousZonePointApproach.TryResolve(nav,zone,sample.Item2,target,240,out var ap)} approach={ap}");
                    }
                    using var exit = db.CreateCommand();
                    exit.CommandText="select Id,SourceX,SourceY,SourceZ from ZonePoint where SourceRegion=@r";
                    exit.Parameters.AddWithValue("@r",sample.Item1);
                    using var er=exit.ExecuteReader();
                    while(er.Read())
                    {
                        Vector3 p=new(er.GetInt32(1),er.GetInt32(2),er.GetInt32(3));
                        bool ok=AutonomousZonePointApproach.TryResolve(nav,zone,sample.Item2,p,180,out Vector3 approach);
                        TestContext.WriteLine($"EXIT {sample.Item1} id={er.GetInt32(0)} raw={p} approach={approach} resolves={ok} endpointLOS={nav.HasLineOfSight(zone,approach,p,nav.DefaultFilters)}");
                        if (sample.Item1 is 223 or 126)
                        {
                            int id = sample.Item1 == 223 ? 57 : 50;
                            Assert.That(AutonomousWorldBotController.CanUseProvenDungeonExit(nav,zone,approach,id,p),Is.True);
                            Assert.That(AutonomousWorldBotController.CanUseProvenDungeonExit(nav,zone,approach,56,p),Is.False);
                            Assert.That(AutonomousWorldBotController.CanUseProvenDungeonExit(nav,zone,approach + new Vector3(0,0,400),id,p),Is.False);
                        }
                    }
                }
            }
            finally { foreach(var zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone); Environment.CurrentDirectory=previous; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable, Explicit("Read-only installed raid entrance and interior route proofs")]
    public class UT_RealmEventNavigation
    {
        [Test]
        public void EpicDungeonEntrancesReachTheirNativeFinalTrigger()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string previous = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            try
            {
                string native = Environment.GetEnvironmentVariable("OFFLINE_DAOC_TEST_DETOUR");
                NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                    (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(native) : IntPtr.Zero);
                Environment.CurrentDirectory = root;
                string database = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_DATABASE") ?? Path.GetFullPath(Path.Combine(root, "..", "data", "opendaoc.sqlite3.db"));
                using var db = new SQLiteConnection($"Data Source={database};Read Only=True;Pooling=False;");
                db.Open();
                using (Assert.EnterMultipleScope())
                {
                foreach (var definition in AutonomousRealmRaid.Definitions.Where(d => !d.IsDungeon))
                {
                    Region region = UT_AuditedDungeonInstalledMesh.BuildRegion(db, definition.Region, loaded);
                    var nav = PathfindingProvider.LocalPathfindingMgr;
                    Point3D home = DragonLairPlacement.Home(definition.Realm);
                    var occupied = new List<Vector3>();
                    for (int slot = 0; slot < 30; slot++)
                        for (int attempt = 0; attempt < 12; attempt++)
                        {
                            double angle = (attempt * 30 + slot * 137.5) * Math.PI / 180;
                            Vector3 raw = new(home.X + (float)Math.Cos(angle) * 4200, home.Y + (float)Math.Sin(angle) * 4200, home.Z);
                            Zone stageZone = region.GetZone((int)raw.X, (int)raw.Y);
                            if (stageZone == null || !nav.HasNavmesh(stageZone)) continue;
                            Vector3? point = nav.GetClosestPoint(stageZone, raw, 96, 96, 4096, nav.DefaultFilters);
                            if (!point.HasValue || !AutonomousRendezvousNavigation.HasLocalExit(nav, stageZone, point.Value) ||
                                occupied.Any(p => Vector3.DistanceSquared(p, point.Value) < 200 * 200) ||
                                !AutonomousZonePointApproach.TryResolve(nav, stageZone, point.Value, new(home.X, home.Y, home.Z), 300, out _)) continue;
                            occupied.Add(point.Value);
                            break;
                        }
                    TestContext.WriteLine($"{definition.Name}: connected distinct staging posts={occupied.Count}/30 home={home}");
                    Assert.That(occupied.Count, Is.EqualTo(30), definition.Name + " staging-to-lair approaches");
                }
                foreach (var definition in AutonomousRealmRaid.Definitions.Where(d => d.IsDungeon))
                {
                    Region region = UT_AuditedDungeonInstalledMesh.BuildRegion(db, definition.Region, loaded);
                    Zone zone = region.Zones.First();
                    using var command = db.CreateCommand();
                    command.CommandText = "select SourceRegion,SourceX,SourceY,SourceZ,TargetX,TargetY,TargetZ from ZonePoint where TargetRegion=@region and SourceRegion<>@region";
                    command.Parameters.AddWithValue("@region", definition.Region);
                    using var reader = command.ExecuteReader();
                    Assert.That(reader.Read(), Is.True);
                    int outsideRegion = reader.GetInt32(0);
                    Vector3 exterior = new(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
                    Vector3 entry = new(reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6));
                    reader.Close();
                    var nav = PathfindingProvider.LocalPathfindingMgr;
                    Vector3? floor = nav.GetClosestPoint(zone, entry, 48, 48, 64, nav.DefaultFilters);
                    bool final = floor.HasValue && AutonomousZonePointApproach.TryResolve(nav, zone, floor.Value, definition.Trigger, 220, out _);
                    TestContext.WriteLine($"{definition.Name}: entry={entry} floor={floor} finalTrigger={definition.Trigger} connected={final}");
                    Assert.That(final, Is.True, definition.Name + " final trigger");
                    Region outdoors = UT_AuditedDungeonInstalledMesh.BuildRegion(db, outsideRegion, loaded);
                    Zone approachZone = outdoors.GetZone((int)exterior.X, (int)exterior.Y);
                    int posts = 0;
                    var occupiedPosts = new List<Vector3>();
                    for (int slot = 0; slot < 30; slot++)
                    {
                        for (int attempt = 0; attempt < 12; attempt++)
                        {
                            double angle = (attempt * 30 + slot * 137.5) * Math.PI / 180;
                            float radius = 500 + slot % 5 * 200;
                            Vector3 raw = exterior + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
                            Vector3? post = nav.GetClosestPoint(approachZone, raw, 64, 64, 512, nav.DefaultFilters);
                            if (post.HasValue && AutonomousRendezvousNavigation.HasLocalExit(nav, approachZone, post.Value) &&
                                occupiedPosts.All(p => Vector3.DistanceSquared(p, post.Value) >= 200 * 200) &&
                                AutonomousZonePointApproach.TryResolve(nav, approachZone, post.Value, exterior, 220, out _))
                            { posts++; occupiedPosts.Add(post.Value); break; }
                        }
                    }
                    TestContext.WriteLine($"{definition.Name}: exterior party posts={posts}/30 portal={exterior}");
                    Assert.That(posts, Is.EqualTo(30));
                    var points = AutonomousDungeonGoalCatalog.VerifiedPointsForRegion(definition.Region);
                    int connected = points.Count(p => AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, p.Position));
                    TestContext.WriteLine($"{definition.Name}: known interior grind rooms connected={connected}/{points.Length}");
                    Assert.That(connected, Is.EqualTo(points.Length));
                    if (definition.Region == 160)
                    {
                        foreach (Vector3 ladder in new[] { new Vector3(51432, 40316, 11371), new Vector3(52397, 41064, 15025),
                            new Vector3(50866, 35452, 11382), new Vector3(55098, 34216, 17437) })
                        {
                            Vector3? landing = nav.GetClosestPoint(zone, ladder, 128, 128, 128, nav.DefaultFilters);
                            var nodes = new WrappedPathfindingNode[512];
                            var path = nav.GetPathStraight(zone, floor.Value, landing ?? ladder, nav.DefaultFilters, nodes);
                            TestContext.WriteLine($"Glacier climb endpoint={ladder} floor={landing} path={path.Status} nodes={path.NodeCount} last={(path.NodeCount > 0 ? nodes[Math.Min(path.NodeCount, nodes.Length)-1].Position : default)} entryConnected={(landing.HasValue && StrictCorridor(nav, zone, floor.Value, landing.Value))}");
                            if (AutonomousZonePointApproach.TryResolve(nav, zone, floor.Value, ladder, 160, out Vector3 baseApproach))
                                TestContext.WriteLine($"Glacier climb nearest entry-connected approach={baseApproach}, separation={Vector3.Distance(ladder, baseApproach)}");
                            if (ladder.X == 51432 || ladder.X == 52397)
                            {
                                bool found = false;
                                for (int radius = 128; radius <= 1024 && !found; radius += 128)
                                    for (int direction = 0; direction < 16 && !found; direction++)
                                    {
                                        double angle = direction * Math.PI / 8;
                                        Vector3 search = ladder + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
                                        Vector3? nearby = nav.GetClosestPoint(zone, search, 64, 64, 256, nav.DefaultFilters);
                                        if (nearby.HasValue && StrictCorridor(nav, zone, floor.Value, nearby.Value))
                                        {
                                            TestContext.WriteLine($"Glacier climb wider connected floor={nearby}, from={ladder}, distance={Vector3.Distance(ladder, nearby.Value)}, LOS={nav.HasLineOfSight(zone, nearby.Value, landing ?? ladder, nav.DefaultFilters)}");
                                            found = true;
                                        }
                                    }
                            }
                        }
                        string ladderFile = Environment.GetEnvironmentVariable("OFFLINE_DAOC_LADDER_GSET");
                        if (!string.IsNullOrEmpty(ladderFile))
                        {
                            int totalLinks = 0, connectedLinks = 0, entryLinks = 0;
                            foreach (string line in File.ReadLines(ladderFile).Where(l => l.StartsWith("c ")))
                            {
                                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                float Read(int i) => float.Parse(parts[i], CultureInfo.InvariantCulture) * 32;
                                Vector3 a = new(Read(1), Read(3), Read(2)), b = new(Read(4), Read(6), Read(5));
                                Vector3? fa = nav.GetClosestPoint(zone, a, 128, 128, 128, nav.DefaultFilters);
                                Vector3? fb = nav.GetClosestPoint(zone, b, 128, 128, 128, nav.DefaultFilters);
                                bool connects = fa.HasValue && fb.HasValue && StrictCorridor(nav, zone, fa.Value, fb.Value);
                                totalLinks++;
                                bool fromEntrance = fa.HasValue && StrictCorridor(nav, zone, floor.Value, fa.Value);
                                if (fromEntrance) entryLinks++;
                                if (totalLinks is 1 or 40)
                                    TestContext.WriteLine($"Glacier climb chain start={a} floor={fa} entryConnected={fromEntrance}");
                                if (connects) connectedLinks++;
                                else
                                {
                                    var nodeBuffer = new WrappedPathfindingNode[512];
                                    var result = nav.GetPathStraight(zone, fa ?? a, fb ?? b, nav.DefaultFilters, nodeBuffer);
                                    TestContext.WriteLine($"CLIMB_LINK_BLOCKED {totalLinks}: {a}->{b} floors={fa}->{fb} status={result.Status} nodes={result.NodeCount} last={(result.NodeCount > 0 ? nodeBuffer[Math.Min(result.NodeCount, 512)-1].Position : default)}");
                                }
                            }
                            TestContext.WriteLine($"Glacier exported climb links connected={connectedLinks}/{totalLinks}, reachable-from-entry={entryLinks}/{totalLinks}");
                        }
                        foreach (var flight in new[]
                        {
                            ("Torst", new[] { new Vector3(50897,36006,16659), new Vector3(51166,37442,17331), new Vector3(53201,39956,16314),
                                new Vector3(55178,38616,17901), new Vector3(54852,36185,17859), new Vector3(53701,35635,17859), new Vector3(52118,36114,17265) }),
                            ("Hurika", new[] { new Vector3(54652,36348,18279), new Vector3(55113,38549,16679), new Vector3(53370,40527,16268),
                                new Vector3(51711,38978,17130), new Vector3(51519,37213,17046) })
                        })
                        {
                            bool reachable = false;
                            for (int leg = 0; leg < flight.Item2.Length && !reachable; leg++)
                            {
                                Vector3 a = flight.Item2[leg], b = flight.Item2[(leg + 1) % flight.Item2.Length];
                                int steps = (int)Math.Ceiling(Vector3.Distance(a, b) / 200);
                                for (int step = 0; step <= steps; step++)
                                {
                                    Vector3 point = Vector3.Lerp(a, b, (float)step / steps);
                                    if (AutonomousZonePointApproach.TryResolve(nav, zone, floor.Value, point, 220, out Vector3 intercept) &&
                                        Vector3.Distance(point, intercept) <= 350)
                                    { reachable = true; TestContext.WriteLine($"Glacier {flight.Item1} native flight interception point={point} floor={intercept}"); break; }
                                }
                            }
                            TestContext.WriteLine($"Glacier {flight.Item1} native patrol reachable={reachable}");
                        }
                    }
                    using var bosses = db.CreateCommand();
                    bosses.CommandText = "select Name,ClassType,X,Y,Z,Flags from Mob where Region=@region and Realm=0 and ClassType<>'DOL.GS.GameNPC' and Level>0";
                    bosses.Parameters.AddWithValue("@region", definition.Region);
                    using var bossRows = bosses.ExecuteReader();
                    int bossCount = 0;
                    int aerialPatrols = 0;
                    var blocked = new List<string>();
                    while (bossRows.Read())
                    {
                        string name = bossRows.GetString(0), type = bossRows.GetString(1);
                        if (type.Contains("Merchant") || type.Contains("Teleporter") || type.Contains("Trainer")) continue;
                        // Invisible controller at a separate coordinate, not a
                        // combat target. Its bridge trigger is tested above.
                        if (type == "DOL.GS.OlcasgeanInitializator") continue;
                        Vector3 boss = new(bossRows.GetInt32(2), bossRows.GetInt32(3), bossRows.GetInt32(4));
                        if (type == "DOL.GS.GameEpicNPC")
                        {
                            Vector3 corrected = EpicSpawnPlacement.Correct(definition.Region, name, boss);
                            if (corrected != boss)
                            {
                                TestContext.WriteLine($"{name}: legacy rock/wall placement {boss} -> {corrected}; melee approach={AutonomousZonePointApproach.TryResolve(nav, zone, floor.Value, corrected, 112, out _)}");
                                Assert.That(AutonomousZonePointApproach.TryResolve(nav, zone, floor.Value, corrected, 112, out _), Is.True, name + " corrected melee approach");
                                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, corrected, floor.Value), Is.True, name + " corrected return path");
                            }
                            boss = corrected;
                        }
                        bossCount++;
                        if (type is "DOL.GS.Torst" or "DOL.GS.Hurika")
                            TestContext.WriteLine($"{name}: original perch melee approach={AutonomousZonePointApproach.TryResolve(nav, zone, floor.Value, boss, 112, out var perch)} floor={perch}");
                        if (!AutonomousZonePointApproach.TryResolve(nav, zone, floor.Value, boss, 220, out _))
                        {
                            if (RealmRaidDungeonRoute.CanDeferAirbornePatrol(typeof(GameNPC).Assembly.GetType(type), (GameNPC.eFlags)bossRows.GetInt32(5)))
                            {
                                aerialPatrols++;
                                TestContext.WriteLine($"AIRBORNE PATROL {name} at {boss}: no melee-floor claim; remains alive/hostile, cannot count as an encounter kill");
                                continue;
                            }
                            Vector3? ground = nav.GetClosestPoint(zone, boss, 256, 256, 4096, nav.DefaultFilters);
                            bool reachableGround = ground.HasValue && AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, ground.Value);
                            blocked.Add($"{name} ({type}) at {boss}; ground={ground}, connected={reachableGround}");
                            if (definition.Region == 160 && name is "hrimthursa berg" or "icebound skeleton")
                            {
                                foreach (int radius in new[] { 350, 500, 750, 1000 })
                                    if (AutonomousZonePointApproach.TryResolve(nav, zone, floor.Value, boss, radius, out Vector3 nearby))
                                    {
                                        TestContext.WriteLine($"Glacier ground pocket {name}: first wider connected candidate={nearby} distance={Vector3.Distance(boss, nearby)} LOS={nav.HasLineOfSight(zone, nearby, boss, nav.DefaultFilters)}");
                                        break;
                                    }
                                var alternatives = new HashSet<Vector3>();
                                var sampled = new HashSet<Vector3>();
                                foreach (int radius in new[] { 0, 128, 256, 512, 1024 })
                                    for (int direction = 0; direction < (radius == 0 ? 1 : 16); direction++)
                                        foreach (int dz in new[] { 0, -512, -1024, -2048, 512, 1024 })
                                        {
                                            double angle = direction * Math.PI / 8;
                                            Vector3 sample = boss + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, dz);
                                            Vector3? candidate = nav.GetClosestPoint(zone, sample, 48, 48, 256, nav.DefaultFilters);
                                            if (candidate.HasValue && sampled.Add(candidate.Value) &&
                                                AutonomousRendezvousNavigation.HasLocalExit(nav, zone, candidate.Value) &&
                                                AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, candidate.Value))
                                                alternatives.Add(candidate.Value);
                                        }
                                foreach (var alternative in alternatives.OrderBy(p => Vector3.DistanceSquared(p, boss)).Take(5))
                                    TestContext.WriteLine($"Glacier {name} DIAGNOSTIC ONLY connected floor={alternative} distance={Vector3.Distance(alternative, boss)}");
                            }
                        }
                    }
                    TestContext.WriteLine($"{definition.Name}: mandatory grounded/boss approaches={bossCount-blocked.Count-aerialPatrols}/{bossCount-aerialPatrols}; separate airborne patrols={aerialPatrols}");
                    foreach (string failure in blocked) TestContext.WriteLine("BLOCKED " + failure);
                    Assert.That(blocked, Is.Empty, definition.Name + " scripted NPC approaches");
                }
                }
            }
            finally
            {
                foreach (Zone zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
        }

        // Climb links must reach the actual end polygon. Ordinary arrival's
        // 48-unit tolerance is appropriate at a doorway but could hide a
        // missing short link between a climbing pad and the real floor.
        private static bool StrictCorridor(IPathfindingMgr nav, Zone zone, Vector3 start, Vector3 end)
        {
            var nodes = new WrappedPathfindingNode[512];
            var visited = new HashSet<Vector3>();
            for (int step = 0; step < 16 && visited.Add(start); step++)
            {
                var result = nav.GetPathStraight(zone, start, end, nav.DefaultFilters, nodes);
                if (result.NodeCount < 1 || result.NodeCount > nodes.Length) return false;
                Vector3 last = nodes[result.NodeCount - 1].Position;
                if (result.Status == PathfindingStatus.PathFound && Vector3.DistanceSquared(last, end) <= 4 * 4) return true;
                if (result.Status != PathfindingStatus.PartialPathFound) return false;
                start = last;
            }
            return false;
        }
    }
}

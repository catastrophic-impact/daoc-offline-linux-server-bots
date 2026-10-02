using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Read-only proof for dungeons named by the September long-run audit.
    /// This deliberately tests real database spawns against the installed native
    /// meshes. It never treats a locally projected polygon as sufficient: a goal
    /// must have a complete path from an authoritative entrance and back again.
    /// </summary>
    [TestFixture, NonParallelizable, Explicit("Read-only installed dungeon coverage audit")]
    public class UT_AuditedDungeonInstalledMesh
    {
        private static readonly int[] Regions = [21, 22, 24, 125, 126, 128, 129, 221, 222, 223];

        [Test]
        public void EveryOrdinaryDungeonSpawnReportsItsEntranceConnectivity()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            string previous = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            var connectedSpawns = new List<object>();
            try
            {
                string native = Environment.GetEnvironmentVariable("OFFLINE_DAOC_TEST_DETOUR");
                if (!string.IsNullOrEmpty(native))
                    NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                        (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(native) : IntPtr.Zero);
                Environment.CurrentDirectory = root;
                string databasePath = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH") ??
                    Path.GetFullPath(Path.Combine(root, "..", "data", "opendaoc.sqlite3.db"));
                using var db = new SQLiteConnection($"Data Source={databasePath};Read Only=True;Pooling=False;");
                db.Open();

                string requestedRegions = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DUNGEON_REGIONS");
                int[] auditedRegions = string.IsNullOrWhiteSpace(requestedRegions) ? Regions :
                    requestedRegions.Split(',').Select(int.Parse).Distinct().ToArray();
                foreach (int regionId in auditedRegions)
                {
                    Region region = BuildRegion(db, regionId, loaded);
                    Vector3[] entrances = LoadEntrances(db, regionId);
                    Assert.That(entrances, Is.Not.Empty, $"region {regionId} needs an authoritative entrance");
                    Zone zone = region.Zones.First();
                    IPathfindingMgr nav = PathfindingProvider.LocalPathfindingMgr;
                    Vector3[] usableEntrances = entrances
                        .Select(entry => nav.GetClosestPoint(zone, entry, 96, 96, 512, nav.DefaultFilters))
                        .Where(point => point.HasValue)
                        .Select(point => point.Value)
                        // The first valid polygon at several old dungeon portals
                        // is a narrow threshold. Complete bidirectional corridor
                        // checks below are the authority; requiring four radial
                        // exits here incorrectly rejects that legitimate doorway.
                        .ToArray();
                    Assert.That(usableEntrances, Is.Not.Empty, $"region {regionId} entrance must project to a usable floor");

                    int eligible = 0, connected = 0, unprojected = 0, outsideZone = 0;
                    var disconnected = new List<string>();
                    var connectedFloors = new List<(string Id, Vector3 Point)>();
                    var disconnectedFloors = new List<(string Id, Vector3 Point)>();
                    var reachableIds = new HashSet<string>(StringComparer.Ordinal);
                    int proactiveReachable = 0;
                    using var command = db.CreateCommand();
                    command.CommandText = "select m.Mob_ID,m.Name,coalesce(nullif(m.Level,0),cast(t.Level as integer),0)," +
                        "m.X,m.Y,m.Z,m.ClassType,m.Realm from Mob m left join NPCTemplate t on t.TemplateId=m.NPCTemplateID where m.Region=@region";
                    command.Parameters.AddWithValue("@region", regionId);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        string name = reader.GetString(1);
                        int level = reader.GetInt32(2);
                        if (string.IsNullOrEmpty(name) || !char.IsLower(name[0]) || level <= 0 ||
                            reader.GetString(6) != "DOL.GS.GameNPC" || reader.GetInt32(7) != 0)
                            continue;
                        Vector3 spawn = new(reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
                        if (region.GetZone((int)spawn.X, (int)spawn.Y) != zone)
                        {
                            // Legacy aliases outside the installed dungeon are
                            // not live-zone goal candidates. Report separately.
                            outsideZone++;
                            continue;
                        }
                        eligible++;
                        Vector3? floor = nav.GetClosestPoint(zone, spawn, 96, 96, 512, nav.DefaultFilters);
                        if (!floor.HasValue)
                        {
                            unprojected++;
                            disconnected.Add($"UNPROJECTED {reader.GetString(0)} {name} L{level} {spawn}");
                            continue;
                        }
                        Vector3 entry = usableEntrances.FirstOrDefault(candidate =>
                            AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, candidate, floor.Value) &&
                            AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, candidate));
                        // A prop-edge spawn can sit on a separate tiny polygon.
                        // A melee-range, same-floor, visible approach is enough;
                        // it must still have a complete two-way entrance path.
                        if (entry == default)
                        {
                            foreach (int radius in new[] { 32, 64, 96 })
                            {
                                for (int angle = 0; angle < 360 && entry == default; angle += 30)
                                {
                                    float radians = angle * MathF.PI / 180;
                                    Vector3 raw = spawn + new Vector3(MathF.Cos(radians) * radius, MathF.Sin(radians) * radius, 0);
                                    Vector3? nearby = nav.GetClosestPoint(zone, raw, 16, 16, 64, nav.DefaultFilters);
                                    if (!nearby.HasValue || Vector3.Distance(nearby.Value, spawn) > 112 ||
                                        Math.Abs(nearby.Value.Z - spawn.Z) > 64 ||
                                        !nav.HasLineOfSight(zone, nearby.Value, floor.Value, nav.DefaultFilters)) continue;
                                    entry = usableEntrances.FirstOrDefault(candidate =>
                                        AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, candidate, nearby.Value) &&
                                        AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, nearby.Value, candidate));
                                    if (entry != default)
                                    {
                                        TestContext.WriteLine($"ADJACENT_APPROACH region={regionId} id={reader.GetString(0)} spawn={spawn} point={nearby.Value}");
                                        floor = nearby;
                                    }
                                }
                                if (entry != default) break;
                            }
                        }
                        if (entry == default)
                        {
                            disconnected.Add($"DISCONNECTED {reader.GetString(0)} {name} L{level} spawn={spawn} floor={floor.Value}");
                            disconnectedFloors.Add((reader.GetString(0), floor.Value));
                            continue;
                        }
                        connected++;
                        reachableIds.Add(reader.GetString(0));
                        if (AutonomousDungeonTargetRoute.CanReach(nav, zone, entry, spawn)) proactiveReachable++;
                        else TestContext.WriteLine($"TARGET_FLOOR_REJECT region={regionId} id={reader.GetString(0)} spawn={spawn} projected={floor}");
                        connectedFloors.Add((reader.GetString(0), floor.Value));
                        connectedSpawns.Add(new
                        {
                            id = reader.GetString(0),
                            zone = (int)zone.ID,
                            region = regionId,
                            name,
                            spawn = new[] { (int)spawn.X, (int)spawn.Y, (int)spawn.Z },
                            point = new[] { floor.Value.X, floor.Value.Y, floor.Value.Z },
                            entries = usableEntrances
                                .Where(candidate => AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, candidate, floor.Value) &&
                                                    AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, candidate))
                                .Select(candidate => new[] { (int)candidate.X, (int)candidate.Y, (int)candidate.Z })
                                .ToArray()
                        });
                    }
                    TestContext.WriteLine($"DUNGEON_COVERAGE region={regionId} eligible={eligible} connected={connected} " +
                                          $"unprojected={unprojected} disconnected={disconnected.Count - unprojected} outsideZone={outsideZone} proactiveReachable={proactiveReachable}");
                    if (regionId == 223)
                    {
                        foreach (var proof in AutonomousDungeonGoalCatalog.VerifiedPointsForRegion(223))
                        {
                            Assert.That(reachableIds.Contains(proof.Id), Is.True,
                                $"Koalinth goal {proof.Id} must still connect to the real entrance and exit");
                        }
                        foreach (var isolated in disconnectedFloors)
                            Assert.That(AutonomousDungeonTargetRoute.CanReach(nav, zone, usableEntrances[0], isolated.Point),
                                Is.False, $"Disconnected Koalinth target {isolated.Id} must not be acquired by same-name scanning");
                    }
                    foreach (string failure in disconnected)
                        TestContext.WriteLine(failure);
                    if (connectedFloors.Count > 0 && disconnectedFloors.Count > 0)
                    {
                        var nearest = (from reachable in connectedFloors
                                       from isolated in disconnectedFloors
                                       let distance = Vector3.Distance(reachable.Point, isolated.Point)
                                       orderby distance
                                       select (reachable, isolated, distance)).Take(12);
                        foreach (var pair in nearest)
                            TestContext.WriteLine($"COMPONENT_NEAREST region={regionId} distance={pair.distance:R} " +
                                $"reachable={pair.reachable.Id}:{pair.reachable.Point} isolated={pair.isolated.Id}:{pair.isolated.Point} " +
                                $"los={nav.HasLineOfSight(zone, pair.reachable.Point, pair.isolated.Point, nav.DefaultFilters)}");
                    }
                }
                string output = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DUNGEON_AUDIT_OUTPUT");
                if (!string.IsNullOrWhiteSpace(output))
                    File.WriteAllText(output, JsonSerializer.Serialize(new { spawns = connectedSpawns },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                foreach (Zone zone in loaded)
                    LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
        }

        private static Vector3[] LoadEntrances(SQLiteConnection db, int regionId)
        {
            using var command = db.CreateCommand();
            command.CommandText = "select TargetX,TargetY,TargetZ from ZonePoint where TargetRegion=@region and SourceRegion<>@region";
            command.Parameters.AddWithValue("@region", regionId);
            using var reader = command.ExecuteReader();
            var points = new List<Vector3>();
            while (reader.Read())
                points.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
            // Some imported dungeon portal targets sit just outside the first
            // polygon.  Reuse the previously native-proven projected entrance
            // rather than auditing from an unwalkable raw DB coordinate.
            foreach (AutonomousDungeonGoalCatalog.Point proof in
                     AutonomousDungeonGoalCatalog.VerifiedPointsForRegion((ushort)regionId))
                foreach (int[] entry in proof.Entries ?? [])
                    if (entry?.Length == 3)
                        points.Add(new(entry[0], entry[1], entry[2]));
            return points.Distinct().ToArray();
        }

        internal static Region BuildRegion(SQLiteConnection db, int regionId, List<Zone> loaded)
        {
            var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
            typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(region, new RegionData { Id = (ushort)regionId });
            var zones = new List<Zone>();
            typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, zones);
            using var command = db.CreateCommand();
            command.CommandText = "select ZoneID,Name,OffsetX,OffsetY,Width,Height from Zones where RegionID=@region";
            command.Parameters.AddWithValue("@region", regionId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ushort id = (ushort)reader.GetInt32(0);
                Zone zone = new(region, id, reader.GetString(1), reader.GetInt32(2) * 8192,
                    reader.GetInt32(3) * 8192, reader.GetInt32(4) * 8192, reader.GetInt32(5) * 8192,
                    id, false, 0, false, 0, 0, 0, 0, 0);
                zones.Add(zone);
                LocalPathfindingMgr.LoadNavMesh(zone);
                loaded.Add(zone);
            }
            return region;
        }
    }
}

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
using System.Text.Json.Serialization;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_Classic165SpawnRestoration
    {
        private sealed class Manifest
        {
            [JsonPropertyName("schema")]
            public int Schema { get; set; }
            [JsonPropertyName("spawns")]
            public Spawn[] Spawns { get; set; } = [];
        }

        private sealed class Spawn
        {
            [JsonPropertyName("mob_id")]
            public string Mob_Id { get; set; } = string.Empty;
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
            [JsonPropertyName("zone_id")]
            public ushort Zone_Id { get; set; }
            [JsonPropertyName("region_id")]
            public ushort Region_Id { get; set; }
            [JsonPropertyName("world_x")]
            public int World_X { get; set; }
            [JsonPropertyName("world_y")]
            public int World_Y { get; set; }
            [JsonPropertyName("z")]
            public int Z { get; set; }
            [JsonPropertyName("level")]
            public int Level { get; set; }
            [JsonPropertyName("category")]
            public string Category { get; set; } = string.Empty;
            [JsonPropertyName("source_id")]
            public string Source_Id { get; set; } = string.Empty;
            [JsonPropertyName("source_distance")]
            public int Source_Distance { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        [Test]
        public void EmbeddedIdCatalogIsBoundedAndComplete()
        {
            Assert.That(AutonomousClassic165RestoredSpawnCatalog.Count, Is.EqualTo(2_273));
        }

        [Test, Explicit("Generated source-evidence manifest verification")]
        public void ManifestIsBoundedAuditableAndContainsNoSyntheticRows()
        {
            Manifest manifest = LoadManifest();
            Assert.Multiple(() =>
            {
                Assert.That(manifest.Spawns.Length, Is.GreaterThan(0).And.LessThan(1_750));
                Assert.That(manifest.Spawns.Select(spawn => spawn.Mob_Id), Is.Unique);
                Assert.That(manifest.Spawns.All(spawn => spawn.Level is >= 1 and <= 50), Is.True);
                Assert.That(manifest.Spawns.All(spawn => spawn.Category is
                    "old_frontier" or "albion_fragment" or "period_old_frontier" or
                    "period_albion_outdoor" or "period_albion_dungeon"), Is.True);
                Assert.That(manifest.Spawns.All(spawn => !string.IsNullOrWhiteSpace(spawn.Source_Id)), Is.True);
                Assert.That(manifest.Spawns.Where(spawn => spawn.Source_Id.StartsWith("capnbry", StringComparison.Ordinal))
                    .All(spawn => spawn.Source_Distance <= 1_500), Is.True);
                Assert.That(manifest.Spawns.Where(spawn => spawn.Source_Id.StartsWith("kirstena-albion-2002:", StringComparison.Ordinal))
                    .All(spawn => spawn.Source_Distance <= 5_000), Is.True);
                Assert.That(manifest.Spawns.GroupBy(spawn => spawn.Source_Id).All(group => group.Count() <= 4), Is.True);
            });
        }

        [Test, Explicit("Read-only installed database and navmesh verification")]
        public void EveryCandidateExistsOnlyInArchiveAndReachesAZoneSeam()
        {
            string navRoot = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string databasePath = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH");
            Assert.That(navRoot, Is.Not.Null.And.Not.Empty);
            Assert.That(databasePath, Is.Not.Null.And.Not.Empty);
            Manifest manifest = LoadManifest();
            string previous = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            try
            {
                string native = Environment.GetEnvironmentVariable("OFFLINE_DAOC_TEST_DETOUR");
                if (!string.IsNullOrEmpty(native))
                {
                    NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                        (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(native) : IntPtr.Zero);
                }
                Environment.CurrentDirectory = navRoot;
                using var db = new SQLiteConnection($"Data Source={databasePath};Read Only=True;Pooling=False;");
                db.Open();
                var regions = LoadRegions(db, manifest.Spawns.Select(spawn => spawn.Region_Id).Distinct(), loaded);
                var nav = PathfindingProvider.LocalPathfindingMgr;
                Dictionary<ushort, Vector3[]> anchors = LoadEstablishedWorldAnchors(db, regions);
                Dictionary<ushort, Vector3[]> dungeonEntrances = LoadDungeonEntrances(db, regions);
                var failures = new List<string>();
                foreach (Spawn spawn in manifest.Spawns)
                {
                    if (!ExistsOnlyInArchive(db, spawn))
                    {
                        failures.Add($"{spawn.Mob_Id}:{spawn.Name}:archive");
                        continue;
                    }
                    Region region = regions[spawn.Region_Id];
                    Zone zone = region.GetZone(spawn.World_X, spawn.World_Y);
                    if (zone == null || zone.ID != spawn.Zone_Id)
                    {
                        failures.Add($"{spawn.Mob_Id}:{spawn.Name}:zone");
                        continue;
                    }
                    Vector3 raw = new(spawn.World_X, spawn.World_Y, spawn.Z);
                    Vector3? floor = nav.GetClosestPoint(zone, raw, 48, 48, 256, nav.DefaultFilters);
                    if (!floor.HasValue || Vector2.Distance(new(raw.X, raw.Y), new(floor.Value.X, floor.Value.Y)) > 48 ||
                        Math.Abs(raw.Z - floor.Value.Z) > 256)
                    {
                        failures.Add($"{spawn.Mob_Id}:{spawn.Name}:projection");
                    }
                    else if (!AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value))
                    {
                        failures.Add($"{spawn.Mob_Id}:{spawn.Name}:local-exit");
                    }
                    else if (zone.IsDungeon && !CanReachEstablishedWorldAnchor(zone, floor.Value,
                        dungeonEntrances.GetValueOrDefault(zone.ID, []), nav))
                    {
                        failures.Add($"{spawn.Mob_Id}:{spawn.Name}:dungeon-entrance");
                    }
                    else if (!zone.IsDungeon && !CanReachAnyZoneSeam(region, zone, floor.Value, nav) &&
                        !CanReachEstablishedWorldAnchor(zone, floor.Value,
                            anchors.GetValueOrDefault(zone.ID, []), nav))
                    {
                        failures.Add($"{spawn.Mob_Id}:{spawn.Name}:zone-seam");
                    }
                }
                string rejectionPath = Environment.GetEnvironmentVariable("OFFLINE_DAOC_SPAWN_REJECTIONS");
                if (!string.IsNullOrWhiteSpace(rejectionPath))
                    File.WriteAllText(rejectionPath, JsonSerializer.Serialize(failures, new JsonSerializerOptions { WriteIndented = true }));
                Assert.That(failures, Is.Empty, string.Join(",", failures));
            }
            finally
            {
                foreach (Zone zone in loaded)
                    LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
        }

        private static bool CanReachAnyZoneSeam(Region region, Zone startZone, Vector3 start, IPathfindingMgr nav)
        {
            foreach (Zone adjacent in region.Zones)
            {
                if (!AutonomousZoneItinerary.TryGetSharedEdge(startZone, adjacent, out _))
                    continue;
                Vector3 goal = new(adjacent.XOffset + adjacent.Width / 2,
                    adjacent.YOffset + adjacent.Height / 2, start.Z);
                if (AutonomousZoneItinerary.TryNextStep(region, startZone, adjacent,
                    start, goal, nav, out AutonomousZoneBoundaryRouting.Step step) &&
                    AutonomousZoneItinerary.HasCompleteCorridor(nav, startZone, step.Inside, start))
                    return true;
            }
            return false;
        }

        private static bool CanReachEstablishedWorldAnchor(Zone zone, Vector3 start,
            IEnumerable<Vector3> anchors, IPathfindingMgr nav)
        {
            foreach (Vector3 raw in anchors.OrderBy(point => Vector3.DistanceSquared(start, point)).Take(32))
            {
                Vector3? floor = nav.GetClosestPoint(zone, raw, 48, 48, 256, nav.DefaultFilters);
                if (!floor.HasValue || !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value))
                    continue;
                if (AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, start, floor.Value) &&
                    AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, start))
                    return true;
            }
            return false;
        }

        private static Dictionary<ushort, Vector3[]> LoadEstablishedWorldAnchors(SQLiteConnection db,
            IReadOnlyDictionary<ushort, Region> regions)
        {
            var anchors = new Dictionary<ushort, List<Vector3>>();
            foreach (Region region in regions.Values)
            {
                using var command = db.CreateCommand();
                command.CommandText = """
                    SELECT X,Y,Z FROM Mob WHERE Region=@region
                    UNION ALL SELECT SourceX,SourceY,SourceZ FROM ZonePoint WHERE SourceRegion=@region
                    UNION ALL SELECT TargetX,TargetY,TargetZ FROM ZonePoint WHERE TargetRegion=@region
                    """;
                command.Parameters.AddWithValue("@region", region.ID);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int x = reader.GetInt32(0);
                    int y = reader.GetInt32(1);
                    int z = reader.GetInt32(2);
                    Zone zone = region.GetZone(x, y);
                    if (zone == null)
                        continue;
                    if (!anchors.TryGetValue(zone.ID, out List<Vector3> list))
                        anchors[zone.ID] = list = [];
                    list.Add(new(x, y, z));
                }
            }
            return anchors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        }

        private static Dictionary<ushort, Vector3[]> LoadDungeonEntrances(SQLiteConnection db,
            IReadOnlyDictionary<ushort, Region> regions)
        {
            var anchors = new Dictionary<ushort, List<Vector3>>();
            foreach (Region region in regions.Values.Where(candidate => candidate.IsDungeon))
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT TargetX,TargetY,TargetZ FROM ZonePoint WHERE TargetRegion=@region";
                command.Parameters.AddWithValue("@region", region.ID);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int x = reader.GetInt32(0), y = reader.GetInt32(1), z = reader.GetInt32(2);
                    Zone zone = region.GetZone(x, y);
                    if (zone == null) continue;
                    if (!anchors.TryGetValue(zone.ID, out List<Vector3> list))
                        anchors[zone.ID] = list = [];
                    list.Add(new(x, y, z));
                }
            }
            return anchors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        }

        private static Dictionary<ushort, Region> LoadRegions(SQLiteConnection db, IEnumerable<ushort> regionIds,
            ICollection<Zone> loaded)
        {
            var result = new Dictionary<ushort, Region>();
            foreach (ushort regionId in regionIds)
            {
                var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
                typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(region, new RegionData { Id = regionId });
                var list = new List<Zone>();
                typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(region, list);
                using var command = db.CreateCommand();
                command.CommandText = "SELECT ZoneID,Name,OffsetX,OffsetY,Width,Height FROM Zones WHERE RegionID=@region";
                command.Parameters.AddWithValue("@region", regionId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ushort id = (ushort)reader.GetInt32(0);
                    Zone zone = new(region, id, reader.GetString(1), reader.GetInt32(2) * 8192,
                        reader.GetInt32(3) * 8192, reader.GetInt32(4) * 8192, reader.GetInt32(5) * 8192,
                        id, false, 0, false, 0, 0, 0, 0, 0);
                    list.Add(zone);
                    LocalPathfindingMgr.LoadNavMesh(zone);
                    loaded.Add(zone);
                }
                result[regionId] = region;
            }
            return result;
        }

        private static bool ExistsOnlyInArchive(SQLiteConnection db, Spawn spawn)
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM offline_classic165_removed_mobs
                     WHERE Mob_ID=@id AND Name=@name AND Region=@region AND X=@x AND Y=@y AND Z=@z AND Level=@level),
                    (SELECT COUNT(*) FROM Mob WHERE Mob_ID=@id)
                """;
            command.Parameters.AddWithValue("@id", spawn.Mob_Id);
            command.Parameters.AddWithValue("@name", spawn.Name);
            command.Parameters.AddWithValue("@region", spawn.Region_Id);
            command.Parameters.AddWithValue("@x", spawn.World_X);
            command.Parameters.AddWithValue("@y", spawn.World_Y);
            command.Parameters.AddWithValue("@z", spawn.Z);
            command.Parameters.AddWithValue("@level", spawn.Level);
            using var reader = command.ExecuteReader();
            return reader.Read() && reader.GetInt32(0) == 1 && reader.GetInt32(1) <= 1;
        }

        private static Manifest LoadManifest()
        {
            string path = Environment.GetEnvironmentVariable("OFFLINE_DAOC_SPAWN_MANIFEST");
            if (string.IsNullOrWhiteSpace(path))
            {
                Assert.Ignore("Set OFFLINE_DAOC_SPAWN_MANIFEST to the generated evidence manifest.");
                return new();
            }
            Assert.That(File.Exists(path), Is.True, path);
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException("Spawn restoration manifest is invalid.");
        }
    }
}

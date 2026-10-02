using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable, Explicit("Read-only installed starter-dungeon mesh proof")]
    public class UT_StarterDungeonInstalledMesh
    {
        [Test]
        public void EveryEligibleStarterDungeonSpawnHasABidirectionalEntranceRoute()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            string previous = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
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

                foreach ((int regionId, Vector3 entrance) in new[]
                {
                    (21, new Vector3(33150, 32732, 16480)),
                    (129, new Vector3(34693, 33173, 16447)),
                    (221, new Vector3(31120, 29939, 16239)),
                })
                {
                    Region region = BuildRegion(db, regionId, loaded);
                    Zone zone = region.GetZone((int)entrance.X, (int)entrance.Y);
                    Assert.That(zone, Is.Not.Null, $"region {regionId} entrance zone");
                    IPathfindingMgr nav = PathfindingProvider.LocalPathfindingMgr;
                    foreach (AutonomousDungeonGoalCatalog.Point point in
                             AutonomousDungeonGoalCatalog.VerifiedPointsForRegion((ushort)regionId))
                    {
                        Assert.Multiple(() =>
                        {
                            Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, entrance, point.Position),
                                Is.True, $"entrance -> {regionId}:{point.Name}:{point.Id}");
                            Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, point.Position, entrance),
                                Is.True, $"{regionId}:{point.Name}:{point.Id} -> entrance");
                        });
                    }

                    using var command = db.CreateCommand();
                    command.CommandText = "select Mob_ID,Name,Level,ClassType,Realm from Mob where Region=@region";
                    command.Parameters.AddWithValue("@region", regionId);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        string name = reader.GetString(1);
                        bool eligible = !string.IsNullOrEmpty(name) && char.IsLower(name[0]) && reader.GetInt32(2) > 0 &&
                                        reader.GetString(3) == "DOL.GS.GameNPC" && reader.GetInt32(4) == 0;
                        if (eligible)
                            Assert.That(AutonomousDungeonGoalCatalog.HasVerifiedSpawn(reader.GetString(0)), Is.True,
                                $"eligible {regionId}:{name}:{reader.GetString(0)} must be audited");
                    }
                }
            }
            finally
            {
                foreach (Zone zone in loaded)
                    LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
        }

        private static Region BuildRegion(SQLiteConnection db, int regionId, List<Zone> loaded)
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

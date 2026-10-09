using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Read-only installed-navmesh probes for the 2026-10-02 siege audit. Never starts a
    /// server or writes anything.
    /// </summary>
    [TestFixture, NonParallelizable, Explicit("Read-only installed-navmesh keep probe")]
    public class UT_KeepDoorNavmeshProbe
    {
        private string _prior;
        private readonly List<Zone> _loaded = new();
        private SQLiteConnection _db;

        [SetUp]
        public void Load()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            _prior = Environment.CurrentDirectory;
            string native = Environment.GetEnvironmentVariable("OFFLINE_DAOC_TEST_DETOUR");
            if (!string.IsNullOrEmpty(native))
                try
                {
                    NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                        (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(native) : IntPtr.Zero);
                }
                catch (InvalidOperationException) { } // resolver already set by an earlier test
            Environment.CurrentDirectory = root;
            _db = new SQLiteConnection($"Data Source={Path.GetFullPath(Path.Combine(root, "..", "data", "opendaoc.sqlite3.db"))};Read Only=True;Pooling=False;");
            _db.Open();
        }

        [TearDown]
        public void Unload()
        {
            foreach (Zone zone in _loaded) LocalPathfindingMgr.UnloadNavMesh(zone);
            _loaded.Clear();
            _db?.Dispose();
            Environment.CurrentDirectory = _prior;
        }

        private Region LoadRegion(int id)
        {
            var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
            typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, new RegionData { Id = (ushort)id });
            var zones = new List<Zone>();
            typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, zones);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "select ZoneID,Name,OffsetX,OffsetY,Width,Height from Zones where RegionID=" + id;
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                ushort zoneId = (ushort)rows.GetInt32(0);
                var zone = new Zone(region, zoneId, rows.GetString(1), rows.GetInt32(2) * 8192, rows.GetInt32(3) * 8192,
                    rows.GetInt32(4) * 8192, rows.GetInt32(5) * 8192, zoneId, false, 0, false, 0, 0, 0, 0, 0);
                zones.Add(zone); _loaded.Add(zone); LocalPathfindingMgr.LoadNavMesh(zone);
            }
            return region;
        }

        /// <summary>OFFLINE_DAOC_DOOR_LIST: "id,x,y,z,region,name" lines from the startup log.</summary>
        [Test]
        public void FailedKeepDoorsHaveDoorPolygonsNearby()
        {
            string list = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DOOR_LIST");
            Assert.That(list, Is.Not.Null.And.Not.Empty);
            var doors = File.ReadAllLines(list).Where(l => l.Length > 0).Select(l => l.Split(',')).ToArray();
            var regions = doors.Select(d => int.Parse(d[4])).Distinct().ToDictionary(id => id, LoadRegion);
            var nav = PathfindingProvider.LocalPathfindingMgr;
            EDtPolyFlags[] doorOnly = [EDtPolyFlags.Door, 0];
            foreach (string[] d in doors)
            {
                var p = new Vector3(int.Parse(d[1]), int.Parse(d[2]), int.Parse(d[3]));
                Zone zone = regions[int.Parse(d[4])].GetZone((int)p.X, (int)p.Y);
                if (zone == null || !nav.HasNavmesh(zone)) { TestContext.WriteLine($"DOOR {d[0]} {d[5]} r{d[4]} nomesh"); continue; }
                Vector3? door = nav.GetClosestPoint(zone, p, 1024, 1024, 1024, doorOnly);
                Vector3? walk = nav.GetClosestPoint(zone, p, 512, 512, 512, nav.DefaultFilters);
                string Fmt(Vector3? v) => v.HasValue ? $"xy={Vector2.Distance(new(v.Value.X, v.Value.Y), new(p.X, p.Y)):F0} dz={v.Value.Z - p.Z:F0}" : "none";
                TestContext.WriteLine($"DOOR {d[0]} {d[5]} r{d[4]} z{zone.ID} doorPoly[{Fmt(door)}] walk[{Fmt(walk)}]");
            }
        }

        [Test]
        public void HadriansWallStallSpot()
        {
            Region region = LoadRegion(1);
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var spot = new Vector3(625357, 314061, 5034);
            Zone z = region.GetZone((int)spot.X, (int)spot.Y);
            TestContext.WriteLine($"SPOT zone={z?.ID} {z?.Description} mesh={nav.HasNavmesh(z)}");
            foreach (int r in new[] { 16, 48, 128, 256, 512 })
                TestContext.WriteLine($"SPOT floor r{r}={nav.GetClosestPoint(z, spot, r, r, r, nav.DefaultFilters)}");
            Vector3? f = nav.GetClosestPoint(z, spot, 256, 256, 512, nav.DefaultFilters);
            TestContext.WriteLine($"SPOT localExit={(f.HasValue ? AutonomousRendezvousNavigation.HasLocalExit(nav, z, f.Value).ToString() : "-")}");
            foreach (var target in new[] { new Vector3(653430, 345890, 6280), new Vector3(605589, 293789, 4839), new Vector3(630000, 320000, 5000), new Vector3(620000, 310000, 5000) })
            {
                Zone tz = region.GetZone((int)target.X, (int)target.Y);
                Vector3? tf = nav.GetClosestPoint(tz, target, 512, 512, 1024, nav.DefaultFilters);
                string corridor = f.HasValue && tf.HasValue && tz == z ? AutonomousZoneItinerary.HasCompleteCorridor(nav, z, f.Value, tf.Value).ToString() : "n/a";
                TestContext.WriteLine($"SPOT -> {target} tz={tz?.ID} sameZone={tz == z} floor={tf} corridor={corridor}");
            }
        }

        [TestCase(60, 31666, 35982, 18639)]
        [TestCase(160, 35612, 17899, 19050)]
        public void EpicDungeonEntranceReach(int regionId, int x, int y, int z)
        {
            Region region = LoadRegion(regionId);
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var entry = new Vector3(x, y, z);
            Zone zone = region.GetZone(x, y);
            Vector3? start = nav.GetClosestPoint(zone, entry, 64, 64, 128, nav.DefaultFilters);
            TestContext.WriteLine($"ENTRY r{regionId} zone={zone?.ID} start={start}");
            Assert.That(start.HasValue, Is.True);
            var bands = new SortedDictionary<int, (int ok, int total, string eg)>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "select Name,X,Y,Z from Mob where Region=" + regionId + " and Realm=0";
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                var p = new Vector3(rows.GetInt32(1), rows.GetInt32(2), rows.GetInt32(3));
                if (region.GetZone((int)p.X, (int)p.Y) != zone) continue;
                Vector3? floor = nav.GetClosestPoint(zone, p, 96, 96, 256, nav.DefaultFilters);
                int band = (int)p.Z / 500 * 500;
                (int ok, int total, string eg) cur = bands.TryGetValue(band, out var b) ? b : (0, 0, "");
                bool ok = floor.HasValue && AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, start.Value, floor.Value);
                bands[band] = (cur.ok + (ok ? 1 : 0), cur.total + 1, cur.eg.Length > 0 ? cur.eg : (ok ? "" : rows.GetString(0) + "@" + p));
            }
            foreach (var kv in bands) TestContext.WriteLine($"BAND r{regionId} z{kv.Key}: reachable {kv.Value.ok}/{kv.Value.total} {kv.Value.eg}");
        }
    }
}

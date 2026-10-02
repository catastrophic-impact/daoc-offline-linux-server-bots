using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using DOL.GS.Commands;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable, Explicit("Read-only installed navmesh and database probes; never starts the server")]
public class UT_PlayerTravelInstalledMesh
{
    [Test]
    public void StonehengeBarrowsHasSafeExteriorTeleportLanding()
    {
        string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        Assert.That(root, Is.Not.Null.And.Not.Empty);
        string previous = Environment.CurrentDirectory;
        Zone zone = null;
        try
        {
            Environment.CurrentDirectory = root;
            string database = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH") ??
                Path.GetFullPath(Path.Combine(root, "..", "data", "opendaoc.sqlite3.db"));
            using var db = new SQLiteConnection($"Data Source={database};Read Only=True;Pooling=False;");
            db.Open();
            var entrance = new DbZonePoint();
            using (var command = db.CreateCommand())
            {
                command.CommandText = "select Id,SourceRegion,SourceX,SourceY,SourceZ,TargetRegion,TargetX,TargetY,TargetZ from ZonePoint where ZonePoint_ID='Stonehenge entrance'";
                using var reader = command.ExecuteReader();
                Assert.That(reader.Read(), Is.True);
                entrance.Id = (ushort)reader.GetInt32(0);
                entrance.SourceRegion = (ushort)reader.GetInt32(1);
                entrance.SourceX = reader.GetInt32(2);
                entrance.SourceY = reader.GetInt32(3);
                entrance.SourceZ = reader.GetInt32(4);
                entrance.TargetRegion = (ushort)reader.GetInt32(5);
                entrance.TargetX = reader.GetInt32(6);
                entrance.TargetY = reader.GetInt32(7);
                entrance.TargetZ = reader.GetInt32(8);
            }

            var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
            typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(region, new RegionData { Id = entrance.SourceRegion });
            using (var command = db.CreateCommand())
            {
                command.CommandText = "select ZoneID,Name,OffsetX*8192,OffsetY*8192,Width*8192,Height*8192 from Zones where RegionID=@r and OffsetX*8192<=@x and OffsetY*8192<=@y and (OffsetX+Width)*8192>@x and (OffsetY+Height)*8192>@y";
                command.Parameters.AddWithValue("@r", entrance.SourceRegion);
                command.Parameters.AddWithValue("@x", entrance.SourceX);
                command.Parameters.AddWithValue("@y", entrance.SourceY);
                using var reader = command.ExecuteReader(); Assert.That(reader.Read(), Is.True);
                ushort id = (ushort)reader.GetInt32(0);
                zone = new(region, id, reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetInt32(5), id, false, 0, false, 0, 0, 0, 0, 0);
            }
            typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(region, new List<Zone> { zone });
            LocalPathfindingMgr.LoadNavMesh(zone);
            var nav = PathfindingProvider.LocalPathfindingMgr;

            Assert.That(PlayerMobNavigator.TryResolveDungeonExteriorApproach(nav, region, zone,
                entrance, new[] { entrance }, out Vector3 landing), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(PlayerMobNavigator.IsSafeDungeonExteriorDistance(entrance.SourceX,
                    entrance.SourceY, (int)landing.X, (int)landing.Y), Is.True);
                Assert.That(AutonomousRendezvousNavigation.HasLocalExit(nav, zone, landing), Is.True);
                Assert.That(region.GetZone((int)landing.X, (int)landing.Y), Is.SameAs(zone));
            });
            TestContext.WriteLine($"Stonehenge exterior landing: {landing}");
        }
        finally
        {
            if (zone != null) LocalPathfindingMgr.UnloadNavMesh(zone);
            Environment.CurrentDirectory = previous;
        }
    }

    [TestCase("Cotswold Village")] [TestCase("Ardee")] [TestCase("Mularn")]
    public void TownToStableUsesTheInstalledWalkableCorridor(string town)
    {
        string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        Assert.That(root, Is.Not.Null.And.Not.Empty);
        string previous = Environment.CurrentDirectory;
        Zone zone = null;
        try
        {
            Environment.CurrentDirectory = root;
            string database = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB_PATH") ??
                Path.GetFullPath(Path.Combine(root, "..", "data", "opendaoc.sqlite3.db"));
            using var db = new SQLiteConnection($"Data Source={database};Read Only=True;Pooling=False;");
            db.Open();
            Vector3 start, goal; int regionId;
            using (var command = db.CreateCommand())
            {
                command.CommandText = "select a.X,a.Y,a.Z,m.X,m.Y,m.Z,a.Region from Area a join Mob m on m.Region=a.Region where a.Description=@town and m.ClassType='DOL.GS.GameStableMaster' order by (m.X-a.X)*(m.X-a.X)+(m.Y-a.Y)*(m.Y-a.Y) limit 1";
                command.Parameters.AddWithValue("@town", town);
                using var reader = command.ExecuteReader(); Assert.That(reader.Read(), Is.True);
                start = new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
                goal = new(reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
                regionId = reader.GetInt32(6);
            }
            var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
            using (var command = db.CreateCommand())
            {
                command.CommandText = "select ZoneID,OffsetX*8192,OffsetY*8192,Width*8192,Height*8192 from Zones where RegionID=@r and OffsetX*8192<=@x and OffsetY*8192<=@y and (OffsetX+Width)*8192>@x and (OffsetY+Height)*8192>@y";
                command.Parameters.AddWithValue("@r", regionId); command.Parameters.AddWithValue("@x", goal.X); command.Parameters.AddWithValue("@y", goal.Y);
                using var reader = command.ExecuteReader(); Assert.That(reader.Read(), Is.True);
                ushort id = (ushort)reader.GetInt32(0);
                zone = new(region, id, town, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), id, false, 0, false, 0, 0, 0, 0, 0);
            }
            typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, new List<Zone> { zone });
            LocalPathfindingMgr.LoadNavMesh(zone);
            var nav = PathfindingProvider.LocalPathfindingMgr;
            Assert.That(nav.HasNavmesh(zone), Is.True);
            // The area record is only a town center, not a character standing on
            // the floor. Project this test start, never rewrite any world data.
            Vector3? floor = nav.GetClosestPoint(zone, start, 512, 512, 1024, nav.DefaultFilters);
            Assert.That(floor.HasValue, Is.True);
            Vector3 current = floor.Value;
            var path = new PlayerTravelPath();
            bool arrived = false;
            int ticks;
            for (ticks = 0; ticks < 4000; ticks++)
            {
                if (Vector3.Distance(current, goal) <= 225)
                { arrived = true; break; }
                Assert.That(path.TryStep(region, zone, current, goal, 47.5f, ticks * 250, nav, out var next, out var error), Is.True, $"{town}: {error}; current={current}; goal={goal}");
                if (ticks % 20 == 0 && Vector3.DistanceSquared(current, next) < 1)
                {
                    var queue = (Queue<WrappedPathfindingNode>)typeof(PlayerTravelPath).GetField("_nodes", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(path);
                    TestContext.WriteLine($"blocked {town}: at={current} next={next} corners={string.Join(" | ", queue)}");
                    if (queue.TryPeek(out var corner))
                    {
                        Vector3 desired = PlayerTravelPath.Advance(current, corner.Position, 47.5f);
                        TestContext.WriteLine($"desired={desired} closest={nav.GetClosestPoint(zone, desired, nav.DefaultFilters)} los={nav.HasLineOfSight(zone, current, desired, nav.DefaultFilters)} originSnap={nav.GetClosestPoint(zone, current, nav.DefaultFilters)}");
                    }
                }
                current = new((float)Math.Round(next.X), (float)Math.Round(next.Y), (float)Math.Round(next.Z));
            }
            TestContext.WriteLine($"{town}: zone={zone.ID} start={floor} master={goal} final={current} ticks={ticks}");
            Assert.That(arrived, Is.True, "Must reach native stable interaction range, not just obtain a path.");
        }
        finally
        {
            if (zone != null) LocalPathfindingMgr.UnloadNavMesh(zone);
            Environment.CurrentDirectory = previous;
        }
    }
}

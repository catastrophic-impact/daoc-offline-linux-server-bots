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
    /// Read-only proof for the 2026-10-02 Realm Exchange move (tools/move_realm_exchange_npcs.py).
    /// Loads the installed Camelot and Jordheim navmeshes and checks that each broker and guard
    /// stands on real floor, that bots reach a trading point in range from every capital entrance
    /// without trading through a wall, and that the nearest ordinary merchants stay reachable from
    /// there and back (the exchange-then-vendor loop). Never starts a server or writes a row.
    /// </summary>
    [TestFixture, NonParallelizable, Explicit("Read-only installed-navmesh realm exchange proof")]
    public class UT_RealmExchangeRelocation
    {
        private const int InteractionRadius = 256;

        private static readonly (string Name, int Region, Vector3 Broker, Vector3[] Guards)[] Spots =
        [
            ("Brynhild", 101, new(31741, 28040, 8798), [new(31641, 28040, 8798), new(31841, 28040, 8798)]),
            ("Adalyn", 10, new(36621, 30701, 8002), [new(36711, 30703, 8002), new(36531, 30699, 8002)]),
        ];

        [Test]
        public void MovedBrokersKeepAWorkingExchangeAndVendorLoop()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            string prior = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            try
            {
                string native = Environment.GetEnvironmentVariable("OFFLINE_DAOC_TEST_DETOUR");
                if (!string.IsNullOrEmpty(native))
                    NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                        (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(native) : IntPtr.Zero);
                Environment.CurrentDirectory = root;
                using var db = new SQLiteConnection($"Data Source={Path.GetFullPath(Path.Combine(root, "..", "data", "opendaoc.sqlite3.db"))};Read Only=True;Pooling=False;");
                db.Open();
                var nav = PathfindingProvider.LocalPathfindingMgr;

                foreach (var spot in Spots)
                {
                    var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
                    typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(region, new RegionData { Id = (ushort)spot.Region });
                    var zones = new List<Zone>();
                    typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, zones);
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "select ZoneID,Name,OffsetX,OffsetY,Width,Height from Zones where RegionID=" + spot.Region;
                        using var rows = cmd.ExecuteReader();
                        while (rows.Read())
                        {
                            ushort zoneId = (ushort)rows.GetInt32(0);
                            var zone = new Zone(region, zoneId, rows.GetString(1), rows.GetInt32(2) * 8192, rows.GetInt32(3) * 8192,
                                rows.GetInt32(4) * 8192, rows.GetInt32(5) * 8192, zoneId, false, 0, false, 0, 0, 0, 0, 0);
                            zones.Add(zone); loaded.Add(zone); LocalPathfindingMgr.LoadNavMesh(zone);
                        }
                    }
                    Zone city = region.GetZone((int)spot.Broker.X, (int)spot.Broker.Y);
                    Assert.That(nav.HasNavmesh(city), Is.True, spot.Name + " city navmesh");

                    // 1. Broker and guards stand on the floor, not in the air or inside geometry.
                    foreach (Vector3 npc in spot.Guards.Prepend(spot.Broker))
                    {
                        Vector3? floor = nav.GetClosestPoint(city, npc, 24, 24, 64, nav.DefaultFilters);
                        TestContext.WriteLine($"FLOOR {spot.Name} npc={npc} floor={floor}");
                        Assert.That(floor.HasValue, Is.True, $"{spot.Name}: no floor under {npc}");
                        Assert.That(Vector2.Distance(new(floor.Value.X, floor.Value.Y), new(npc.X, npc.Y)), Is.LessThanOrEqualTo(24));
                        Assert.That(MathF.Abs(floor.Value.Z - npc.Z), Is.LessThanOrEqualTo(32), $"{spot.Name}: {npc} floats or sinks");
                    }
                    Vector3 brokerFloor = nav.GetClosestPoint(city, spot.Broker, 24, 24, 64, nav.DefaultFilters).Value;
                    Assert.That(AutonomousRouteHotspotRepair.IsJordheimServicePocket((ushort)spot.Region, spot.Broker), Is.False,
                        "the new spot uses the general approach resolver, not the corridor escape");

                    // 2. From every capital entrance: an in-range trading point, connected both ways,
                    //    with a clear straight view of the broker (no trading through a wall).
                    var entrances = new List<Vector3>();
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "select TargetX,TargetY,TargetZ from ZonePoint where TargetRegion=" + spot.Region;
                        using var rows = cmd.ExecuteReader();
                        while (rows.Read()) entrances.Add(new(rows.GetInt32(0), rows.GetInt32(1), rows.GetInt32(2)));
                    }
                    if (spot.Region == 101) entrances.Add(AutonomousRendezvousNavigation.JordheimMeetingPoint);
                    Assert.That(entrances, Is.Not.Empty);
                    Vector3 trading = default;
                    int routed = 0;
                    foreach (Vector3 entrance in entrances)
                    {
                        if (region.GetZone((int)entrance.X, (int)entrance.Y) != city ||
                            !AutonomousNavigationSurface.TryFloor(nav, city, entrance, out Vector3 start))
                            continue;
                        // Exactly the bot's service resolver: clear-sight trading points first.
                        bool ok = AutonomousZonePointApproach.TryResolve(nav, city, start, spot.Broker,
                            InteractionRadius - 16, out Vector3 approach,
                            AutonomousZonePointApproach.ClearSightOf(nav, city, spot.Broker));
                        TestContext.WriteLine($"APPROACH {spot.Name} from={entrance} approach={approach} ok={ok} " +
                            $"distance={Vector3.Distance(approach, spot.Broker):F0}");
                        Assert.That(ok, Is.True, $"{spot.Name}: no connected trading point from {entrance}");
                        Assert.That(Vector3.Distance(approach, spot.Broker) + 8, Is.LessThan(InteractionRadius),
                            "precise arrival stays inside the real transaction radius");
                        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, city, start, approach), Is.True);
                        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, city, approach, start), Is.True,
                            "bots leave the broker again");
                        bool clear = nav.HasLineOfSight(city, approach, brokerFloor, nav.DefaultFilters);
                        TestContext.WriteLine($"SIGHT {spot.Name} approach={approach} clear={clear} walk={WalkLength(nav, city, approach, brokerFloor):F0} " +
                            $"straight={Vector3.Distance(approach, brokerFloor):F0}");
                        Assert.That(clear, Is.True, $"{spot.Name}: trading point {approach} is behind a wall");
                        Assert.That(WalkLength(nav, city, approach, brokerFloor),
                            Is.LessThanOrEqualTo(Vector3.Distance(approach, brokerFloor) * 1.5f + 64),
                            $"{spot.Name}: the walk to the broker detours around a wall");
                        trading = approach;
                        routed++;
                    }
                    Assert.That(routed, Is.GreaterThan(0), spot.Name + " must be reached from at least one entrance");

                    // 3. Vendor loop: the three nearest ordinary merchants are reachable and back.
                    var merchants = new List<(string Name, Vector3 Position)>();
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "select Name,X,Y,Z from Mob where Region=@r and ClassType='DOL.GS.GameMerchant' " +
                            "and abs(X-@x)<1600 and abs(Y-@y)<1600 and abs(Z-@z)<300";
                        cmd.Parameters.AddWithValue("@r", spot.Region);
                        cmd.Parameters.AddWithValue("@x", (int)spot.Broker.X);
                        cmd.Parameters.AddWithValue("@y", (int)spot.Broker.Y);
                        cmd.Parameters.AddWithValue("@z", (int)spot.Broker.Z);
                        using var rows = cmd.ExecuteReader();
                        while (rows.Read()) merchants.Add((rows.GetString(0), new(rows.GetInt32(1), rows.GetInt32(2), rows.GetInt32(3))));
                    }
                    int vendors = 0;
                    foreach (var merchant in merchants.OrderBy(m => Vector3.DistanceSquared(m.Position, spot.Broker)).Take(3))
                    {
                        bool ok = AutonomousZonePointApproach.TryResolve(nav, city, trading, merchant.Position,
                            InteractionRadius - 16, out Vector3 sell);
                        bool there = ok && AutonomousZoneItinerary.HasCompleteCorridor(nav, city, trading, sell);
                        bool back = ok && AutonomousZoneItinerary.HasCompleteCorridor(nav, city, sell, trading);
                        TestContext.WriteLine($"VENDOR {spot.Name} -> {merchant.Name} {merchant.Position} sell={sell} there={there} back={back}");
                        if (there && back) vendors++;
                    }
                    Assert.That(vendors, Is.GreaterThan(0), spot.Name + " must keep a reachable vendor for the sell loop");
                }
            }
            finally
            {
                foreach (Zone zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = prior;
            }
        }

        private static float WalkLength(IPathfindingMgr nav, Zone zone, Vector3 start, Vector3 end)
        {
            var nodes = new WrappedPathfindingNode[256];
            PathfindingResult result = nav.GetPathStraight(zone, start, end, nav.DefaultFilters, nodes);
            if (result.Status != PathfindingStatus.PathFound || result.NodeCount == 0)
                return float.MaxValue;
            float length = Vector3.Distance(start, nodes[0].Position);
            for (int i = 1; i < result.NodeCount; i++)
                length += Vector3.Distance(nodes[i - 1].Position, nodes[i].Position);
            return length;
        }
    }
}

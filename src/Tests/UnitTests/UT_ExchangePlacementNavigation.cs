using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    // Offline diagnostic only: no world registration, server startup or DB writes.
    [TestFixture, NonParallelizable, Explicit("Reads the installed capital navmeshes only")]
    public class UT_ExchangePlacementNavigation
    {
        [Test]
        public void ValidatePlannedPositionsAndApproaches()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string planPath = Environment.GetEnvironmentVariable("OFFLINE_DAOC_EXCHANGE_PLAN");
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            Assert.That(planPath, Is.Not.Null.And.Not.Empty);
            byte[] planBytes = File.ReadAllBytes(planPath);
            using JsonDocument plan = JsonDocument.Parse(planBytes);
            string previous = Environment.CurrentDirectory;
            var loaded = new Dictionary<int, Zone>();
            try
            {
                Environment.CurrentDirectory = root;
                var nav = PathfindingProvider.LocalPathfindingMgr;
                foreach (ushort id in new ushort[] { 120, 209 })
                {
                    Zone zone = new(null, id, "Exchange placement validation", 8192, 8192, 65536, 65536, id, false, 0, false, 0, 0, 0, 0, 0);
                    LocalPathfindingMgr.LoadNavMesh(zone);
                    loaded.Add(id, zone);
                    Assert.That(nav.HasNavmesh(zone), Is.True);
                }
                foreach (JsonElement entry in plan.RootElement.GetProperty("changes").EnumerateArray())
                {
                    string id = entry.GetProperty("id").GetString();
                    int zoneId = entry.GetProperty("zone").GetInt32();
                    var xyz = entry.GetProperty("position");
                    Vector3 point = new(xyz[0].GetInt32(), xyz[1].GetInt32(), xyz[2].GetInt32());
                    Zone zone = loaded[zoneId];
                    // Keep authoritative client/NPC floor height. These meshes have
                    // a small voxel-height offset; don't move models up onto that offset.
                    for (int i = -1; i < 16; i++)
                    {
                        double angle = 2 * Math.PI * i / 16;
                        float radius = i < 0 ? 0 : 32;
                        Vector3 edge = point + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
                        Vector3? snap = nav.GetClosestPoint(zone, edge, 3, 3, 48, nav.DefaultFilters);
                        Assert.That(snap.HasValue, Is.True, id + " standing clearance");
                        Assert.That(Vector2.Distance(new(edge.X, edge.Y), new(snap.Value.X, snap.Value.Y)), Is.LessThan(4), id);
                        Assert.That(Math.Abs(edge.Z - snap.Value.Z), Is.LessThan(40), id + " same floor");
                    }
                    Vector3[] entrances = zoneId == 120
                        ? [new(32869,36103,8002), new(36051,33658,8002), new(36069,31900,8005), new(31086,36070,8000)]
                        : [new(31604,37783,7678), new(18002,36126,6253)];
                    foreach (Vector3 entrance in entrances)
                        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, entrance, point), Is.True, id + " from " + entrance);

                    if (!id.Contains("-guard-", StringComparison.Ordinal))
                    {
                        Vector3 approach = zoneId == 120 ? new(31740,28140,8776) : new(33370,31340,8000);
                        Assert.That(Vector3.Distance(point, approach), Is.LessThanOrEqualTo(232), id + " interaction range minus margin");
                        Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, approach, point), Is.True, id + " last mile");
                    }
                    TestContext.WriteLine($"Verified {id}: {point}, heading={entry.GetProperty("heading")}, all {entrances.Length} capital entrances, 32-unit standing clearance.");
                }
                TestContext.WriteLine("VERIFIED_PLAN_SHA256=" + Convert.ToHexString(SHA256.HashData(planBytes)));
            }
            finally
            {
                foreach (Zone zone in loaded.Values) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
        }

        [Test]
        public void InspectCapitalFloors()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            string previous = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            try
            {
                Environment.CurrentDirectory = root;
                var nav = PathfindingProvider.LocalPathfindingMgr;
                Zone Load(ushort id)
                {
                    Zone zone = new(null, id, "Exchange placement probe", 8192, 8192, 65536, 65536, id, false, 0, false, 0, 0, 0, 0, 0);
                    LocalPathfindingMgr.LoadNavMesh(zone);
                    loaded.Add(zone);
                    Assert.That(nav.HasNavmesh(zone), Is.True);
                    return zone;
                }
                void Grid(Zone zone, int minX, int maxX, int minY, int maxY, int z)
                {
                    TestContext.WriteLine($"GRID {zone.ID}: X={minX}..{maxX} step50, floor Z={z}. '.' walkable; '#' blocked/other floor");
                    for (int y = minY; y <= maxY; y += 50)
                    {
                        var line = new StringBuilder($"{y}: ");
                        for (int x = minX; x <= maxX; x += 50)
                        {
                            Vector3 point = new(x, y, z);
                            Vector3? snap = nav.GetClosestPoint(zone, point, 3, 3, 50, nav.DefaultFilters);
                            line.Append(snap.HasValue && Vector2.Distance(new(point.X, point.Y), new(snap.Value.X, snap.Value.Y)) < 4 && Math.Abs(point.Z - snap.Value.Z) < 40 ? '.' : '#');
                        }
                        TestContext.WriteLine(line.ToString());
                    }
                }
                void Probe(Zone zone, string name, Vector3 start, Vector3 end)
                {
                    var nodes = new WrappedPathfindingNode[512];
                    var snap = nav.GetClosestPoint(zone, end, 16, 16, 64, nav.DefaultFilters);
                    var result = nav.GetPathStraight(zone, start, end, nav.DefaultFilters, nodes);
                    TestContext.WriteLine($"{name}: target={end}, snap={snap}, status={result.Status}, nodes={result.NodeCount}, end={(result.NodeCount > 0 ? nodes[result.NodeCount - 1].Position : default)}");
                }
                Zone jordheim = Load(120);
                Zone tir = Load(209);
                Grid(jordheim, 31200, 32200, 27200, 28400, 8776);
                Grid(tir, 32800, 33600, 31000, 31800, 8000);
                foreach (Vector3 p in new[] { new Vector3(31740,28020,8776), new Vector3(31560,28020,8776), new Vector3(31920,28020,8776) })
                    Probe(jordheim, "Jordheim entrance to candidate", new(32893,36286,8002), p);
                foreach (Vector3 p in new[] { new Vector3(33197,31340,8000), new Vector3(33197,31190,8000), new Vector3(33197,31490,8000) })
                    Probe(tir, "Tir entrance to candidate", new(31802,38049,7693), p);
            }
            finally
            {
                foreach (Zone zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
        }
    }
}

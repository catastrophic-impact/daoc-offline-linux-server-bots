using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Tool: the solid Classic-side Shrouded Isles portal platforms (Camelot Hills 000, Vale of
    /// Mularn 100, Lough Derg 200) with the real Detour library. Finds the walkable top of each
    /// platform around its zone point and checks routes from a ring of points (600-2,500 units)
    /// up onto it and back. Read only.
    /// Env: OFFLINE_DAOC_NAV_ROOT (folder with navmesh/zone000.nav, zone100.nav, zone200.nav and
    /// lib/Detour.dll), OFFLINE_DAOC_PROOF_OUT (report file).
    /// </summary>
    [TestFixture, Explicit("SI portal platform probe tool"), NonParallelizable]
    public class UT_SiPortalPlatformProbe
    {
        private static readonly (ushort Zone, string Name, int OffX, int OffY, Vector3 Point)[] Portals =
        {
            (0, "Camelot Hills", 67 * 8192, 59 * 8192, new(564976, 509200, 2811)),
            (100, "Vale of Mularn", 92 * 8192, 82 * 8192, new(808830, 725039, 4882)),
            (200, "Lough Derg", 39 * 8192, 59 * 8192, new(348198, 492819, 5240)),
        };

        [Test]
        public void ProbePlatforms()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string output = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_OUT");
            Assume.That(root != null && output != null);
            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = root;
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var lines = new List<string>();
            try
            {
                // Optional OFFLINE_DAOC_PORTALS = "zoneId:offsetX:offsetY:x,y,z;..." replaces the default list.
                var list = Portals.ToList();
                string custom = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PORTALS");
                if (!string.IsNullOrWhiteSpace(custom))
                    list = custom.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(item =>
                    {
                        string[] parts = item.Split(':');
                        float[] v = parts[3].Split(',').Select(float.Parse).ToArray();
                        return (ushort.Parse(parts[0]), $"zone {parts[0]}", int.Parse(parts[1]), int.Parse(parts[2]), new Vector3(v[0], v[1], v[2]));
                    }).ToList();
                foreach (var portal in list)
                {
                    var zone = new Zone(null, portal.Zone, portal.Name, portal.OffX, portal.OffY, 65536, 65536, portal.Zone, false, 0, false, 0, 0, 0, 0, 0);
                    LocalPathfindingMgr.LoadNavMesh(zone);
                    try
                    {
                        // Top surface: query from well above, every 16 units within 240 of the point.
                        var tops = new List<Vector3>();
                        for (int dx = -240; dx <= 240; dx += 16)
                        for (int dy = -240; dy <= 240; dy += 16)
                        {
                            Vector3 above = portal.Point + new Vector3(dx, dy, 500);
                            Vector3? p = nav.GetClosestPoint(zone, above, 8, 8, 520, nav.DefaultFilters);
                            if (p.HasValue && Vector2.Distance(new(p.Value.X, p.Value.Y), new(above.X, above.Y)) < 12)
                                tops.Add(p.Value);
                        }
                        Vector3? ground = nav.GetClosestPoint(zone, portal.Point + new Vector3(0, 0, -200), 8, 8, 260, nav.DefaultFilters);
                        float maxZ = tops.Count > 0 ? tops.Max(t => t.Z) : float.NaN;
                        var raised = tops.Where(t => t.Z >= portal.Point.Z + 40).ToList();
                        // OFFLINE_DAOC_PORTAL_PAD=1: the given point's Z is the pad height; route to
                        // the walkable floor nearest it instead of the highest surface (an arch roof).
                        Vector3? pad = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PORTAL_PAD") == "1"
                            ? nav.GetClosestPoint(zone, portal.Point, 64, 64, 60, nav.DefaultFilters)
                            : null;
                        Vector3 top = pad.HasValue ? pad.Value : raised.Count > 0
                            ? raised.OrderByDescending(t => t.Z).ThenBy(t => Vector2.Distance(new(t.X, t.Y), new(portal.Point.X, portal.Point.Y))).First()
                            : portal.Point;
                        lines.Add($"{portal.Name} zone {portal.Zone:000} point {portal.Point.X:0},{portal.Point.Y:0},{portal.Point.Z:0}: " +
                                  $"surface samples {tops.Count}, raised (>= +40) {raised.Count}, max Z {maxZ:0}, " +
                                  $"ground below point {(ground.HasValue ? $"{ground.Value.Z:0}" : "none")}, top {top.X:0},{top.Y:0},{top.Z:0}");
                        // Height profile along the line through the point (every 32 units).
                        var profile = new List<string>();
                        for (int d = -320; d <= 320; d += 32)
                        {
                            Vector3? p = nav.GetClosestPoint(zone, portal.Point + new Vector3(d, 0, 500), 8, 8, 520, nav.DefaultFilters);
                            profile.Add(p.HasValue ? $"{p.Value.Z:0}" : "-");
                        }
                        lines.Add("  X profile (-320..320 step 32): " + string.Join(" ", profile));
                        profile.Clear();
                        for (int d = -320; d <= 320; d += 32)
                        {
                            Vector3? p = nav.GetClosestPoint(zone, portal.Point + new Vector3(0, d, 500), 8, 8, 520, nav.DefaultFilters);
                            profile.Add(p.HasValue ? $"{p.Value.Z:0}" : "-");
                        }
                        lines.Add("  Y profile (-320..320 step 32): " + string.Join(" ", profile));

                        var nodes = new WrappedPathfindingNode[512];
                        bool Reach(Vector3 a, Vector3 b)
                        {
                            for (int segment = 0; segment < 16; segment++)
                            {
                                var res = nav.GetPathStraight(zone, a, b, nav.DefaultFilters, nodes);
                                if (res.NodeCount < 1) return false;
                                Vector3 last = nodes[res.NodeCount - 1].Position;
                                if (Vector3.Distance(last, b) <= 48) return true;
                                if (res.Status != PathfindingStatus.PartialPathFound || Vector3.Distance(last, a) < 1) return false;
                                a = last;
                            }
                            return false;
                        }
                        int up = 0, down = 0, total = 0;
                        var failures = new List<string>();
                        foreach (int radius in new[] { 600, 1200, 2500 })
                        for (int angle = 0; angle < 360; angle += 15)
                        {
                            double r = angle * Math.PI / 180;
                            Vector3? start = nav.GetClosestPoint(zone, portal.Point + new Vector3((float)(Math.Cos(r) * radius), (float)(Math.Sin(r) * radius), 0), 128, 128, 600, nav.DefaultFilters);
                            if (!start.HasValue) continue;
                            total++;
                            bool u = Reach(start.Value, top), d = Reach(top, start.Value);
                            if (u) up++;
                            if (d) down++;
                            if (!u || !d) failures.Add($"{start.Value.X:0},{start.Value.Y:0},{start.Value.Z:0} up={u} down={d}");
                        }
                        lines.Add($"  routes to the top from {total} ring points: up {up}, down {down}");
                        foreach (string f in failures.Take(12)) lines.Add("    FAIL " + f);
                    }
                    finally { LocalPathfindingMgr.UnloadNavMesh(zone); }
                }
            }
            finally { Environment.CurrentDirectory = previous; }
            File.WriteAllLines(output, lines);
            TestContext.Progress.WriteLine(lines[0]);
        }
    }
}

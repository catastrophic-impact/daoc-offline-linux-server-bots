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
    /// Tool: finds places where a navmesh lets a bot walk straight through one of the client's
    /// invisible zone walls (bound.csv), with the real Detour library. Along every wall segment it
    /// takes floor points on both sides and casts a walkable-surface ray across; a clear ray is a
    /// leak. Read only.
    /// Env: OFFLINE_DAOC_NAV_ROOT (folder with navmesh/ and lib/Detour.dll), OFFLINE_DAOC_PROOF_OUT,
    /// OFFLINE_DAOC_BOUNDS = file with lines "zoneId offsetX offsetY|x1,y1;x2,y2;..." (world units),
    /// optional OFFLINE_DAOC_BOUNDS_ZONES = "0,100,200" to limit the zones.
    /// </summary>
    [TestFixture, Explicit("Navmesh zone boundary leak probe tool"), NonParallelizable]
    public class UT_ZoneBoundaryLeakProbe
    {
        private const float Step = 256f;
        private const float Side = 160f;

        [Test]
        public void ProbeBoundaryLeaks()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string output = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_OUT");
            string boundsFile = Environment.GetEnvironmentVariable("OFFLINE_DAOC_BOUNDS");
            string only = Environment.GetEnvironmentVariable("OFFLINE_DAOC_BOUNDS_ZONES");
            Assume.That(root != null && output != null && boundsFile != null);
            HashSet<ushort> filter = string.IsNullOrWhiteSpace(only) ? null :
                only.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ushort.Parse).ToHashSet();

            var byZone = new Dictionary<ushort, (int X, int Y, List<Vector2[]> Lines)>();
            foreach (string line in File.ReadAllLines(boundsFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] halves = line.Split('|');
                string[] head = halves[0].Split(' ');
                ushort id = ushort.Parse(head[0]);
                if (filter != null && !filter.Contains(id)) continue;
                Vector2[] points = halves[1].Split(';').Select(p => p.Split(','))
                    .Select(p => new Vector2(float.Parse(p[0]), float.Parse(p[1]))).ToArray();
                if (!byZone.TryGetValue(id, out var entry))
                    byZone[id] = entry = (int.Parse(head[1]), int.Parse(head[2]), new List<Vector2[]>());
                entry.Lines.Add(points);
            }

            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = root;
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var report = new List<string>();
            int totalSamples = 0, totalLeaks = 0;
            try
            {
                foreach (var (id, (offsetX, offsetY, polylines)) in byZone.OrderBy(z => z.Key))
                {
                    var zone = new Zone(null, id, $"zone{id}", offsetX, offsetY, 65536, 65536, id, false, 0, false, 0, 0, 0, 0, 0);
                    LocalPathfindingMgr.LoadNavMesh(zone);
                    if (!nav.HasNavmesh(zone))
                    {
                        report.Add($"zone {id}: no navmesh");
                        continue;
                    }
                    int samples = 0, leaks = 0;
                    var leakPoints = new List<string>();
                    try
                    {
                        foreach (Vector2[] polyline in polylines)
                        {
                            for (int i = 0; i + 1 < polyline.Length; i++)
                            {
                                Vector2 a = polyline[i], b = polyline[i + 1], delta = b - a;
                                float length = delta.Length();
                                if (length < 1) continue;
                                Vector2 direction = delta / length, normal = new(-direction.Y, direction.X);
                                for (float t = Step / 2; t < length; t += Step)
                                {
                                    Vector2 at = a + direction * t;
                                    // Skip the outermost sliver of the zone square; the seam belongs to the neighbour.
                                    float lx = at.X - offsetX, ly = at.Y - offsetY;
                                    if (lx < 200 || ly < 200 || lx > 65336 || ly > 65336) continue;
                                    Vector3? left = Floor(zone, at + normal * Side);
                                    Vector3? right = Floor(zone, at - normal * Side);
                                    if (left == null || right == null) continue;
                                    samples++;
                                    if (Math.Abs(left.Value.Z - right.Value.Z) > 600) continue;
                                    if (nav.HasLineOfSight(zone, left.Value, right.Value, nav.DefaultFilters) &&
                                        nav.HasLineOfSight(zone, right.Value, left.Value, nav.DefaultFilters))
                                    {
                                        leaks++;
                                        if (leakPoints.Count < 12)
                                            leakPoints.Add($"{at.X:0},{at.Y:0},{(left.Value.Z + right.Value.Z) / 2:0}");
                                    }
                                }
                            }
                        }
                    }
                    finally { LocalPathfindingMgr.UnloadNavMesh(zone); }
                    totalSamples += samples;
                    totalLeaks += leaks;
                    report.Add($"zone {id}: {polylines.Count} walls, {samples} crossings with floor on both sides, {leaks} leaks" +
                        (leakPoints.Count > 0 ? " e.g. " + string.Join(" ", leakPoints) : ""));
                }
            }
            finally { Environment.CurrentDirectory = previous; }
            report.Insert(0, $"TOTAL {byZone.Count} zones, {totalSamples} crossings, {totalLeaks} leaks");
            File.WriteAllLines(output, report);
            TestContext.Progress.WriteLine(report[0]);

            Vector3? Floor(Zone zone, Vector2 p) =>
                nav.GetClosestPoint(zone, new Vector3(p.X, p.Y, 6000), 40, 40, 9000, nav.DefaultFilters) is Vector3 v &&
                Vector2.Distance(new(v.X, v.Y), p) < 48 ? v : null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Tool: routes from the Iarnwood (zone 156) stuck corner 436838,411341,4803 and points
    /// around it to the redcap camp destinations the stuck bots logged ("NoPathFound"), with
    /// the real Detour library. Read only.
    /// Env: OFFLINE_DAOC_NAV_ROOT (folder with navmesh/zone156.nav and lib/Detour.dll),
    /// OFFLINE_DAOC_PROOF_OUT (report file).
    /// </summary>
    [TestFixture, Explicit("Navmesh corner probe tool"), NonParallelizable]
    public class UT_IarnwoodCornerProbe
    {
        private static readonly Vector3 Corner = new(436838, 411341, 4803);

        private static readonly Vector3[] Destinations =
        {
            new(437885, 412138, 5306), new(437632, 411955, 5179),
            new(437648, 411978, 5191), new(437465, 411833, 5107),
            new(436774, 411293, 4774), // the paralyzer spawn
        };

        [Test]
        public void ProbeCorner()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string output = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_OUT");
            Assume.That(root != null && output != null);
            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = root;
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var zone = new Zone(null, 156, "Iarnwood", 48 * 8192, 48 * 8192, 65536, 65536, 156, false, 0, false, 0, 0, 0, 0, 0);
            var lines = new List<string>();
            LocalPathfindingMgr.LoadNavMesh(zone);
            try
            {
                var nodes = new WrappedPathfindingNode[512];
                int failures = 0, total = 0;
                for (int dx = -48; dx <= 48; dx += 8)
                for (int dy = -48; dy <= 48; dy += 8)
                {
                    Vector3 start = Corner + new Vector3(dx, dy, 0);
                    Vector3? floor = nav.GetClosestPoint(zone, start, nav.DefaultFilters);
                    var parts = new List<string>();
                    foreach (Vector3 end in Destinations)
                    {
                        var res = nav.GetPathStraight(zone, start, end, nav.DefaultFilters, nodes);
                        Vector3 last = res.NodeCount > 0 ? nodes[res.NodeCount - 1].Position : start;
                        bool ok = res.NodeCount > 0 && Vector3.Distance(last, end) <= 64;
                        total++;
                        if (!ok) failures++;
                        parts.Add(ok ? "ok" : $"{res.Status}({res.NodeCount})");
                    }
                    lines.Add($"start {start.X:0},{start.Y:0} floor={(floor.HasValue ? $"{floor.Value.X:0},{floor.Value.Y:0},{floor.Value.Z:0}" : "none")}: {string.Join(" ", parts)}");
                }
                lines.Insert(0, $"zone156 corner probe: {failures} of {total} routes fail");
                // Walk real approach routes into the camp and re-path from every node's
                // exact floating-point position, the way a bot stands on a node.
                var approaches = new List<Vector3>();
                for (int a = 0; a < 360; a += 15)
                {
                    double r = a * Math.PI / 180;
                    var p = nav.GetClosestPoint(zone, Corner + new Vector3((float)(Math.Cos(r) * 1500), (float)(Math.Sin(r) * 1500), 0), 256, 256, 600, nav.DefaultFilters);
                    if (p.HasValue) approaches.Add(p.Value);
                }
                int nodeFailures = 0, nodeTotal = 0;
                foreach (Vector3 from in approaches)
                foreach (Vector3 end in Destinations)
                {
                    var res = nav.GetPathStraight(zone, from, end, nav.DefaultFilters, nodes);
                    var chain = new List<Vector3>();
                    for (int i = 0; i < res.NodeCount; i++) chain.Add(nodes[i].Position);
                    for (int i = 1; i < chain.Count - 1; i++)
                    {
                        Vector3 at = chain[i];
                        var again = nav.GetPathStraight(zone, at, end, nav.DefaultFilters, nodes);
                        Vector3 last = again.NodeCount > 0 ? nodes[again.NodeCount - 1].Position : at;
                        bool ok = again.NodeCount > 0 && Vector3.Distance(last, end) <= 64;
                        nodeTotal++;
                        bool nearCorner = Vector2.Distance(new(at.X, at.Y), new(Corner.X, Corner.Y)) <= 40;
                        if (!ok || nearCorner)
                        {
                            if (!ok) nodeFailures++;
                            lines.Add($"node {at.X:0.###},{at.Y:0.###},{at.Z:0.###} from {from.X:0},{from.Y:0} to {end.X:0},{end.Y:0}: " +
                                      $"{(ok ? "ok" : $"FAIL {again.Status}({again.NodeCount})")} corner={nearCorner} " +
                                      $"los-next={nav.HasLineOfSight(zone, at, chain[i + 1], nav.DefaultFilters)} " +
                                      $"los-nudged={nav.HasLineOfSight(zone, at + Vector3.Normalize(chain[i + 1] - at) * 2, chain[i + 1], nav.DefaultFilters)} " +
                                      $"dupNext={Vector3.Distance(at, chain[i + 1]) < 1}");
                        }
                    }
                }
                lines.Insert(1, $"re-path from exact route nodes: {nodeFailures} of {nodeTotal} fail");
            }
            finally { LocalPathfindingMgr.UnloadNavMesh(zone); Environment.CurrentDirectory = previous; }
            File.WriteAllLines(output, lines);
            TestContext.Progress.WriteLine(lines[0]);
        }
    }
}

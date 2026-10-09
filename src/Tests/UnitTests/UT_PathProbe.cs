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
    /// Tool: the real Detour route between point pairs in one zone, as a bot would get it. Read only.
    /// Env: OFFLINE_DAOC_NAV_ROOT (folder with navmesh/ and lib/Detour.dll), OFFLINE_DAOC_PROOF_OUT,
    /// OFFLINE_DAOC_PATHS = "zoneId:offsetX:offsetY:x,y,z>x,y,z;..." (world units).
    /// Output: one line per pair: status, length, then the nodes "x,y,z" separated by spaces.
    /// </summary>
    [TestFixture, Explicit("Navmesh path probe tool"), NonParallelizable]
    public class UT_PathProbe
    {
        [Test]
        public void ProbePaths()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string output = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_OUT");
            string spec = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PATHS");
            Assume.That(root != null && output != null && spec != null);
            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = root;
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var lines = new List<string>();
            var zones = new Dictionary<ushort, Zone>();
            var nodes = new WrappedPathfindingNode[4096];
            try
            {
                foreach (string item in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = item.Split(':');
                    ushort id = ushort.Parse(parts[0]);
                    if (!zones.TryGetValue(id, out Zone zone))
                    {
                        zone = new Zone(null, id, $"zone{id}", int.Parse(parts[1]), int.Parse(parts[2]), 65536, 65536, id, false, 0, false, 0, 0, 0, 0, 0);
                        LocalPathfindingMgr.LoadNavMesh(zone);
                        zones[id] = zone;
                    }
                    Vector3[] ends = parts[3].Split('>').Select(p => p.Split(',').Select(float.Parse).ToArray())
                        .Select(v => new Vector3(v[0], v[1], v[2])).ToArray();
                    PathfindingResult result = nav.GetPathStraight(zone, ends[0], ends[1], nav.DefaultFilters, nodes);
                    var points = nodes.Take(result.NodeCount).Select(n => n.Position).ToArray();
                    float length = 0;
                    for (int i = 1; i < points.Length; i++) length += Vector3.Distance(points[i - 1], points[i]);
                    // The server's bounded corridor proof (16 partial segments) for the same pair.
                    bool corridor = AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, ends[0], ends[1]);
                    lines.Add($"{result.Status} {length:0} corridor={corridor} " + string.Join(" ", points.Select(p => $"{p.X:0},{p.Y:0},{p.Z:0}")));
                }
            }
            finally
            {
                foreach (Zone zone in zones.Values) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
            File.WriteAllLines(output, lines);
            TestContext.Progress.WriteLine(lines.FirstOrDefault() ?? "no paths");
        }
    }
}

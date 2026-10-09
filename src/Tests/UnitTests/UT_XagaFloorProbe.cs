using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Tool: walkable floor height under Xaga's room and the Tine/Beatha patrol corners in
    /// Galladoria (zone 191), with the real Detour library. Read only.
    /// Env: OFFLINE_DAOC_NAV_ROOT (folder with navmesh/zone191.nav and lib/Detour.dll),
    /// OFFLINE_DAOC_PROOF_OUT (report file).
    /// </summary>
    [TestFixture, Explicit("Navmesh floor probe tool"), NonParallelizable]
    public class UT_XagaFloorProbe
    {
        [Test]
        public void ProbeFloors()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string output = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_OUT");
            Assume.That(root != null && output != null);
            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = root;
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var zone = new Zone(null, 191, "Galladoria", 8192, 8192, 65536, 65536, 191, false, 0, false, 0, 0, 0, 0, 0);
            var lines = new List<string>();
            LocalPathfindingMgr.LoadNavMesh(zone);
            try
            {
                var points = new (string Name, Vector3 P)[]
                {
                    ("Xaga spawn", new(27397, 54975, 12949)),
                    ("Tine spawn", new(27211, 54902, 13213)), ("Beatha spawn", new(27614, 54866, 13213)),
                    ("Beatha 1", new(27572, 54473, 13213)), ("Beatha 2", new(27183, 54530, 13213)),
                    ("Beatha 3", new(27213, 55106, 13213)), ("Beatha 4", new(27581, 55079, 13213)),
                    ("Tine 1", new(27168, 54598, 13213)), ("Tine 2", new(27597, 54579, 13213)),
                    ("Tine 3", new(27606, 55086, 13213)), ("Tine 4", new(27208, 55133, 13213)),
                };
                foreach (var (name, p) in points)
                {
                    Vector3? floor = nav.GetClosestPoint(zone, p, 64, 64, 600, nav.DefaultFilters);
                    string reach = "";
                    if (floor.HasValue)
                    {
                        var nodes = new WrappedPathfindingNode[512];
                        var res = nav.GetPathStraight(zone, new(27606, 54886, 12973), floor.Value, nav.DefaultFilters, nodes);
                        Vector3 last = res.NodeCount > 0 ? nodes[res.NodeCount - 1].Position : default;
                        reach = res.NodeCount > 0 && Vector3.Distance(last, floor.Value) <= 64 ? "reachable from raid front" : $"NOT reachable ({res.Status})";
                    }
                    lines.Add($"{name} {p.X:0},{p.Y:0},{p.Z:0}: floor {(floor.HasValue ? $"{floor.Value.X:0},{floor.Value.Y:0},{floor.Value.Z:0}" : "none")} {reach}");
                }
            }
            finally { LocalPathfindingMgr.UnloadNavMesh(zone); Environment.CurrentDirectory = previous; }
            File.WriteAllLines(output, lines);
            TestContext.Progress.WriteLine(lines[0]);
        }
    }
}

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
    /// Tool: nearest walkable navmesh floor at given points, with the real Detour library, to
    /// compare a mesh against known ground heights (spawns, zone points, player /loc). Read only.
    /// Env: OFFLINE_DAOC_NAV_ROOT (folder with navmesh/ and lib/Detour.dll), OFFLINE_DAOC_PROOF_OUT,
    /// OFFLINE_DAOC_PROBE_POINTS = "zoneId:offsetX:offsetY:x,y,z;..." (offsets in world units).
    /// </summary>
    [TestFixture, Explicit("Navmesh floor probe tool"), NonParallelizable]
    public class UT_FloorAtPointsProbe
    {
        [Test]
        public void ProbeFloors()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string output = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_OUT");
            string spec = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROBE_POINTS");
            Assume.That(root != null && output != null && spec != null);
            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = root;
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var lines = new List<string>();
            var zones = new Dictionary<ushort, Zone>();
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
                    float[] v = parts[3].Split(',').Select(float.Parse).ToArray();
                    var p = new Vector3(v[0], v[1], v[2]);
                    Vector3? near = nav.GetClosestPoint(zone, p, 32, 32, 400, nav.DefaultFilters);
                    lines.Add($"zone {id} {p.X:0},{p.Y:0},{p.Z:0}: floor {(near.HasValue ? $"{near.Value.Z:0} (dz {near.Value.Z - p.Z:+0;-0;0}, dxy {Vector2.Distance(new(near.Value.X, near.Value.Y), new(p.X, p.Y)):0})" : "none")}");
                }
            }
            finally
            {
                foreach (Zone zone in zones.Values) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
            File.WriteAllLines(output, lines);
            TestContext.Progress.WriteLine(lines.FirstOrDefault() ?? "no points");
        }
    }
}

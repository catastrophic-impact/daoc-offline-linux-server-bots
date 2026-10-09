using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Tool: finds navmesh gaps in a dungeon. Samples walkable points, splits them into the part
    /// reachable from the entrance (or OFFLINE_DAOC_PROBE_FROM "x,y,z") and the rest, following
    /// partial paths the way the game's corridor check does, and reports where routes stall and
    /// the closest pairs across the split. Read only.
    /// Env: OFFLINE_DAOC_NAV_ROOT (folder with navmesh/ and lib/Detour.dll), OFFLINE_DAOC_DB,
    /// OFFLINE_DAOC_PROOF_REGION, OFFLINE_DAOC_PROOF_OUT, optional OFFLINE_DAOC_PROBE_STEP
    /// (grid spacing, 0 = spawns only) and OFFLINE_DAOC_PROBE_FROM.
    /// </summary>
    [TestFixture, Explicit("Navmesh gap analysis tool"), NonParallelizable]
    public class UT_NavGapProbe
    {
        [Test]
        public void FindGaps()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string dbPath = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB");
            string output = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_OUT");
            ushort region = ushort.Parse(Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_REGION") ?? "0");
            Assume.That(root != null && dbPath != null && output != null && region > 0);
            using var db = new SQLiteConnection($"Data Source={dbPath};Read Only=True;Pooling=False");
            db.Open();
            (ushort Id, string Name, int X0, int Y0) zinfo;
            using (var cmd = new SQLiteCommand("select ZoneID,Name,OffsetX,OffsetY from Zones where RegionID=@r", db))
            {
                cmd.Parameters.AddWithValue("@r", region);
                using var r = cmd.ExecuteReader(); r.Read();
                zinfo = ((ushort)r.GetInt32(0), r.GetString(1), r.GetInt32(2) * 8192, r.GetInt32(3) * 8192);
            }
            var entrances = new List<Vector3>();
            using (var cmd = new SQLiteCommand("select TargetX,TargetY,TargetZ from ZonePoint where TargetRegion=@r", db))
            {
                cmd.Parameters.AddWithValue("@r", region);
                using var r = cmd.ExecuteReader();
                while (r.Read()) entrances.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
            }
            var mobs = new List<(string Name, Vector3 P)>();
            using (var cmd = new SQLiteCommand("select Name,X,Y,Z from Mob where Region=@r and Realm=0 and Level>0", db))
            {
                cmd.Parameters.AddWithValue("@r", region);
                using var r = cmd.ExecuteReader();
                while (r.Read()) mobs.Add((r.GetString(0), new(r.GetInt32(1), r.GetInt32(2), r.GetInt32(3))));
            }

            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = root;
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var lines = new List<string>();
            var zone = new Zone(null, zinfo.Id, zinfo.Name, zinfo.X0, zinfo.Y0, 65536, 65536, zinfo.Id, false, 0, false, 0, 0, 0, 0, 0);
            LocalPathfindingMgr.LoadNavMesh(zone);
            try
            {
                string from = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROBE_FROM");
                Vector3 origin = from == null ? entrances[0] : ParseVector(from);
                Vector3 entry = nav.GetClosestPoint(zone, origin, 48, 48, 128, nav.DefaultFilters).Value;
                // Sample walkable points: around every mob spawn and randomly across the zone.
                var rng = new Random(7);
                var samples = new List<Vector3>();
                foreach (var m in mobs)
                {
                    var p = nav.GetClosestPoint(zone, m.P, 96, 96, 256, nav.DefaultFilters);
                    if (p.HasValue) samples.Add(p.Value);
                }
                // Dense grid: every 384 units, at heights spanning the dungeon's spawn range.
                float minZ = mobs.Min(m => m.P.Z) - 600, maxZ = mobs.Max(m => m.P.Z) + 600;
                var seen = new HashSet<(int, int, int)>();
                int step = int.Parse(Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROBE_STEP") ?? "384");
                for (int gx = 0; gx < 65536 && step > 0; gx += step)
                for (int gy = 0; gy < 65536; gy += step)
                for (float gz = minZ; gz <= maxZ; gz += 350)
                {
                    var p = nav.GetClosestPoint(zone, new(zinfo.X0 + gx, zinfo.Y0 + gy, gz), 192, 192, 175, nav.DefaultFilters);
                    if (p.HasValue && seen.Add(((int)p.Value.X / 128, (int)p.Value.Y / 128, (int)p.Value.Z / 96))) samples.Add(p.Value);
                }
                var stalls = new Dictionary<(int, int, int), int>();
                // Same continuation as the game's corridor check: follow partial
                // results (256-polygon corridor cap) until the end or a stall.
                var segmentsUsed = new List<int>();
                bool Reach(Vector3 a, Vector3 b)
                {
                    var nodes = new WrappedPathfindingNode[512];
                    var visited = new List<Vector3> { a };
                    for (int segment = 0; segment < 48; segment++)
                    {
                        var res = nav.GetPathStraight(zone, a, b, nav.DefaultFilters, nodes);
                        if (res.NodeCount < 1) return false;
                        Vector3 last = nodes[res.NodeCount - 1].Position;
                        if (Vector3.Distance(last, b) <= 64) { segmentsUsed.Add(segment + 1); return true; }
                        if (res.Status != PathfindingStatus.PartialPathFound || visited.Any(v => Vector3.DistanceSquared(v, last) < 1))
                        {
                            var key = ((int)last.X / 200, (int)last.Y / 200, (int)last.Z / 200);
                            stalls[key] = stalls.GetValueOrDefault(key) + 1;
                            return false;
                        }
                        visited.Add(last);
                        a = last;
                    }
                    return false;
                }
                var reach = samples.Select(s => { bool to = Reach(entry, s); return (P: s, To: to, Back: to && Reach(s, entry)); }).ToList();
                var A = reach.Where(r => r.To && r.Back).Select(r => r.P).ToArray();
                var oneWay = reach.Where(r => r.To && !r.Back).Select(r => r.P).ToArray();
                var B = reach.Where(r => !r.To).Select(r => r.P).ToArray();
                lines.Add($"region {region} entry {entry} samples {samples.Count}: two-way {A.Length}, one-way(down) {oneWay.Length}, unreachable {B.Length}");
                foreach (var m in mobs.GroupBy(m => m.Name).Select(g => g.First()))
                {
                    var p = nav.GetClosestPoint(zone, m.P, 96, 96, 256, nav.DefaultFilters);
                    string s = !p.HasValue ? "no floor" : Reach(entry, p.Value) ? (Reach(p.Value, entry) ? "two-way" : "ONE-WAY") : "UNREACHABLE";
                    lines.Add($"  mob {m.Name} @ {m.P.X:0},{m.P.Y:0},{m.P.Z:0}: {s} segs={(segmentsUsed.Count > 0 ? segmentsUsed[^1] : 0)}");
                }
                lines.Add($"  segments: max {segmentsUsed.DefaultIfEmpty().Max()}, over 16: {segmentsUsed.Count(s => s > 16)} of {segmentsUsed.Count}");
                foreach (var s in stalls.OrderByDescending(s => s.Value).Take(25))
                    lines.Add($"  STALL x{s.Value} near {s.Key.Item1 * 200},{s.Key.Item2 * 200},{s.Key.Item3 * 200}");
                // Candidate links: a stranded point close to a two-way point.
                Vector3[] stranded = oneWay.Concat(B).ToArray();
                var pairs = new List<(float D, Vector3 From, Vector3 To)>();
                foreach (var b in stranded)
                {
                    var best = A.Select(a => (D: Vector3.Distance(a, b), H: Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y)), a))
                        .Where(x => x.H <= 700).OrderBy(x => x.D).FirstOrDefault();
                    if (best.D > 0) pairs.Add((best.D, best.a, b));
                }
                var picked = new List<(float D, Vector3 From, Vector3 To)>();
                foreach (var pr in pairs.OrderBy(p => p.D))
                    if (picked.All(q => Vector3.Distance(q.To, pr.To) > 600)) picked.Add(pr);
                foreach (var pr in picked.Take(40))
                    lines.Add($"  GAP d={pr.D:0} dz={pr.To.Z - pr.From.Z:0} two-way={pr.From.X:0},{pr.From.Y:0},{pr.From.Z:0} stranded={pr.To.X:0},{pr.To.Y:0},{pr.To.Z:0}");
            }
            finally { LocalPathfindingMgr.UnloadNavMesh(zone); Environment.CurrentDirectory = previous; }
            File.WriteAllLines(output, lines);
            TestContext.Progress.WriteLine(lines[0]);
        }

        private static Vector3 ParseVector(string s)
        {
            float[] v = s.Split(',').Select(float.Parse).ToArray();
            return new(v[0], v[1], v[2]);
        }
    }
}

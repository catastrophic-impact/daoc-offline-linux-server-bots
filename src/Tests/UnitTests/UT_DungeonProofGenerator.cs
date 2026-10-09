using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Tool, not a regular test: builds dungeon navigation proofs (dungeon_navigation_points.json
    /// format) for a dungeon region from the installed navmesh. A spawn is written only when a
    /// complete path runs from a real entrance to its floor point and back. Run with
    /// OFFLINE_DAOC_NAV_ROOT (runtime/server), OFFLINE_DAOC_DB (the game database),
    /// OFFLINE_DAOC_PROOF_REGION and OFFLINE_DAOC_PROOF_OUT set.
    /// </summary>
    [TestFixture, Explicit("Generates navigation proofs from the installed navmesh"), NonParallelizable]
    public class UT_DungeonProofGenerator
    {
        private const float SpawnSnapRadius = 200f;
        private const float EndpointTolerance = 64f;

        [Test]
        public void GenerateProofs()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string dbPath = Environment.GetEnvironmentVariable("OFFLINE_DAOC_DB");
            string output = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_OUT");
            ushort region = ushort.Parse(Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_REGION") ?? "0");
            Assume.That(root != null && dbPath != null && output != null && region > 0, "tool settings missing");

            using var db = new SQLiteConnection($"Data Source={dbPath};Read Only=True;Pooling=False");
            db.Open();
            var zones = new List<(ushort Id, string Name, int X0, int Y0, int X1, int Y1)>();
            using (var cmd = new SQLiteCommand("select ZoneID,Name,OffsetX,OffsetY,Width,Height from Zones where RegionID=@r", db))
            {
                cmd.Parameters.AddWithValue("@r", region);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    zones.Add(((ushort)r.GetInt32(0), r.GetString(1), r.GetInt32(2) * 8192, r.GetInt32(3) * 8192,
                        (r.GetInt32(2) + r.GetInt32(4)) * 8192, (r.GetInt32(3) + r.GetInt32(5)) * 8192));
            }
            var entrances = new List<Vector3>();
            using (var cmd = new SQLiteCommand("select TargetX,TargetY,TargetZ from ZonePoint where TargetRegion=@r", db))
            {
                cmd.Parameters.AddWithValue("@r", region);
                using var r = cmd.ExecuteReader();
                while (r.Read()) entrances.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
            }
            var spawns = new List<(string Id, string Name, Vector3 Spawn)>();
            using (var cmd = new SQLiteCommand("select Mob_ID,Name,X,Y,Z from Mob where Region=@r and Realm=0 and Level>0", db))
            {
                cmd.Parameters.AddWithValue("@r", region);
                using var r = cmd.ExecuteReader();
                while (r.Read()) spawns.Add((r.GetString(0), r.GetString(1), new(r.GetInt32(2), r.GetInt32(3), r.GetInt32(4))));
            }

            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = root;
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var proofs = new List<object>();
            int rejected = 0;
            try
            {
                foreach (var z in zones)
                {
                    var zone = new Zone(null, z.Id, z.Name, z.X0, z.Y0, z.X1 - z.X0, z.Y1 - z.Y0, z.Id, false, 0, false, 0, 0, 0, 0, 0);
                    LocalPathfindingMgr.LoadNavMesh(zone);
                    try
                    {
                        if (!nav.HasNavmesh(zone)) continue;
                        bool Inside(Vector3 p) => p.X >= z.X0 && p.X < z.X1 && p.Y >= z.Y0 && p.Y < z.Y1;
                        Vector3[] entryFloors = entrances.Where(Inside)
                            .Select(e => nav.GetClosestPoint(zone, e, 48, 48, 128, nav.DefaultFilters))
                            .Where(e => e.HasValue).Select(e => e.Value).ToArray();
                        foreach (var spawn in spawns.Where(s => Inside(s.Spawn)))
                        {
                            Vector3? floor = nav.GetClosestPoint(zone, spawn.Spawn, 64, 64, 256, nav.DefaultFilters);
                            if (!floor.HasValue || Vector3.Distance(floor.Value, spawn.Spawn) > SpawnSnapRadius) { rejected++; continue; }
                            int[][] proven = entryFloors.Where(e => Reaches(nav, zone, e, floor.Value) && Reaches(nav, zone, floor.Value, e))
                                .Select(e => new[] { (int)MathF.Round(e.X), (int)MathF.Round(e.Y), (int)MathF.Round(e.Z) }).ToArray();
                            if (proven.Length == 0) { rejected++; continue; }
                            proofs.Add(new
                            {
                                id = spawn.Id, zone = z.Id, region, name = spawn.Name,
                                spawn = new[] { (int)spawn.Spawn.X, (int)spawn.Spawn.Y, (int)spawn.Spawn.Z },
                                point = new[] { floor.Value.X, floor.Value.Y, floor.Value.Z },
                                entries = proven
                            });
                        }
                    }
                    finally { LocalPathfindingMgr.UnloadNavMesh(zone); }
                }
            }
            finally { Environment.CurrentDirectory = previous; }

            File.WriteAllText(output, JsonSerializer.Serialize(proofs));
            TestContext.Progress.WriteLine($"region {region}: {proofs.Count} proofs written, {rejected} spawns without a two-way entrance route, {entrances.Count} entrances");
        }

        private static bool Reaches(IPathfindingMgr nav, Zone zone, Vector3 from, Vector3 to)
        {
            var nodes = new WrappedPathfindingNode[4096];
            PathfindingResult result = nav.GetPathStraight(zone, from, to, nav.DefaultFilters, nodes);
            return result.Status == PathfindingStatus.PathFound && result.NodeCount > 0 &&
                Vector3.Distance(nodes[result.NodeCount - 1].Position, to) <= EndpointTolerance;
        }
    }
}

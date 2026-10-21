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
    /// Tool (owner 2026-10-18: "audit ... see if the bots have a path to the dragons and their meeting spots", relic keeps,
    /// Apocalypse, Olcasgean): walks event routes on the installed meshes as a bot does. Zone steps come from the server's
    /// planner (AutonomousZoneItinerary.ProbeNextStep); inside a zone the walk is Detour, continued from the end of a
    /// partial path as the mover continues it (up to 24 segments), with the zone road route for a far-short partial.
    /// Env: OFFLINE_DAOC_NAV_ROOT, OFFLINE_DAOC_PROOF_OUT, OFFLINE_DAOC_ZONES ("id:x:y:w:h;..."), OFFLINE_DAOC_REGION,
    /// AUDIT_TRIPS = "name=x,y,z>x,y,z>...;..." and optionally AUDIT_DRAGON = "hub x,y,z>home x,y,z": all raid parties'
    /// muster posts at the hub and staging posts round the lair (the server's own TryPost / TryDragonPost), then
    /// hub -> every staging post and every staging post -> its lair approach. Read only.
    /// </summary>
    [TestFixture, Explicit("Event route audit tool"), NonParallelizable]
    public class UT_EventRouteAudit
    {
        [Test]
        public void Audit()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string output = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_OUT");
            string zoneSpec = Environment.GetEnvironmentVariable("OFFLINE_DAOC_ZONES");
            Assume.That(root != null && output != null && zoneSpec != null);
            ushort region = ushort.Parse(Environment.GetEnvironmentVariable("OFFLINE_DAOC_REGION") ?? "0");
            static Vector3 Point(string s) { float[] v = s.Split(',').Select(float.Parse).ToArray(); return new Vector3(v[0], v[1], v[2]); }
            static string P(Vector3 p) => $"{(int)p.X},{(int)p.Y},{(int)p.Z}";
            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = root;
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var lines = new List<string>();
            var zones = zoneSpec.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(item =>
            {
                int[] v = item.Split(':').Select(int.Parse).ToArray();
                return new Zone(null, (ushort)v[0], $"zone{v[0]}", v[1], v[2], v[3], v[4], (ushort)v[0], false, 0, false, 0, 0, 0, 0, 0);
            }).ToList();
            Zone At(Vector3 p) => zones.FirstOrDefault(z => p.X >= z.XOffset && p.X < z.XOffset + z.Width && p.Y >= z.YOffset && p.Y < z.YOffset + z.Height);
            var nodes = new WrappedPathfindingNode[4096];
            var trips = new List<(string Name, Vector3[] Points)>();
            foreach (string trip in (Environment.GetEnvironmentVariable("AUDIT_TRIPS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                trips.Add((trip.Split('=')[0], trip.Split('=')[1].Split('>').Select(Point).ToArray()));
            int complete = 0, failed = 0;
            try
            {
                foreach (Zone zone in zones) LocalPathfindingMgr.LoadNavMesh(zone);
                if (Environment.GetEnvironmentVariable("AUDIT_DRAGON") is string dragon)
                {
                    Vector3 hub = Point(dragon.Split('>')[0]), home = Point(dragon.Split('>')[1]);
                    Zone hubZone = At(hub), lairZone = At(home);
                    var hubRecord = new RealmRaidMuster.Hub("audit", "audit hub", region, hubZone.ID, 0, 0, hub);
                    var hubPosts = new List<Vector3>();
                    var stagingPosts = new List<Vector3>();
                    for (int slot = 0; slot < RealmRaidRecruitmentPolicy.MaximumParties; slot++)
                    {
                        if (RealmRaidMuster.TryPost(nav, hubZone, hubRecord, slot, hubPosts, out Vector3 post)) hubPosts.Add(post);
                        else lines.Add($"hub post {slot}: NONE");
                        if (RealmRaidStaging.TryDragonPost(nav, lairZone, home, slot, stagingPosts, out Vector3 staging)) { stagingPosts.Add(staging); if (slot < 3) lines.Add($"staging post {slot}: {P(staging)}"); }
                        else lines.Add($"staging post {slot}: NONE");
                    }
                    lines.Add($"dragon hub posts {hubPosts.Count}/{RealmRaidRecruitmentPolicy.MaximumParties}, staging posts {stagingPosts.Count}/{RealmRaidRecruitmentPolicy.MaximumParties}");
                    for (int i = 0; i < stagingPosts.Count; i++)
                    {
                        trips.Add(($"hub>staging{i}", [hub, stagingPosts[i]]));
                        if (AutonomousZonePointApproach.TryResolve(nav, lairZone, stagingPosts[i], home, 300, out Vector3 approach))
                            trips.Add(($"staging{i}>lair", [stagingPosts[i], approach]));
                        else { lines.Add($"staging{i}>lair: NO APPROACH to {P(home)} from {P(stagingPosts[i])}"); failed++; }
                    }
                }
                foreach (var (name, points) in trips)
                {
                    if (name.StartsWith("road:"))
                    {
                        // The zone road graph's own route length between two points of one zone (no Detour walk).
                        bool routed = AutonomousZoneRoads.TryRoute(nav, At(points[0]), points[0], points[1], out Vector3[] via);
                        lines.Add($"{name}: {(routed ? $"ROAD length={AutonomousTownTeleporters.RouteLength(points[0], via, points[1]):0} via={via.Length}" : "NO ROAD")} straight={Vector3.Distance(points[0], points[1]):0}");
                        continue;
                    }
                    Vector3 position = points[0];
                    float total = 0;
                    bool ok = true;
                    var legs = new List<string>();
                    bool Walk(Zone zone, Vector3 to, out float length)
                    {
                        length = 0;
                        for (int segment = 0; segment < 24; segment++)
                        {
                            AutonomousNavigationSurface.TryFloor(nav, zone, position, out Vector3 from);
                            PathfindingResult path = nav.GetPathStraight(zone, from, to, nav.DefaultFilters, nodes);
                            if (Pathfinder.TryRoadPath(nav, zone, from, to, nav.DefaultFilters, path, nodes, nodes.Length, out var road)) path = road;
                            if (path.NodeCount < 1) return false;
                            Vector3 last = from;
                            for (int i = 0; i < path.NodeCount; i++) { length += Vector3.Distance(last, nodes[i].Position); last = nodes[i].Position; }
                            bool progressed = Vector3.Distance(last, from) > 32;
                            position = last;
                            if (path.Status == PathfindingStatus.PathFound || Vector3.Distance(last, to) <= 64) return true;
                            if (path.Status != PathfindingStatus.PartialPathFound || !progressed) return false;
                        }
                        return false;
                    }
                    for (int leg = 1; leg < points.Length && ok; leg++)
                    {
                        Vector3 goal = points[leg];
                        Zone target = At(goal);
                        if (target == null || At(position) == null) { legs.Add($"  leg {leg}: point outside the loaded zones"); ok = false; break; }
                        int crossings = 0;
                        // Zones already crossed are not entered again, as the raid planner does (RealmRaidMuster.TryRoute).
                        var visited = new HashSet<ushort>();
                        while (ok && At(position) != target && crossings < 16)
                        {
                            Zone from = At(position);
                            visited.Add(from.ID);
                            if (!AutonomousZoneItinerary.ProbeNextStep(zones, region, from, target, position, goal, nav, out var step, new List<string>(),
                                    z => !visited.Contains(z.ID)))
                            { legs.Add($"  leg {leg}: no zone step {from.ID}->{target.ID} from {P(position)}"); ok = false; break; }
                            if (!Walk(from, step.Inside, out float inside))
                            { legs.Add($"  leg {leg}: no path in zone {from.ID} from {P(position)} to crossing {P(step.Inside)}"); ok = false; break; }
                            total += inside;
                            legs.Add($"    walked {inside:0} in zone {from.ID}");
                            legs.Add($"  leg {leg}: zone {from.ID} -> {At(step.Outside)?.ID} at {P(step.Outside)}");
                            position = step.Outside;
                            crossings++;
                        }
                        if (!ok) break;
                        if (!Walk(target, goal, out float final))
                        { legs.Add($"  leg {leg}: no path in zone {target.ID} to {P(goal)}, stopped at {P(position)} ({Vector3.Distance(position, goal):0} short)"); ok = false; break; }
                        total += final;
                        legs.Add($"    walked {final:0} in zone {target.ID} to the leg's end");
                    }
                    if (ok) complete++; else failed++;
                    lines.Add($"{name}: {(ok ? "COMPLETE" : "FAILED")} walked={total:0} straight={Vector3.Distance(points[0], points[^1]):0}");
                    if (!ok || name is "hub>staging0" or "hub>staging1" || !name.StartsWith("hub>") && !name.StartsWith("staging")) lines.AddRange(legs);
                }
            }
            finally
            {
                foreach (Zone zone in zones) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
            lines.Insert(0, $"complete={complete} failed={failed}");
            File.WriteAllLines(output, lines);
            TestContext.Progress.WriteLine(lines[0]);
        }
    }
}

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DOL.GS
{
    /// <summary>
    /// Walking topology, not a new mover. Rectangular zones need not tile the
    /// whole region: a straight chord toward a distant goal can leave the map.
    /// Choose adjacent zones, then let the existing Detour path follow each leg.
    /// Shared seam samples are bounded and cached, never sampled each AI tick.
    /// </summary>
    public static class AutonomousZoneItinerary
    {
        public readonly record struct Edge(Zone From, Zone To, bool Vertical, float Border, float Low, float High, int Direction)
        {
            public AutonomousZoneBoundaryRouting.Step At(float coordinate, float z)
            {
                coordinate = Math.Clamp(coordinate, Low + 96, High - 96);
                return Vertical
                    ? new(new(Border - Direction * 64, coordinate, z), new(Border + Direction * 64, coordinate, z))
                    : new(new(coordinate, Border - Direction * 64, z), new(coordinate, Border + Direction * 64, z));
            }
        }

        private sealed class Topology
        {
            public readonly Zone[] Zones;
            public readonly Dictionary<Zone, Edge[]> Edges;
            public readonly Dictionary<(Edge Edge, int Height), AutonomousZoneBoundaryRouting.Step[]> Samples = new();
            public Topology(IReadOnlyList<Zone> zones)
            {
                Zones = zones.ToArray();
                Edges = Zones.ToDictionary(zone => zone, zone => Zones
                    .Where(other => TryGetSharedEdge(zone, other, out _))
                    .Select(other => { TryGetSharedEdge(zone, other, out Edge edge); return edge; }).ToArray());
            }
        }

        private static readonly ConditionalWeakTable<Region, Topology> Topologies = new();

        public static bool TryGetSharedEdge(Zone from, Zone to, out Edge edge)
        {
            edge = default;
            if (from == null || to == null || from == to) return false;
            float low = Math.Max(from.YOffset, to.YOffset);
            float high = Math.Min(from.YOffset + from.Height, to.YOffset + to.Height);
            if (high - low >= 256)
            {
                if (from.XOffset + from.Width == to.XOffset)
                    edge = new(from, to, true, to.XOffset, low, high, 1);
                else if (to.XOffset + to.Width == from.XOffset)
                    edge = new(from, to, true, from.XOffset, low, high, -1);
            }
            if (edge.From != null) return true;
            low = Math.Max(from.XOffset, to.XOffset);
            high = Math.Min(from.XOffset + from.Width, to.XOffset + to.Width);
            if (high - low >= 256)
            {
                if (from.YOffset + from.Height == to.YOffset)
                    edge = new(from, to, false, to.YOffset, low, high, 1);
                else if (to.YOffset + to.Height == from.YOffset)
                    edge = new(from, to, false, from.YOffset, low, high, -1);
            }
            return edge.From != null;
        }

        public static Zone[] FindZoneRoute(IReadOnlyList<Zone> zones, Zone from, Zone to)
        {
            return FindZoneRoute(new Topology(zones), from, to, null);
        }

        private static Zone[] FindZoneRoute(Topology topology, Zone from, Zone to, HashSet<Edge> excluded, Func<Zone, bool> allowed = null)
        {
            var previous = new Dictionary<Zone, Zone> { [from] = null };
            var queue = new Queue<Zone>();
            queue.Enqueue(from);
            while (queue.TryDequeue(out Zone current))
            {
                if (current == to)
                {
                    var result = new List<Zone>();
                    for (Zone cursor = to; cursor != null; cursor = previous[cursor]) result.Add(cursor);
                    result.Reverse();
                    return result.ToArray();
                }
                if (!topology.Edges.TryGetValue(current, out Edge[] edges)) continue;
                foreach (Edge edge in edges)
                {
                    if (excluded?.Contains(edge) == true || previous.ContainsKey(edge.To) || allowed?.Invoke(edge.To) == false) continue;
                    previous[edge.To] = current;
                    queue.Enqueue(edge.To);
                }
            }
            return Array.Empty<Zone>();
        }

        public static bool TryNextStep(Region region, Zone from, Zone to, Vector3 start, Vector3 goal,
            IPathfindingMgr nav, out AutonomousZoneBoundaryRouting.Step step, Func<Zone, bool> allowed = null)
        {
            step = default;
            if (region == null || from == null || to == null || from == to || !nav.HasNavmesh(from)) return false;
            // Lough Gur's direct eastern seam reaches a Sheeroe cliff pocket.
            // The real connected road goes south through Bog of Cullen before
            // turning north. Apply the same proven approach to ordinary hunting
            // travel as to the dragon convoy; no relocation or goal change.
            if (region.ID == 200 && from.ID == 204 && to.ID == 216)
            {
                Vector3 road = new(361354, 750434, 4944);
                Zone via = region.GetZone((int)road.X, (int)road.Y);
                if (via != null && via != from && via != to && allowed?.Invoke(via) != false &&
                    TryNextStep(region, from, via, start, road, nav, out step, allowed)) return true;
            }
            // The direct Shannon/Silvermine -> Koalinth topology chooses a
            // high Lough Derg/Connacht seam near (319584,483264,11299).
            // Although each individual border is walkable, that Connacht
            // shelf has no connected continuation to Cliffs of Moher. Stage
            // the first two borders toward the installed lower Lough Derg
            // road instead. The ordinary final-zone corridor proof then
            // selects a low seam; no actor is moved or teleported here.
            if (region.ID == 200 && from.ID is 201 or 202 && to.ID == 203)
            {
                Vector3 road = new(342015, 498967, 4980);
                Zone via = region.GetZone((int)road.X, (int)road.Y);
                if (via != null && via != from && via != to && allowed?.Invoke(via) != false &&
                    TryNextStep(region, from, via, start, road, nav, out step, allowed)) return true;
            }
            // Same-XY altitude correction for planning only. The mover repairs
            // the actor on a failed raw path; never substitute another XY island.
            AutonomousNavigationSurface.TryFloor(nav, from, start, out start);
            Topology topology = Topologies.GetValue(region, key => new Topology(key.Zones));
            var excluded = new HashSet<Edge>();
            int maximumAlternatives = Math.Min(8,
                topology.Edges.TryGetValue(from, out Edge[] firstEdges) ? firstEdges.Length : 0);
            for (int alternative = 0; alternative < maximumAlternatives; alternative++)
            {
                Zone[] route = FindZoneRoute(topology, from, to, excluded, allowed);
                if (route.Length < 2 || route.Length > 32) return false;
                TryGetSharedEdge(from, route[1], out Edge edge);
                if (nav.HasNavmesh(edge.To) && TryEdgeStep(topology, edge, start, goal, nav, out step)) return true;
                excluded.Add(edge);
            }
            return false;
        }

        private static bool TryEdgeStep(Topology topology, Edge edge, Vector3 start, Vector3 goal,
            IPathfindingMgr nav, out AutonomousZoneBoundaryRouting.Step step)
        {
            step = default;
            // Prefer the direct intersection when it is legal. Unlike the old
            // chord, clamp it to the actual shared edge (never an empty map gap).
            float delta = edge.Vertical ? goal.X - start.X : goal.Y - start.Y;
            float t = Math.Abs(delta) > 0.001f ? Math.Clamp((edge.Border - (edge.Vertical ? start.X : start.Y)) / delta, 0, 1) : 0;
            float coordinate = edge.Vertical ? start.Y + (goal.Y - start.Y) * t : start.X + (goal.X - start.X) * t;
            var candidates = new List<AutonomousZoneBoundaryRouting.Step>();
            bool Resolve(AutonomousZoneBoundaryRouting.Step raw, out AutonomousZoneBoundaryRouting.Step resolved)
            {
                return AutonomousZoneBoundaryRouting.TryResolveHeights(raw.Inside, raw.Outside,
                    p => nav.GetClosestPoint(edge.From, p, 64, 64, 4096, nav.DefaultFilters),
                    p => nav.GetClosestPoint(edge.To, p, 64, 64, 4096, nav.DefaultFilters), out resolved) &&
                    Contains(edge.From, resolved.Inside) && Contains(edge.To, resolved.Outside);
            }
            if (Resolve(edge.At(coordinate, start.Z), out var direct)) candidates.Add(direct);
            // Cache at most 32 height bands per shared edge; bounded memory even
            // after many bots/camps. Sampling is geometry work, never world scans.
            int height = Math.Clamp((int)start.Z / 1024, 0, 31);
            AutonomousZoneBoundaryRouting.Step[] samples;
            lock (topology.Samples)
                topology.Samples.TryGetValue((edge, height), out samples);
            if (samples == null)
            {
                // Native projections can take time. Do not hold the region's
                // shared seam-cache lock while another worker needs a route.
                var valid = new List<AutonomousZoneBoundaryRouting.Step>();
                for (int i = 0; i <= 32; i++)
                {
                    float along = edge.Low + 96 + (edge.High - edge.Low - 192) * i / 32;
                    if (Resolve(edge.At(along, height * 1024 + 512), out var sample)) valid.Add(sample);
                }
                samples = valid.ToArray();
                lock (topology.Samples)
                    topology.Samples.TryAdd((edge, height), samples);
            }
            candidates.AddRange(samples);
            // Test a bounded but broad sample set.  The old four-candidate
            // limit routinely discarded the only walkable seam on large zone
            // borders, producing a false "no connected seam" and a new goal
            // even though the mesh had a valid crossing farther along the
            // border.  This is only reached when a boundary step is first
            // planned; the selected step is then retained by the mover.
            foreach (var candidate in candidates.OrderBy(p => Vector3.DistanceSquared(start, p.Inside) +
                         Vector3.DistanceSquared(p.Outside, goal)))
            {
                if (!HasCompleteCorridor(nav, edge.From, start, candidate.Inside)) continue;
                // When this crossing enters the destination zone, also prove
                // that its outside point belongs to the same walkable component
                // as the actual goal. A geometrically valid border sample can
                // otherwise drop a bot onto an isolated hill/ledge and make the
                // final leg retry forever (the Branelaedan Lough Derg case).
                if (Contains(edge.To, goal) &&
                    !HasCompleteCorridor(nav, edge.To, candidate.Outside, goal)) continue;
                step = candidate;
                return true;
            }
            return false;
        }

        public static bool HasCompleteCorridor(IPathfindingMgr nav, Zone zone, Vector3 start, Vector3 end)
        {
            // DF's stacked floors can yield DT_PARTIAL_RESULT with a fake
            // numerical endpoint after a multi-thousand-unit Z leap.  Its
            // route checks must use the strict complete 3-D validator; other
            // dungeons retain the existing bounded partial continuation.
            if (zone?.ZoneRegion?.ID == AutonomousDarknessFallsPolicy.RegionId)
                return AutonomousDarknessFallsNavigation.HasStrictSegment(nav, zone, start, end);
            // Opt-in memoization/yielding for keep planning ONLY. Every other
            // caller and the ordinary PvE corridor cache retain their behavior.
            if (nav is RvrPlanningNavigation work)
                return work.CompleteCorridor(zone, start, end, () => CalculateCompleteCorridor(nav, zone, start, end));
            // Only the installed provider supplies door/mesh revision tracking.
            // No quantization: even a one-unit position change gets a real query.
            if (nav is not LocalPathfindingMgr || zone == null)
                return CalculateCompleteCorridor(nav, zone, start, end);
            long revision = NavigationGeometryRevision.Read(zone);
            var key = new AutonomousCorridorCache.Key(start, end, revision);
            var cache = AutonomousCorridorCache.For(zone);
            if (cache.TryGet(key, out bool answer)) return answer;
            answer = CalculateCompleteCorridor(nav, zone, start, end);
            if (NavigationGeometryRevision.Read(zone) == revision) cache.Store(key, answer);
            return answer;
        }

        private static bool CalculateCompleteCorridor(IPathfindingMgr nav, Zone zone, Vector3 start, Vector3 end)
        {
            WrappedPathfindingNode[] nodes = ArrayPool<WrappedPathfindingNode>.Shared.Rent(512);
            try
            {
                Span<Vector3> visited = stackalloc Vector3[17];
                visited[0] = start;
                int visitedCount = 1;
                for (int segment = 0; segment < 16; segment++)
                {
                    PathfindingResult result = nav.GetPathStraight(zone, start, end, nav.DefaultFilters, nodes);
                    if (result.NodeCount < 1 || result.NodeCount > nodes.Length) return false;
                    Vector3 last = nodes[result.NodeCount - 1].Position;
                    // Detour can report a partial path when the endpoint is on
                    // the final polygon edge.  A near-end partial is already a
                    // complete usable corridor; requiring 240 units of travel
                    // here incorrectly rejected short gate/seam approaches.
                    if ((result.Status == PathfindingStatus.PathFound ||
                         result.Status == PathfindingStatus.PartialPathFound) &&
                        Vector3.DistanceSquared(last, end) <= 48 * 48) return true;
                    // A verified winding corridor may briefly move away from
                    // its goal or finish a short segment. Detect actual stalls
                    // and cycles instead of imposing straight-line progress.
                    if (result.Status != PathfindingStatus.PartialPathFound) return false;
                    for (int i = 0; i < visitedCount; i++)
                        if (Vector3.DistanceSquared(visited[i], last) < 1) return false;
                    visited[visitedCount++] = last;
                    start = last;
                }
                return false;
            }
            finally { ArrayPool<WrappedPathfindingNode>.Shared.Return(nodes); }
        }

        private static bool Contains(Zone zone, Vector3 p)
        {
            return p.X >= zone.XOffset && p.X < zone.XOffset + zone.Width &&
                p.Y >= zone.YOffset && p.Y < zone.YOffset + zone.Height;
        }
    }
}

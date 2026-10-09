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
            /// <summary>Only the borders the seam file lists (real ground meets there); null without seam data.</summary>
            public readonly Dictionary<Zone, Edge[]> SeamEdges;
            public readonly Dictionary<(Edge Edge, int Height), AutonomousZoneBoundaryRouting.Step[]> Samples = new();
            public readonly Dictionary<Edge, SeamStep[]> SeamSteps = new();
            public readonly Dictionary<Edge, SeamStep[][]> SeamAreas = new();
            public readonly ushort RegionId;
            public readonly bool Seams;
            public Topology(IReadOnlyList<Zone> zones, ushort regionId = 0)
            {
                Zones = zones.ToArray();
                RegionId = regionId != 0 ? regionId : Zones.FirstOrDefault()?.ZoneRegion?.ID ?? 0;
                Seams = RegionId != 0 && AutonomousZoneSeams.Covers(RegionId);
                Edges = Zones.ToDictionary(zone => zone, zone => Zones
                    .Where(other => TryGetSharedEdge(zone, other, out _))
                    .Select(other => { TryGetSharedEdge(zone, other, out Edge edge); return edge; }).ToArray());
                if (Seams)
                    SeamEdges = Edges.ToDictionary(pair => pair.Key, pair => pair.Value
                        .Where(edge => AutonomousZoneSeams.For(RegionId, edge.From.ID, edge.To.ID) != null).ToArray());
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

        private static Zone[] FindZoneRoute(Topology topology, Zone from, Zone to, HashSet<Edge> excluded, Func<Zone, bool> allowed = null,
            bool seamsOnly = false)
        {
            Dictionary<Zone, Edge[]> graph = seamsOnly && topology.SeamEdges != null ? topology.SeamEdges : topology.Edges;
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
                if (!graph.TryGetValue(current, out Edge[] edges)) continue;
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
            long profiled = NavQueryProfile.Start();
            try
            {
                // A failed step is a run of corridor proofs (1-3 s on long borders); the same start
                // spot, zone pair, goal area and allowed zones fail the same way for a while.
                step = default;
                if (region == null || from == null || to == null) return false;
                int allowedMask = 0;
                if (allowed != null)
                    foreach (Zone zone in region.Zones) allowedMask = allowedMask * 31 + (allowed(zone) ? zone.ID : 0);
                var key = (region.ID, from.ID, to.ID, (int)start.X >> 9, (int)start.Y >> 9, (int)start.Z >> 8,
                    (int)goal.X >> 11, (int)goal.Y >> 11, allowedMask);
                long now = GameLoop.GameLoopTime;
                if (FailedSteps.TryGetValue(key, out long until) && now < until) return false;
                if (SolvedSteps.TryGetValue(key, out var solved) && now < solved.Until)
                {
                    step = solved.Step;
                    return true;
                }
                if (TryNextStepCore(region, from, to, start, goal, nav, out step, allowed))
                {
                    if (SolvedSteps.Count > 50_000) SolvedSteps.Clear();
                    SolvedSteps[key] = (step, now + SolvedStepMemoryMilliseconds);
                    return true;
                }
                if (FailedSteps.Count > 50_000) FailedSteps.Clear();
                FailedSteps[key] = now + FailedStepMemoryMilliseconds;
                return false;
            }
            finally { NavQueryProfile.Stop(NavQueryProfile.Kind.ZoneStep, profiled); }
        }

        public const long FailedStepMemoryMilliseconds = 90_000;
        // Solved steps are shared the same way: a muster or raid sends dozens of bots from one town
        // toward one place, and each re-ran the same corridor proofs (zone steps were the main cost of
        // 113 of 138 AI turns over 300 ms in run 10).
        public const long SolvedStepMemoryMilliseconds = 120_000;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ushort, ushort, ushort, int, int, int, int, int, int),
            (AutonomousZoneBoundaryRouting.Step Step, long Until)> SolvedSteps = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ushort, ushort, ushort, int, int, int, int, int, int), long>
            FailedSteps = new();

        private static bool TryNextStepCore(Region region, Zone from, Zone to, Vector3 start, Vector3 goal,
            IPathfindingMgr nav, out AutonomousZoneBoundaryRouting.Step step, Func<Zone, bool> allowed)
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
            return TryNextStep(topology, from, to, start, goal, nav, out step, allowed);
        }

        [ThreadStatic] private static List<string> _probeTrace;

        /// <summary>
        /// Tool (UT_ZoneStepProbe): the same zone-step planning over a plain zone list, without a live
        /// region (no region-specific road staging), with a trace of every edge and candidate tried.
        /// </summary>
        public static bool ProbeNextStep(IReadOnlyList<Zone> zones, ushort regionId, Zone from, Zone to, Vector3 start, Vector3 goal,
            IPathfindingMgr nav, out AutonomousZoneBoundaryRouting.Step step, List<string> trace)
        {
            _probeTrace = trace;
            try
            {
                AutonomousNavigationSurface.TryFloor(nav, from, start, out start);
                return TryNextStep(new Topology(zones, regionId), from, to, start, goal, nav, out step, null);
            }
            finally { _probeTrace = null; }
        }

        private static bool TryNextStep(Topology topology, Zone from, Zone to, Vector3 start, Vector3 goal, IPathfindingMgr nav,
            out AutonomousZoneBoundaryRouting.Step step, Func<Zone, bool> allowed)
        {
            // A zone chain over borders where real ground meets (seam file) comes first; the full
            // border graph remains the fallback, so a border missing from the file never strands a bot.
            // A first border that failed in the seam pass for the same zone chain fails the same way
            // in the full pass (same start, goal, crossings and chain): it is skipped there.
            var failed = new HashSet<string>();
            if (topology.SeamEdges != null && TryRoutes(topology, from, to, start, goal, nav, allowed, true, failed, out step)) return true;
            return TryRoutes(topology, from, to, start, goal, nav, allowed, false, failed, out step);
        }

        private static bool TryRoutes(Topology topology, Zone from, Zone to, Vector3 start, Vector3 goal, IPathfindingMgr nav,
            Func<Zone, bool> allowed, bool seamsOnly, HashSet<string> failed, out AutonomousZoneBoundaryRouting.Step step)
        {
            step = default;
            var excluded = new HashSet<Edge>();
            int maximumAlternatives = Math.Min(8,
                topology.Edges.TryGetValue(from, out Edge[] firstEdges) ? firstEdges.Length : 0);
            for (int alternative = 0; alternative < maximumAlternatives; alternative++)
            {
                Zone[] route = FindZoneRoute(topology, from, to, excluded, allowed, seamsOnly);
                _probeTrace?.Add($"route seamsOnly={seamsOnly} [{string.Join(">", route.Select(z => z.ID))}]");
                if (route.Length < 2 || route.Length > 32) return false;
                TryGetSharedEdge(from, route[1], out Edge edge);
                string chain = string.Join(">", route.Select(z => z.ID));
                if (!failed.Contains(chain) && nav.HasNavmesh(edge.To) && TryEdgeStep(topology, edge, route, start, goal, nav, out step)) return true;
                excluded.Add(edge);
                failed.Add(chain);
            }
            return false;
        }

        private static bool TryEdgeStep(Topology topology, Edge edge, Zone[] route, Vector3 start, Vector3 goal,
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
            // Crossings known to join real ground (seam file) are tried first, nearest first; the
            // direct crossing and the usual border samples remain as the fallback.
            // Only crossings whose far-side ground continues, area by area, along the planned zone
            // chain (seam file area ids; several separate real networks share some borders).
            SeamStep[][] areas = SeamAreas(topology, edge, nav);
            if (areas != null)
            {
                // At most two per near-side area, best area first: crossings of one area share the
                // bot's reachability, so one unreachable area (a separate hillside in the same zone,
                // Salisbury Plains toward Black Mtns South) cannot use up every candidate. Groups
                // are cached per border; only the two best of each are picked here (no sorting of
                // every listed point: it was a top allocator).
                var reaches = new Dictionary<int, bool>();
                var reachesMain = new Dictionary<int, bool?>();
                var picks = new List<(float Score, SeamStep First, SeamStep? Second)>();
                foreach (SeamStep[] group in areas)
                {
                    SeamStep? best = null, next = null; float bestScore = float.MaxValue, nextScore = float.MaxValue;
                    foreach (SeamStep s in group)
                    {
                        if (!(reaches.TryGetValue(s.OutsideArea, out bool known) ? known
                                : reaches[s.OutsideArea] = ChainContinues(topology, route, s.OutsideArea))) continue;
                        float score = Vector3.DistanceSquared(start, s.Step.Inside) + Vector3.DistanceSquared(s.Step.Outside, goal);
                        if (score < bestScore) { next = best; nextScore = bestScore; best = s; bestScore = score; }
                        else if (score < nextScore) { next = s; nextScore = score; }
                    }
                    if (best.HasValue) picks.Add((bestScore, best.Value, next));
                }
                // Prefer crossings whose ground reaches the destination zone's main area when any does:
                // ridge networks touch several borders yet never reach the camps (run 15: 102 failures on
                // the East Svealand plateau above Vale of Mularn).
                foreach (var pick in picks)
                    if (!reachesMain.ContainsKey(pick.First.OutsideArea))
                        reachesMain[pick.First.OutsideArea] = ChainReachesMain(topology, route, pick.First.OutsideArea);
                if (picks.Any(pick => reachesMain[pick.First.OutsideArea] == true))
                    picks.RemoveAll(pick => reachesMain[pick.First.OutsideArea] == false);
                picks.Sort((a, b) => a.Score.CompareTo(b.Score));
                foreach (var pick in picks)
                {
                    if (candidates.Count >= SeamCandidates) break;
                    candidates.Add(pick.First.Step);
                    if (pick.Second.HasValue && candidates.Count < SeamCandidates) candidates.Add(pick.Second.Value.Step);
                }
            }
            int preferred = candidates.Count;
            if (Resolve(edge.At(coordinate, start.Z), out var direct)) candidates.Add(direct);
            candidates.AddRange(Samples(topology, edge, start.Z, nav));
            _probeTrace?.Add($" edge {edge.From.ID}>{edge.To.ID} start={start} seams={preferred} candidates={candidates.Count}");
            // Test a bounded but broad sample set.  The old four-candidate
            // limit routinely discarded the only walkable seam on large zone
            // borders, producing a false "no connected seam" and a new goal
            // even though the mesh had a valid crossing farther along the
            // border.  This is only reached when a boundary step is first
            // planned; the selected step is then retained by the mover.
            int index = 0;
            foreach (var candidate in candidates.Take(preferred).Concat(candidates.Skip(preferred)
                         .OrderBy(p => Vector3.DistanceSquared(start, p.Inside) + Vector3.DistanceSquared(p.Outside, goal))))
            {
                bool listed = index++ < preferred;
                // With seam data an unlisted crossing takes the area of the listed point at the same
                // spot: none means it is not on real ground, and passing through, its ground must
                // continue along the zone chain. Checked first: data lookups, no corridor proofs.
                int? area = listed ? null : SeamAreaAt(topology, edge, candidate);
                if (area == NoSeamArea || area is int known && !Contains(edge.To, goal) && !ChainContinues(topology, route, known))
                {
                    _probeTrace?.Add($"  {edge.From.ID}>{edge.To.ID} sample {candidate.Outside} not on continuing ground");
                    continue;
                }
                if (!HasCompleteCorridor(nav, edge.From, start, candidate.Inside))
                {
                    _probeTrace?.Add($"  {edge.From.ID}>{edge.To.ID} {(listed ? "seam" : "sample")} {candidate.Inside} no corridor from start");
                    continue;
                }
                // When this crossing enters the destination zone, also prove
                // that its outside point belongs to the same walkable component
                // as the actual goal. A geometrically valid border sample can
                // otherwise drop a bot onto an isolated hill/ledge and make the
                // final leg retry forever (the Branelaedan Lough Derg case).
                if (Contains(edge.To, goal) &&
                    !HasCompleteCorridor(nav, edge.To, candidate.Outside, goal))
                {
                    _probeTrace?.Add($"  {edge.From.ID}>{edge.To.ID} {(listed ? "seam" : "sample")} {candidate.Outside} no corridor to goal");
                    continue;
                }
                // Passing through: the far side must also reach a crossing of the next zone on
                // the way. With every client wall in the meshes a geometrically valid border
                // sample can sit on a walled-off mountain shelf (Bri Leith and Camelot Hills
                // seams at y=483392, 2026-10-06) from which nothing continues.
                // A crossing listed in the seam file is already proven to be on real ground; a
                // whole-zone corridor proof is longer than the corridor check can verify
                // (Cruachan Gorge, 2026-10-06). Unlisted fallback crossings still get the check.
                // Without seam area data the one-zone corridor look-ahead below still applies.
                if (!listed && area == null && !Contains(edge.To, goal) && !ContinuesToward(topology, edge, candidate.Outside, goal, nav))
                {
                    _probeTrace?.Add($"  {edge.From.ID}>{edge.To.ID} sample {candidate.Outside} does not continue");
                    continue;
                }
                _probeTrace?.Add($"  {edge.From.ID}>{edge.To.ID} {(listed ? "seam" : "sample")} chosen {candidate.Inside} > {candidate.Outside}");
                step = candidate;
                return true;
            }
            return false;
        }

        private static bool Resolve(Edge edge, IPathfindingMgr nav, AutonomousZoneBoundaryRouting.Step raw,
            out AutonomousZoneBoundaryRouting.Step resolved) =>
            AutonomousZoneBoundaryRouting.TryResolveHeights(raw.Inside, raw.Outside,
                p => nav.GetClosestPoint(edge.From, p, 64, 64, 4096, nav.DefaultFilters),
                p => nav.GetClosestPoint(edge.To, p, 64, 64, 4096, nav.DefaultFilters), out resolved) &&
            Contains(edge.From, resolved.Inside) && Contains(edge.To, resolved.Outside);

        // Cache at most 32 height bands per shared edge; bounded memory even
        // after many bots/camps. Sampling is geometry work, never world scans.
        private static AutonomousZoneBoundaryRouting.Step[] Samples(Topology topology, Edge edge, float z, IPathfindingMgr nav)
        {
            int height = Math.Clamp((int)z / 1024, 0, 31);
            AutonomousZoneBoundaryRouting.Step[] samples;
            lock (topology.Samples)
                topology.Samples.TryGetValue((edge, height), out samples);
            if (samples != null) return samples;
            // Native projections can take time. Do not hold the region's
            // shared seam-cache lock while another worker needs a route.
            var valid = new List<AutonomousZoneBoundaryRouting.Step>();
            for (int i = 0; i <= 32; i++)
            {
                float along = edge.Low + 96 + (edge.High - edge.Low - 192) * i / 32;
                if (Resolve(edge, nav, edge.At(along, height * 1024 + 512), out var sample)) valid.Add(sample);
            }
            samples = valid.ToArray();
            lock (topology.Samples)
                topology.Samples.TryAdd((edge, height), samples);
            return samples;
        }

        public const int ContinuationChecks = 4;
        public const int SeamCandidates = 24;
        public const int SeamsPerArea = 2;

        /// <summary>A listed crossing snapped to the meshes, with the walkable area id on each side (-1: unknown).</summary>
        private readonly record struct SeamStep(AutonomousZoneBoundaryRouting.Step Step, int InsideArea, int OutsideArea);

        /// <summary>
        /// True when the ground on the far side of a crossing (its area in route[1]) continues through
        /// every following border of the route by the seam file's area ids. Unknown data passes.
        /// </summary>
        private static bool ChainContinues(Topology topology, Zone[] route, int outsideArea)
        {
            if (outsideArea < 0) return true;
            var areas = new HashSet<int> { outsideArea };
            for (int i = 1; i + 1 < route.Length; i++)
            {
                AutonomousZoneSeams.Crossing[] next = AutonomousZoneSeams.For(topology.RegionId, route[i].ID, route[i + 1].ID);
                if (next == null || next.Any(c => c.InsideArea < 0)) return true;
                var reached = new HashSet<int>(next.Where(c => areas.Contains(c.InsideArea)).Select(c => c.OutsideArea));
                if (reached.Count == 0) return false;
                areas = reached;
            }
            return true;
        }

        /// <summary>
        /// Whether the ground past a crossing (its area in route[1]) reaches the main area of the route's
        /// last zone through the seam file's area ids; null when the file has no main area or area data.
        /// </summary>
        private static bool? ChainReachesMain(Topology topology, Zone[] route, int outsideArea)
        {
            if (outsideArea < 0 || route.Length < 2) return null;
            int? main = AutonomousZoneSeams.MainArea(topology.RegionId, route[^1].ID);
            if (main == null) return null;
            var areas = new HashSet<int> { outsideArea };
            for (int i = 1; i + 1 < route.Length; i++)
            {
                AutonomousZoneSeams.Crossing[] next = AutonomousZoneSeams.For(topology.RegionId, route[i].ID, route[i + 1].ID);
                if (next == null || next.Any(c => c.InsideArea < 0)) return null;
                var reached = new HashSet<int>(next.Where(c => areas.Contains(c.InsideArea)).Select(c => c.OutsideArea));
                if (reached.Count == 0) return false;
                areas = reached;
            }
            return areas.Contains(main.Value);
        }

        private const int NoSeamArea = int.MinValue;

        /// <summary>
        /// The far-side area of the listed seam point at a crossing's spot (within 96 along the border
        /// and 160 in height); <see cref="NoSeamArea"/> when the border is listed but no point matches
        /// (not on real ground); null when the border has no area data (older file or no seam data).
        /// </summary>
        private static int? SeamAreaAt(Topology topology, Edge edge, AutonomousZoneBoundaryRouting.Step candidate)
        {
            if (!topology.Seams) return null;
            AutonomousZoneSeams.Crossing[] listed = AutonomousZoneSeams.For(topology.RegionId, edge.From.ID, edge.To.ID);
            if (listed == null || listed.Length == 0 || listed[0].InsideArea < 0) return null;
            float along = edge.Vertical ? candidate.Inside.Y : candidate.Inside.X;
            foreach (var point in listed)
                if (MathF.Abs(point.Along - along) <= 96 && MathF.Abs(point.InsideZ - candidate.Inside.Z) <= 160)
                    return point.OutsideArea;
            return NoSeamArea;
        }

        /// <summary>The listed crossings of a border grouped by near-side area (cached with the steps).</summary>
        private static SeamStep[][] SeamAreas(Topology topology, Edge edge, IPathfindingMgr nav)
        {
            if (!topology.Seams) return null;
            lock (topology.SeamAreas)
                if (topology.SeamAreas.TryGetValue(edge, out var cached)) return cached;
            SeamStep[] steps = SeamSteps(topology, edge, nav);
            SeamStep[][] grouped = steps?.GroupBy(s => s.InsideArea).Select(g => g.ToArray()).ToArray();
            lock (topology.SeamAreas) topology.SeamAreas[edge] = grouped;
            return grouped;
        }

        /// <summary>The listed real-ground crossings of a border, snapped to the meshes once and cached.</summary>
        private static SeamStep[] SeamSteps(Topology topology, Edge edge, IPathfindingMgr nav)
        {
            if (!topology.Seams) return null;
            lock (topology.SeamSteps)
                if (topology.SeamSteps.TryGetValue(edge, out var cached)) return cached;
            AutonomousZoneSeams.Crossing[] listed = AutonomousZoneSeams.For(topology.RegionId, edge.From.ID, edge.To.ID);
            if (listed == null) return null;
            var steps = new List<SeamStep>(listed.Length / 2 + 1);
            // Every second listed point (128 units apart) is plenty and halves the snapping work.
            for (int i = 0; i < listed.Length; i += 2)
            {
                var raw = edge.At(listed[i].Along, listed[i].InsideZ);
                raw = new(raw.Inside with { Z = listed[i].InsideZ }, raw.Outside with { Z = listed[i].OutsideZ });
                if (Resolve(edge, nav, raw, out var step)) steps.Add(new(step, listed[i].InsideArea, listed[i].OutsideArea));
            }
            var result = steps.ToArray();
            lock (topology.SeamSteps) topology.SeamSteps[edge] = result;
            return result;
        }

        /// <summary>
        /// One zone of look-ahead: from a crossing's far side, a complete corridor to at least one of
        /// the next border's crossings toward the goal (the closest few only). Unknown topology passes.
        /// </summary>
        private static bool ContinuesToward(Topology topology, Edge edge, Vector3 outside, Vector3 goal, IPathfindingMgr nav)
        {
            Zone goalZone = topology.Zones.FirstOrDefault(zone => Contains(zone, goal));
            if (goalZone == null || goalZone == edge.To || !nav.HasNavmesh(edge.To)) return true;
            Zone[] route = FindZoneRoute(topology, edge.To, goalZone, null);
            if (route.Length < 2 || !TryGetSharedEdge(edge.To, route[1], out Edge next) || !nav.HasNavmesh(next.To)) return true;
            var samples = SeamSteps(topology, next, nav)?.Select(s => s.Step).ToArray() ?? Samples(topology, next, outside.Z, nav);
            if (samples.Length == 0) return true;
            foreach (var sample in samples.OrderBy(s => Vector3.DistanceSquared(outside, s.Inside) + Vector3.DistanceSquared(s.Outside, goal))
                         .Take(ContinuationChecks))
                if (HasCompleteCorridor(nav, edge.To, outside, sample.Inside)) return true;
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
            long profiled = NavQueryProfile.Start();
            try { return CalculateCompleteCorridorCore(nav, zone, start, end); }
            finally { NavQueryProfile.Stop(NavQueryProfile.Kind.Corridor, profiled); }
        }

        private static bool CalculateCompleteCorridorCore(IPathfindingMgr nav, Zone zone, Vector3 start, Vector3 end)
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

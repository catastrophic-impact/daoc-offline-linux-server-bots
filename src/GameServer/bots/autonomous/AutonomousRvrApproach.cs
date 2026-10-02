using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DOL.GS;

/// <summary>
/// Selects connected ground on the outside perimeter of a keep. Keep database
/// coordinates frequently identify an interior wall/tower component and are
/// not valid walking goals. This never moves an actor; it only rejects bad
/// endpoints and returns a Detour-proven approach on the actor's component.
/// </summary>
public static class AutonomousRvrApproach
{
    private static readonly int[] Rings = [1_800, 2_400, 3_200, 1_200, 800];
    private static readonly ConditionalWeakTable<Region, Dictionary<(eRealm, ushort, Vector3), Vector3>> Resolved = new();

    public static bool TryGateApproach(IPathfindingMgr nav, Region region, eRealm realm,
        Vector3 actor, Vector3 center, IEnumerable<Vector3> closedGates, out Vector3 approach, int lateral = 0)
    {
        approach=default;
        if (nav is not (AutonomousKeepApproachNavigation or RvrPlanningNavigation)) nav=new AutonomousKeepApproachNavigation(nav,[]);
        // The outer gate is farthest from the keep's center. Once destroyed it
        // disappears from this list, exposing the next closed gate inwards.
        foreach(var gate in closedGates.OrderByDescending(p=>Vector2.DistanceSquared(new(p.X,p.Y),new(center.X,center.Y))))
        {
            Vector2 away=new(gate.X-center.X,gate.Y-center.Y);
            if(away.LengthSquared()<1)continue;
            away=Vector2.Normalize(away);
            // Inner tower doors need not face away from the keep's database
            // center. Test both sides, but accept ONLY the side reachable
            // without crossing any intact enemy door. This allows advancing
            // through the outer breach without aiming behind the inner gate.
            foreach(var direction in new[]{away,-away,new Vector2(-away.Y,away.X),new Vector2(away.Y,-away.X)})
            foreach(int distance in new[]{400,600,900})
            foreach(int side in new[]{Math.Clamp(lateral,-480,480),0,160,-160})
            {
                Vector3 raw=gate+new Vector3(direction.X*distance-direction.Y*side,direction.Y*distance+direction.X*side,0);
                var zone=region.GetZone((int)raw.X,(int)raw.Y);
                if(zone==null || !nav.HasNavmesh(zone))continue;
                var floor=nav.GetClosestPoint(zone,raw,48,48,256,nav.DefaultFilters);
                if(!floor.HasValue || !AutonomousRendezvousNavigation.HasLocalExit(nav,zone,floor.Value) ||
                    !RvrKeepRoute.TryBuild(region,nav,realm,actor,floor.Value,out _))continue;
                approach=floor.Value;return true;
            }
        }
        return false;
    }

    public static bool TryResolveAcrossZones(IPathfindingMgr nav, Region region, eRealm realm,
        Vector3 actor, Vector3 keep, out Vector3 approach)
    {
        approach = default;
        Zone from = region?.GetZone((int)actor.X, (int)actor.Y);
        Zone to = region?.GetZone((int)keep.X, (int)keep.Y);
        if (from == null || to == null || nav == null || !nav.IsAvailable || !nav.HasNavmesh(to)) return false;
        if (nav is not (AutonomousKeepApproachNavigation or RvrPlanningNavigation)) nav = new AutonomousKeepApproachNavigation(nav, []);
        var cache = Resolved.GetOrCreateValue(region);
        var key = (realm, from.ID, keep);
        Vector3? previous;
        // A cooperative request replays across ticks. Other responders may
        // replace this shared candidate between slices; keep that request's
        // candidate order deterministic rather than switching endpoints mid-search.
        lock(cache) previous = nav is not RvrPlanningNavigation && cache.TryGetValue(key,out var stored) ? stored : null;
        // Reuse only the candidate, NEVER its old reachability result. Doors,
        // ownership and the actor's current mesh component are checked afresh.
        if (previous.HasValue && RvrKeepRoute.TryBuild(region,nav,realm,actor,previous.Value,out _))
        { approach=previous.Value;return true; }
        void Remember(Vector3 point)
        {
            lock(cache) { if(cache.Count>=128)cache.Clear();cache[key]=point; }
        }
        if (from == to)
        {
            if (!TryResolve(nav,to,actor,keep,out approach)) return false;
            Remember(approach); return true;
        }
        // Project the perimeter BEFORE selecting a zone seam. The raw keep
        // center may be a walled-off component even when its exterior is reachable.
        foreach (int radius in Rings)
        {
            float bearing = MathF.Atan2(actor.Y - keep.Y, actor.X - keep.X);
            for (int step = 0; step < 12; step++)
            {
                float angle = bearing + step * MathF.PI / 6;
                Vector3 raw = new(keep.X + MathF.Cos(angle) * radius, keep.Y + MathF.Sin(angle) * radius, keep.Z);
                Vector3? floor = nav.GetClosestPoint(to, raw, 96, 96, 4096, nav.DefaultFilters);
                if (!floor.HasValue || !AutonomousRendezvousNavigation.HasLocalExit(nav, to, floor.Value) ||
                    !RvrKeepRoute.TryBuild(region, nav, realm, actor, floor.Value,out _)) continue;
                approach = floor.Value;
                Remember(approach);
                return true;
            }
        }
        return false;
    }

    public static bool TryResolve(IPathfindingMgr nav, Zone zone, Vector3 actor, Vector3 keep,
        out Vector3 approach)
    {
        approach = default;
        if (nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
        {
            approach = keep;
            return true;
        }
        if (!AutonomousNavigationSurface.TryFloor(nav, zone, actor, out Vector3 start))
            return false;

        foreach (int radius in Rings)
        {
            var candidates = new List<Vector3>();
            for (int degrees = 0; degrees < 360; degrees += 15)
            {
                float angle = degrees * MathF.PI / 180f;
                Vector3 raw = new(keep.X + MathF.Cos(angle) * radius,
                    keep.Y + MathF.Sin(angle) * radius, start.Z);
                Vector3? floor = nav.GetClosestPoint(zone, raw, 96, 96, 4_096, nav.DefaultFilters);
                if (!floor.HasValue || !float.IsFinite(floor.Value.X) || !float.IsFinite(floor.Value.Y) ||
                    !float.IsFinite(floor.Value.Z) || !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value))
                    continue;
                float distance = Vector2.Distance(new(floor.Value.X, floor.Value.Y), new(keep.X, keep.Y));
                if (distance is < 650 or > 3_500 ||
                    candidates.Any(existing => Vector3.DistanceSquared(existing, floor.Value) < 48 * 48))
                    continue;
                candidates.Add(floor.Value);
            }

            foreach (Vector3 candidate in candidates.OrderBy(point => Vector3.DistanceSquared(start, point)))
            {
                if (!AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, start, candidate))
                    continue;
                approach = candidate;
                return true;
            }
        }
        return false;
    }
}

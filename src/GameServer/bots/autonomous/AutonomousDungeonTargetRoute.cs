using System.Numerics;

namespace DOL.GS
{
    /// <summary>Proactive pulls require a walkable return route, not visibility alone.</summary>
    public static class AutonomousDungeonTargetRoute
    {
        public static bool CanReach(IPathfindingMgr nav, Zone zone, Vector3 from, Vector3 target)
        {
            if (!AutonomousNavigationSurface.TryFloor(nav, zone, from, out Vector3 start)) return false;
            // Imported NPCs sometimes stand a few units outside an eroded
            // polygon. Prove a melee-range approach, not an exact actor-floor
            // snap, so valid wall-edge spawns remain usable. Normal attack LOS
            // remains required by the caller; no movement/teleport is done here.
            Vector3? end = nav.GetClosestPoint(zone, target, 96, 96, 96, nav.DefaultFilters);
            if (end.HasValue && Vector3.DistanceSquared(end.Value, target) <= 112 * 112 &&
                AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, start, end.Value) &&
                AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, end.Value, start)) return true;
            // The nearest polygon can be the wrong side of a wall edge. Probe
            // an actual melee-range approach only after the ordinary fast path
            // fails; never relax range, attack LOS or return connectivity.
            // Reject an unsuitable candidate inside the search, not after it:
            // the closest reachable point can be behind an obstruction while
            // another legal melee point has LOS and a safe return corridor.
            return AutonomousZonePointApproach.TryResolve(nav, zone, start, target, 112, out _,
                approach => Vector3.DistanceSquared(approach, target) <= 112 * 112 &&
                    nav.HasLineOfSight(zone, approach, target, nav.DefaultFilters) &&
                    AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, approach, start));
        }
    }
}

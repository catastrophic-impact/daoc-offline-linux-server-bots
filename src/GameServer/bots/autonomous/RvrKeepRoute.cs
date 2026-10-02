using System;
using System.Collections.Generic;
using System.Numerics;

namespace DOL.GS;

/// <summary>RvR-only walking itinerary. Keep every proved seam and road waypoint;
/// do not replan the next leg back through a zone already visited.</summary>
public static class RvrKeepRoute
{
    public static bool TryBuild(Region region, IPathfindingMgr nav, eRealm realm,
        Vector3 start, Vector3 goal, out Vector3[] route)
    {
        route = [];
        var from = region?.GetZone((int)start.X, (int)start.Y);
        var target = region?.GetZone((int)goal.X, (int)goal.Y);
        if (from == null || target == null) return false;
        var points = new List<Vector3>();
        var visited = new HashSet<Zone>();
        // These are existing, installed-mesh-tested dragon-road anchors. The
        // tempting northern/eastern Sheeroe seams strand a returning defender.
        if (region.ID == 200 && realm == eRealm.Hibernia && from.ID is 216 or 205 && target.ID is not (216 or 205))
        {
            if (!Leg(new(361354, 750434, 4944)) || !Leg(new(334820, 719979, 4296))) return false;
        }
        if (!Leg(goal)) return false;
        route = points.ToArray();
        return true;

        bool Leg(Vector3 end)
        {
            var to = region.GetZone((int)end.X, (int)end.Y);
            if (to == null || !AutonomousRealmBoundary.Allows(realm, region.ID, to.ID)) return false;
            while (from != to)
            {
                if (nav is RvrPlanningNavigation work) work.Checkpoint();
                if (!visited.Add(from) || visited.Count > 24) return false;
                if (!AutonomousZoneItinerary.TryNextStep(region, from, to, start, end, nav, out var step,
                    z => !visited.Contains(z) && AutonomousRealmBoundary.Allows(realm, region.ID, z.ID))) return false;
                points.Add(step.Inside); points.Add(step.Outside);
                start = step.Outside;
                from = region.GetZone((int)start.X, (int)start.Y);
                if (from == null) return false;
            }
            if (!AutonomousZoneItinerary.HasCompleteCorridor(nav, from, start, end)) return false;
            points.Add(end); start = end;
            return true;
        }
    }
}

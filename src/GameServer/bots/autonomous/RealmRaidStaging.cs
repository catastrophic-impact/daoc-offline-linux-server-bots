using System;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS;

public static class RealmRaidStaging
{
    public static bool TryDungeonPost(IPathfindingMgr nav, Zone exterior, Vector3 portal, int slot,
        IReadOnlyCollection<Vector3> occupied, out Vector3 staging)
    {
        staging = default;
        if (exterior == null || nav == null || !nav.IsAvailable || !nav.HasNavmesh(exterior)) return false;
        for (int attempt = 0; attempt < 36; attempt++)
        {
            double angle = (attempt * 30 + slot * 137.5) * Math.PI / 180;
            float radius = 500 + slot % 5 * 200 + (slot >= 30 ? attempt / 12 * 250 : 0);
            Vector3 raw = portal + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
            Vector3? floor = nav.GetClosestPoint(exterior, raw, 64, 64, 512, nav.DefaultFilters);
            if (!floor.HasValue || !AutonomousRendezvousNavigation.HasLocalExit(nav, exterior, floor.Value) ||
                occupied.Any(p => Vector3.DistanceSquared(p, floor.Value) < 200 * 200) ||
                (occupied.Count > 0 && !AutonomousZoneItinerary.HasCompleteCorridor(nav,exterior,occupied.First(),floor.Value)) ||
                !AutonomousZonePointApproach.TryResolve(nav, exterior, floor.Value, portal, 220, out _)) continue;
            staging = floor.Value;
            return true;
        }
        return false;
    }
    public static bool TryDragonPost(IPathfindingMgr nav, Zone zone, Vector3 home, int slot, IReadOnlyCollection<Vector3> occupied, out Vector3 staging)
    {
        staging = default;
        if (slot < 0 || slot >= RealmRaidRecruitmentPolicy.MaximumParties || nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
        for (int attempt = 0; attempt < 36; attempt++)
        {
            double angle = (attempt * 47.5 + slot * 137.5) * Math.PI / 180;
            float radius = 3600 + attempt % 6 * 500;
            var raw = home + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
            var floor = nav.GetClosestPoint(zone, raw, 96, 96, 4096, nav.DefaultFilters);
            if (!floor.HasValue || Math.Abs(floor.Value.Z - home.Z) > 768 ||
                occupied.Any(p => Vector3.DistanceSquared(p, floor.Value) < 600 * 600) ||
                !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value) ||
                !AutonomousZonePointApproach.TryResolve(nav, zone, floor.Value, home, 300, out _) ||
                !HasOpenPartySpace(nav, zone, floor.Value)) continue;
            staging = floor.Value;
            return true;
        }
        return false;
    }

    // Prove a party can spread around the post, not merely that one nav polygon exists.
    public static bool HasOpenPartySpace(IPathfindingMgr nav, Zone zone, Vector3 center)
    {
        if (nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
        foreach (Vector2 offset in new[] { new Vector2(260, 0), new(-260, 0), new(0, 260), new(0, -260) })
        {
            var raw = center + new Vector3(offset, 0);
            var floor = nav.GetClosestPoint(zone, raw, 32, 32, 128, nav.DefaultFilters);
            if (!floor.HasValue || Math.Abs(floor.Value.Z - center.Z) > 128 ||
                !nav.HasLineOfSight(zone, center, floor.Value, nav.DefaultFilters) ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, center, floor.Value) ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, center)) return false;
        }
        return true;
    }
}

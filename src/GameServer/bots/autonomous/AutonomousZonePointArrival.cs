using System.Numerics;
using DOL.Database;

namespace DOL.GS;

/// <summary>
/// Resolves an autonomous bot's server-side portal landing to the same local
/// navmesh component without changing the authoritative region edge. This is
/// evaluated once at a real crossing, never in the movement hot path.
/// </summary>
public static class AutonomousZonePointArrival
{
    public static bool TryResolve(DbZonePoint edge, out Vector3 arrival)
    {
        arrival = edge == null ? default : new(edge.TargetX, edge.TargetY, edge.TargetZ);
        if (edge == null)
            return false;

        Region region = WorldMgr.GetRegion(edge.TargetRegion);
        Zone zone = region?.GetZone(edge.TargetX, edge.TargetY);
        if (zone == null)
            return false;

        // Dungeon portals often intentionally land in compact entrance cells.
        // Preserve their authored destination; dungeon corridors are separately
        // validated by the dungeon goal catalog.
        if (region.IsDungeon || zone.IsDungeon)
            return true;

        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (!nav.IsAvailable || !nav.HasNavmesh(zone))
            return true; // Retain legacy behavior where this client has no mesh.

        if (AutonomousRouteHotspotRepair.TryResolveAuditedPortalLanding(nav, region, edge, out Vector3 audited))
        {
            arrival = audited;
            return true;
        }

        return AutonomousRendezvousNavigation.TryChoosePoint(nav, zone, arrival, out arrival);
    }
}

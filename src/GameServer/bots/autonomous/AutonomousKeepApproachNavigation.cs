using System;
using System.Linq;
using System.Numerics;

namespace DOL.GS;

/// <summary>Planning-only view that honors the mover's closed-door restrictions.
/// Does not change meshes, doors or the shared pathfinding provider.</summary>
public sealed class AutonomousKeepApproachNavigation : PathfindingMgrBase
{
    private readonly IPathfindingMgr _nav;
    private readonly Vector3[] _friendlyDoors;
    public AutonomousKeepApproachNavigation(IPathfindingMgr nav, Vector3[] friendlyDoors)
    { _nav = nav; _friendlyDoors = friendlyDoors; }

    public static IPathfindingMgr ForRealm(IPathfindingMgr nav, Region region, eRealm realm)
    {
        if (nav == null || nav is AutonomousKeepApproachNavigation) return nav;
        if (realm==eRealm.None || region==null) return new AutonomousKeepApproachNavigation(nav,[]);
        var doors = GameServer.KeepManager?.GetKeepsOfRegion(region.ID)
            .Where(k => k.Realm == realm).SelectMany(k => k.Doors.Values)
            .Select(d => new Vector3(d.X,d.Y,d.Z)).ToArray() ?? [];
        return new AutonomousKeepApproachNavigation(nav, doors);
    }

    public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
        EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
    {
        var result = _nav.GetPathStraight(zone,start,end,filters,destination);
        for (int i = 0; i < Math.Min(result.NodeCount,destination.Length); i++)
        {
            var node = destination[i];
            if ((node.Flags & EDtPolyFlags.BlockingDoor) == 0) continue;
            bool friendly = false;
            foreach (var door in _friendlyDoors)
                if (Vector3.DistanceSquared(door,node.Position) <= Pathfinder.DOOR_SEARCH_DISTANCE * Pathfinder.DOOR_SEARCH_DISTANCE)
                { friendly = true; break; }
            if (!friendly)
            {
                var alternative=_nav.GetPathStraight(zone,start,end,_nav.BlockingDoorAvoidanceFilters,destination);
                // Match Pathfinder.TryApplyAlternativePath exactly: a partial
                // route ending at the outside of the wall is NOT permission
                // to aim for an interior endpoint, even when only 48 units away.
                return alternative.Status==PathfindingStatus.PathFound ? alternative : new(PathfindingStatus.NoPathFound,0);
            }
        }
        return result;
    }
    public override bool IsAvailable => _nav.IsAvailable;
    public override bool HasNavmesh(Zone zone) => _nav.HasNavmesh(zone);
    public override EDtPolyFlags[] DefaultFilters => _nav.DefaultFilters;
    public override EDtPolyFlags[] BlockingDoorAvoidanceFilters => _nav.BlockingDoorAvoidanceFilters;
    public override Vector3? GetClosestPoint(Zone zone, Vector3 point, float x, float y, float z, EDtPolyFlags[] filters)
        => _nav.GetClosestPoint(zone,point,x,y,z,filters);
    public override Vector3? GetClosestPoint(Zone zone, Vector3 point, EDtPolyFlags[] filters)
        => _nav.GetClosestPoint(zone,point,filters);
    public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters)
        => _nav.HasLineOfSight(zone,start,end,filters);
}

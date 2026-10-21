using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DOL.GS;

public static class AutonomousNavigationSurface
{
    // Persisted world coordinates are integral while Detour floors are
    // fractional, and some city geometry records its standing height slightly
    // above the Detour polygon. This remains deliberately small:
    // it repairs rounding/step drift without snapping down through a bridge,
    // shelf, upper floor, or other legitimate stacked surface.
    private const float MaximumDownwardFloorCorrection = 32f;

    public static Vector3? MoveAlongGround(IPathfindingMgr nav, Zone zone, Vector3 start, Vector3 end)
    {
        if (nav is LocalPathfindingMgr local)
            return local.GetMoveAlongSurfaceGrounded(zone, start, end, nav.DefaultFilters);
        // No guessed height/nearest island for unsupported remote providers.
        return null;
    }
    // Repair stale imported/interpolated Z, not horizontal path obstruction.
    // A wide nearest-poly search can select a table, another floor, or the far
    // side of a wall. Keep XY effectively fixed and retain bounded vertical range.
    public static bool TryFloor(IPathfindingMgr nav, Zone zone, Vector3 position, out Vector3 floor)
    {
        floor = position;
        if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
        Vector3? candidate = nav.GetClosestPoint(zone, position, 2, 2, 128, nav.DefaultFilters);
        if (!candidate.HasValue || !IsLocalFloor(position, candidate.Value)) return false;
        floor = candidate.Value;
        return true;
    }

    /// <summary>
    /// The nearest real floor for an actor standing just off the walkable mesh: a ledge, a prop edge or a saved logout
    /// spot (Vindsaul Faste and Caer Diogel, 2026-10-13: bots 50-260 units off the floor failed every route they planned
    /// and blocked their raid party), or standing on a small cut-off scrap of mesh beside the real floor (Aegir's Landing
    /// teleporter, 2026-10-13). False when the actor already stands on floor with a verified local way out, or when no
    /// floor within <paramref name="maxLateral"/> outside a known walled-off pocket has one.
    /// </summary>
    public static bool TryNearestWalkableFloor(IPathfindingMgr nav, Zone zone, Vector3 position, float maxLateral, out Vector3 floor,
        bool leavePocket = false)
    {
        floor = position;
        if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
            return false;
        bool onMesh = TryFloor(nav, zone, position, out Vector3 here);
        // leavePocket: the bot stands in a listed walled-off pocket; its patch can look fine locally, so always search.
        if (onMesh && !leavePocket && AutonomousRendezvousNavigation.HasLocalExit(nav, zone, here))
            return false;
        bool Usable(Vector3 p) =>
            Vector2.Distance(new(p.X, p.Y), new(position.X, position.Y)) <= maxLateral &&
            !AutonomousZonePockets.Contains(zone.ID, p.X, p.Y, p.Z) &&
            AutonomousRendezvousNavigation.HasLocalExit(nav, zone, p);
        if (!onMesh)
        {
            Vector3? nearest = nav.GetClosestPoint(zone, position, maxLateral, maxLateral, 256, nav.DefaultFilters);
            if (nearest.HasValue && Usable(nearest.Value))
            {
                floor = nearest.Value;
                return true;
            }
        }
        // The closest floor is the scrap itself (or another dead end): look outward in rings for floor that leads on.
        var tried = new List<Vector3>();
        for (float radius = 50; radius <= maxLateral; radius += 50)
            for (int k = 0; k < 8; k++)
            {
                double angle = k * Math.PI / 4;
                var probe = new Vector3(position.X + (float)(Math.Cos(angle) * radius), position.Y + (float)(Math.Sin(angle) * radius), position.Z);
                Vector3? ground = nav.GetClosestPoint(zone, probe, 24, 24, 256, nav.DefaultFilters);
                if (!ground.HasValue || tried.Any(t => Vector3.DistanceSquared(t, ground.Value) < 16 * 16))
                    continue;
                tried.Add(ground.Value);
                if (Usable(ground.Value))
                {
                    floor = ground.Value;
                    return true;
                }
            }
        return false;
    }

    // A swimming actor's height is the water surface; the lake or river bed under it can lie well
    // below the repair limit (Valley of Bri Leith rally, 2026-10-12: 38 units).
    private const float MaximumPlanningFloorDepth = 160f;

    /// <summary>
    /// The floor to plan a route from: <see cref="TryFloor"/>, or else the floor straight below
    /// (same XY, at most MaximumPlanningFloorDepth down), for an actor swimming above it. For route
    /// planning only; the actor is never moved there. Every leg from it is still proven.
    /// </summary>
    public static bool TryPlanningFloor(IPathfindingMgr nav, Zone zone, Vector3 position, out Vector3 floor)
    {
        if (TryFloor(nav, zone, position, out floor)) return true;
        if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
        Vector3? below = nav.GetClosestPoint(zone, position with { Z = position.Z - MaximumPlanningFloorDepth / 2 }, 2, 2,
            MaximumPlanningFloorDepth / 2 + 8, nav.DefaultFilters);
        if (!below.HasValue || !float.IsFinite(below.Value.Z) ||
            Vector2.DistanceSquared(new(position.X, position.Y), new(below.Value.X, below.Value.Y)) > 4 ||
            below.Value.Z > position.Z || position.Z - below.Value.Z > MaximumPlanningFloorDepth)
            return false;
        floor = below.Value;
        return true;
    }

    public static bool IsLocalFloor(Vector3 position, Vector3 floor) =>
        float.IsFinite(floor.X) && float.IsFinite(floor.Y) && float.IsFinite(floor.Z) &&
        Vector2.DistanceSquared(new(position.X, position.Y), new(floor.X, floor.Y)) <= 4 &&
        floor.Z >= position.Z - MaximumDownwardFloorCorrection && floor.Z - position.Z <= 128;
}

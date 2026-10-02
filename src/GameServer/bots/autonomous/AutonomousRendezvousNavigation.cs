using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DOL.GS;

// Called only when choosing an assembly point, never in a bot's movement tick.
// A valid nav polygon may still be an isolated prop top, not a usable town floor.
public static class AutonomousRendezvousNavigation
{
    // Open lower-city ground: complete corridors to the bank/exit and unobstructed
    // formation rings verified on zone120. Do not randomize onto nearby steps.
    public static Vector3 JordheimMeetingPoint => new(34100, 34600, 8008);

    public static bool TryChooseFixedPoint(IPathfindingMgr nav, Zone zone, Vector3 anchor, out Vector3 point)
    {
        point = default;
        if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
        Vector3? floor = nav.GetClosestPoint(zone, anchor, 48, 48, 96, nav.DefaultFilters);
        if (!floor.HasValue || !HasLocalExit(nav, zone, floor.Value)) return false;
        point = floor.Value;
        return true;
    }

    public static bool CanReachFrom(IPathfindingMgr nav, Zone zone, Vector3 start, Vector3 rendezvous)
    {
        AutonomousNavigationSurface.TryFloor(nav, zone, start, out start);
        return AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, start, rendezvous);
    }

    /// <summary>
    /// Resolves one deterministic formation offset without allowing it to land
    /// on a nearby prop, shelf, or disconnected polygon. This is called while a
    /// party is formed (and when its membership changes), never every movement
    /// tick.
    /// </summary>
    public static bool TryResolveFormationSlot(
        IPathfindingMgr nav,
        Zone zone,
        Vector3 center,
        Vector3 desired,
        out Vector3 slot)
    {
        slot = default;
        if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
            return false;

        Vector3? moved = AutonomousNavigationSurface.MoveAlongGround(nav, zone, center, desired);
        if (!moved.HasValue || Vector2.Distance(new(moved.Value.X, moved.Value.Y), new(desired.X, desired.Y)) > 48 ||
            MathF.Abs(moved.Value.Z - center.Z) > 160 || !ConnectedBothWays(nav, zone, center, moved.Value))
            return false;

        slot = moved.Value;
        return true;
    }

    public static bool CanReachFormationSlot(
        IPathfindingMgr nav,
        Region region,
        Zone memberZone,
        Zone slotZone,
        Vector3 memberPosition,
        Vector3 slot)
    {
        if (region == null || memberZone == null || slotZone == null)
            return false;
        if (memberZone == slotZone)
            return CanReachFrom(nav, slotZone, memberPosition, slot);
        return AutonomousZoneItinerary.TryNextStep(region, memberZone, slotZone,
            memberPosition, slot, nav, out _);
    }

    private static readonly Vector2[] ExitOffsets = [new(512, 0), new(0, 512), new(-512, 0), new(0, -512)];

    public static bool TryChoosePoint(IPathfindingMgr nav, Zone zone, Vector3 anchor, out Vector3 point)
    {
        point = default;
        if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
        Vector3? floor = nav.GetClosestPoint(zone, anchor, 48, 48, 96, nav.DefaultFilters);
        if (!floor.HasValue || !HasLocalExit(nav, zone, floor.Value)) return false;
        var choices = new List<Vector3> { floor.Value };
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Vector3? candidate = nav.GetRandomPoint(zone, floor.Value, 240, nav.DefaultFilters);
            if (candidate.HasValue && Vector3.DistanceSquared(candidate.Value, floor.Value) <= 300 * 300 &&
                ConnectedBothWays(nav, zone, floor.Value, candidate.Value) && HasLocalExit(nav, zone, candidate.Value))
                choices.Add(candidate.Value);
        }
        // A connected lower town floor is preferable to a reachable wall,
        // shelf, or raised service platform.  Keep the projected anchor when
        // candidates are on the same elevation so ordinary street rendezvous
        // remain stable and do not drift between sessions.
        point = choices.OrderBy(candidate => candidate.Z)
            .ThenBy(candidate => Vector3.DistanceSquared(candidate, floor.Value))
            .First();
        return true;
    }

    public static bool HasLocalExit(IPathfindingMgr nav, Zone zone, Vector3 center)
    {
        foreach (Vector2 offset in ExitOffsets)
        {
            Vector3 target = center + new Vector3(offset, 0);
            if (target.X < zone.XOffset || target.X >= zone.XOffset + zone.Width ||
                target.Y < zone.YOffset || target.Y >= zone.YOffset + zone.Height) continue;
            Vector3? exit = nav.GetClosestPoint(zone, target, 64, 64, 256, nav.DefaultFilters);
            if (exit.HasValue && ConnectedBothWays(nav, zone, center, exit.Value)) return true;
        }
        return false;
    }

    private static bool ConnectedBothWays(IPathfindingMgr nav, Zone zone, Vector3 a, Vector3 b) =>
        AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, a, b) &&
        AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, b, a);
}

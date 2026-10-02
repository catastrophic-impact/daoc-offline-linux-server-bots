using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DOL.GS;

/// <summary>
/// Resolves a walkable point close enough to activate an authoritative region
/// crossing. Kept independent from a bot/controller so the installed capital
/// meshes can exercise the exact production resolver.
/// </summary>
public static class AutonomousZonePointApproach
{
    public static bool TryResolve(IPathfindingMgr nav, Zone zone, Vector3 actor,
        Vector3 portal, int arrivalRadius, out Vector3 approach, Func<Vector3, bool> candidateFilter = null)
    {
        approach = portal;
        if (nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
            return true;
        if (!AutonomousNavigationSurface.TryFloor(nav, zone, actor, out Vector3 start))
            return false;

        var candidates = new List<Vector3>();
        void AddCandidate(Vector3 raw, int xy = 48, int z = 192)
        {
            Vector3? floor = nav.GetClosestPoint(zone, raw, xy, xy, z, nav.DefaultFilters);
            if (!floor.HasValue || Vector2.Distance(new(floor.Value.X, floor.Value.Y),
                    new(portal.X, portal.Y)) > arrivalRadius ||
                MathF.Abs(floor.Value.Z - portal.Z) > 256)
                return;
            if (!candidates.Any(existing => Vector3.DistanceSquared(existing, floor.Value) < 24 * 24))
                candidates.Add(floor.Value);
        }

        AddCandidate(portal);
        if (Vector2.Distance(new(start.X, start.Y), new(portal.X, portal.Y)) <= arrivalRadius &&
            MathF.Abs(start.Z - portal.Z) <= 256 &&
            nav.HasLineOfSight(zone, start, portal, nav.DefaultFilters))
            candidates.Add(start);

        int[] radii =
        [
            Math.Max(32, arrivalRadius / 3),
            Math.Max(48, arrivalRadius * 2 / 3),
            Math.Max(64, arrivalRadius - 20)
        ];
        foreach (int radius in radii.Distinct())
        for (int angle = 0; angle < 360; angle += 30)
        {
            float radians = angle * MathF.PI / 180f;
            AddCandidate(new(portal.X + MathF.Cos(radians) * radius,
                portal.Y + MathF.Sin(radians) * radius, portal.Z), 36, 128);
        }

        foreach (Vector3 candidate in candidates.OrderBy(point => Vector3.DistanceSquared(start, point)))
        {
            if (candidateFilter != null && !candidateFilter(candidate))
                continue;
            if (!AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, start, candidate))
                continue;
            approach = candidate;
            return true;
        }

        return false;
    }
}

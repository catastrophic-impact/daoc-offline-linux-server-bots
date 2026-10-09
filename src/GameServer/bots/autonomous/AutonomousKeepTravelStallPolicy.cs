using System;
using System.Numerics;

namespace DOL.GS;

/// <summary>
/// Keeps RvR keep travel moving. In the 2026-10-02 evening run, 77 Hibernian and
/// many Midgard siege bots stood still for 15 minutes on a single 50,000-unit
/// "segment 1/1" leg from their portal keep toward Caer Benowyc, so no siege engine
/// was ever placed. Long legs are walked in navmesh-proven chunks, and a bot that
/// makes no progress replans from where it stands instead of idling.
/// </summary>
public static class KeepTravelStallPolicy
{
    public const float LongLeg = 6_000;
    public const float LegLength = 4_000;
    public const float StallRadius = 128;
    public const float SameSpotRadius = 384;
    public const long StallMilliseconds = 60_000;
    public const int StallsBeforeBackoff = 3;
    public const long BackoffMilliseconds = 120_000;

    public static bool NeedsShorterLeg(Vector3 from, Vector3 to) =>
        Vector2.Distance(new(from.X, from.Y), new(to.X, to.Y)) > LongLeg;

    /// <summary>Fighting, casting or riding a stable horse is not a travel stall.</summary>
    public static bool IsStalled(long stillMilliseconds, bool busy, bool riding) =>
        !busy && !riding && stillMilliseconds >= StallMilliseconds;

    public static bool SameSpot(Vector3 a, Vector3 b) =>
        Vector2.DistanceSquared(new(a.X, a.Y), new(b.X, b.Y)) <= SameSpotRadius * SameSpotRadius;

    /// <summary>The point <paramref name="length"/> units along a path, or its end when shorter.</summary>
    public static Vector3? PointAlong(ReadOnlySpan<Vector3> corners, float length)
    {
        if (corners.Length == 0) return null;
        float remaining = length;
        for (int i = 1; i < corners.Length; i++)
        {
            float segment = Vector3.Distance(corners[i - 1], corners[i]);
            if (segment >= remaining)
                return segment <= 0 ? corners[i] : Vector3.Lerp(corners[i - 1], corners[i], remaining / segment);
            remaining -= segment;
        }
        return corners[^1];
    }

    /// <summary>A walkable point about <see cref="LegLength"/> along the real navmesh path.</summary>
    public static bool TryShorterLeg(IPathfindingMgr nav, Zone zone, Vector3 from, Vector3 to, out Vector3 hop)
    {
        hop = to;
        if (nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
        Vector3? start = nav.GetClosestPoint(zone, from, 64, 64, 256, nav.DefaultFilters);
        Vector3? end = nav.GetClosestPoint(zone, to, 64, 64, 256, nav.DefaultFilters);
        if (!start.HasValue || !end.HasValue) return false;
        var nodes = new WrappedPathfindingNode[256];
        PathfindingResult result = nav.GetPathStraight(zone, start.Value, end.Value, nav.DefaultFilters, nodes);
        if (result.NodeCount < 1 || result.Status is not (PathfindingStatus.PathFound or
                PathfindingStatus.PartialPathFound or PathfindingStatus.BufferTooSmall))
            return false;
        var corners = new Vector3[result.NodeCount + 1];
        corners[0] = start.Value;
        for (int i = 0; i < result.NodeCount; i++) corners[i + 1] = nodes[i].Position;
        Vector3? along = PointAlong(corners, LegLength);
        if (!along.HasValue) return false;
        Vector3? floor = nav.GetClosestPoint(zone, along.Value, 48, 48, 256, nav.DefaultFilters);
        if (!floor.HasValue || Vector2.DistanceSquared(new(from.X, from.Y), new(floor.Value.X, floor.Value.Y)) < 200 * 200)
            return false;
        hop = floor.Value;
        return true;
    }
}

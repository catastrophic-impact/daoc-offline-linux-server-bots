using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DOL.GS;

/// <summary>
/// Selects one certified DF waypoint at a time. The caller supplies a native
/// complete/strict-leg check; geometric proximity alone cannot cross a wall,
/// another floor, or a one-way entrance drop in reverse.
/// </summary>
public static class AutonomousDarknessFallsRoutePlan
{
    private const string CampPrefix = "df-live:";

    public static bool TryGetCampMobId(string campId, out string mobId)
    {
        mobId = null;
        if (string.IsNullOrWhiteSpace(campId) ||
            !campId.StartsWith(CampPrefix, StringComparison.Ordinal)) return false;
        int start = CampPrefix.Length;
        int separator = campId.IndexOf(':', start);
        if (separator <= start) return false;
        string candidate = campId[start..separator];
        if (!Guid.TryParse(candidate, out _) && !long.TryParse(candidate, out _)) return false;
        mobId = candidate;
        return true;
    }

    public static bool TryGetWaypoints(float[][] data, out Vector3[] waypoints)
    {
        waypoints = [];
        if (data is not { Length: >= 2 and <= 256 } ||
            data.Any(point => point?.Length != 3 || point.Any(value => !float.IsFinite(value))))
            return false;
        waypoints = data.Select(point => new Vector3(point[0], point[1], point[2])).ToArray();
        return true;
    }

    /// <summary>Find a nearby point on the ordered chain after interruption,
    /// then continue at its next point. A 3D near match is only trusted inside
    /// a tight 16-unit arrival ball; otherwise a complete strict native leg is
    /// mandatory. No uncertain route is returned.</summary>
    public static bool TryRejoin(Vector3 current, IReadOnlyList<Vector3> chain,
        Func<Vector3, Vector3, bool> completeStrictLeg, out int nextIndex)
    {
        nextIndex = -1;
        if (chain is not { Count: >= 2 } || completeStrictLeg == null ||
            !IsFinite(current) || chain.Any(point => !IsFinite(point))) return false;

        foreach (int index in Enumerable.Range(0, chain.Count)
                     .OrderBy(index => Vector3.DistanceSquared(current, chain[index])))
        {
            if (Vector3.DistanceSquared(current, chain[index]) <= 16 * 16)
            {
                nextIndex = index + 1;
                return true;
            }
            if (completeStrictLeg(current, chain[index]))
            {
                nextIndex = index;
                return true;
            }
        }
        return false;
    }

    public static bool IsAtWaypoint(Vector3 current, Vector3 waypoint) =>
        IsFinite(current) && IsFinite(waypoint) &&
        Vector3.DistanceSquared(current, waypoint) <= 24 * 24;

    /// <summary>A failed exit search may be expensive on a detached floor.
    /// Retry after a short pause, or immediately if the destination changes
    /// or the bot has physically moved to a different approach.</summary>
    public static bool ShouldProbeExit(string priorKey, string key,
        Vector3 priorPosition, Vector3 current, long now, long nextProbeTick) =>
        !string.Equals(priorKey, key, StringComparison.Ordinal) ||
        now >= nextProbeTick ||
        IsFinite(current) && IsFinite(priorPosition) &&
        Vector3.DistanceSquared(current, priorPosition) >= 96 * 96;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DOL.GS;

/// <summary>
/// Classic-frontier warbands assemble inside their own safe border keep before
/// entering the frontier. These are fixed realm contracts, not random PvE town
/// choices. The coordinator still projects and validates the point against the
/// installed navmesh before using it.
/// </summary>
public static class AutonomousRvrStaging
{
    public readonly record struct BorderKeep(ushort RegionId, Vector3 Position, string Name);

    /// <summary>
    /// Where a realm's siege army musters for a target in another region: its own guarded portal
    /// outpost inside that frontier (the frontier teleporter's arrival point), else its border keep.
    /// Run 11: Midgard mustered at Svasud Faste, crossed, and walked 105k-145k units from the Hadrian's
    /// Wall arrival to Castle Myrddin; most were still on the road at the rally deadline.
    /// </summary>
    public static bool TryGetSiegeMuster(eRealm realm, ushort targetRegion, out BorderKeep muster)
    {
        var passage = AutonomousFrontierTransport.Destination(realm, targetRegion);
        if (passage != null && passage.Medallion != "home_necklace")
        {
            Zone zone = WorldMgr.GetRegion(passage.Region)?.GetZone(passage.Location.X, passage.Location.Y);
            muster = new(passage.Region, new(passage.Location.X, passage.Location.Y, passage.Location.Z),
                $"the portal outpost in {zone?.Description ?? "the frontier"}");
            return true;
        }
        return TryGetBorderKeep(realm, out muster);
    }

    // Each realm's two classic border keeps (safe-area centres from the Area table). Owner 2026-10-07: bots only
    // ever entered the frontier through the first one; the second was never offered.
    private static readonly BorderKeep[] AlbionKeeps =
        [new(1, new(585085, 477504, 2600), "Castle Sauvage"), new(1, new(528451, 358290, 8320), "Snowdonia Fortress")];
    private static readonly BorderKeep[] MidgardKeeps =
        [new(100, new(766235, 669173, 5736), "Svasud Faste"), new(100, new(704022, 738009, 5704), "Vindsaul Faste")];
    private static readonly BorderKeep[] HiberniaKeeps =
        [new(200, new(333229, 419539, 5336), "Druim Ligen"), new(200, new(421166, 484998, 1976), "Druim Cain")];

    public static IReadOnlyList<BorderKeep> BorderKeeps(eRealm realm) => realm switch
    {
        eRealm.Albion => AlbionKeeps,
        eRealm.Midgard => MidgardKeeps,
        eRealm.Hibernia => HiberniaKeeps,
        _ => [],
    };

    /// <summary>The realm's first border keep (a fixed reference; staging picks the nearer of the two).</summary>
    public static bool TryGetBorderKeep(eRealm realm, out BorderKeep keep)
    {
        keep = BorderKeeps(realm).FirstOrDefault();
        return keep.RegionId != 0;
    }

    /// <summary>The border keep this bot reaches sooner (travel estimate, teleporters and region crossings included).</summary>
    public static bool TryGetNearestBorderKeep(GameBot bot, out BorderKeep keep,
        Dictionary<(eRealm Realm, ushort Region), DOL.Database.DbZonePoint> crossings = null)
    {
        crossings ??= new();
        keep = BorderKeeps(bot.Realm).OrderBy(k => AutonomousWorldBotController.EstimateTravelMinutes(bot, k.RegionId,
            (int)k.Position.X, (int)k.Position.Y, crossings)).FirstOrDefault();
        return keep.RegionId != 0;
    }

    /// <summary>The border keep nearest a point in its region (shared by a whole army), else the first one.</summary>
    public static bool TryGetBorderKeepNear(eRealm realm, ushort regionId, Vector2 point, out BorderKeep keep)
    {
        var keeps = BorderKeeps(realm);
        keep = keeps.Where(k => k.RegionId == regionId).OrderBy(k => Vector2.DistanceSquared(new(k.Position.X, k.Position.Y), point))
            .FirstOrDefault();
        if (keep.RegionId == 0) keep = keeps.FirstOrDefault();
        return keep.RegionId != 0;
    }

    /// <summary>
    /// Imported area centers can sit inside keep geometry rather than on a
    /// walkable courtyard polygon. Probe a deterministic set wholly inside the
    /// 3,500-unit safe-area radius; the coordinator still requires a connected
    /// floor and valid formation slots before accepting one.
    /// </summary>
    public static IEnumerable<Vector3> CandidateAnchors(BorderKeep keep, long formationKey = 0)
    {
        int offset = (int)(unchecked((ulong)formationKey) % 12);
        for (int ring = 0; ring < 4; ring++)
        for (int step = 0; step < 12; step++)
        {
            int radius = 600 * (1 + (ring + (int)(unchecked((ulong)formationKey) / 12 % 4)) % 4);
            double angle = Math.PI * 2d * ((step + offset) % 12) / 12d;
            yield return keep.Position + new Vector3(
                (float)(Math.Cos(angle) * radius),
                (float)(Math.Sin(angle) * radius), 0);
        }
        yield return keep.Position;
    }

    /// <summary>
    /// RvR leader candidates, closest to their realm's border keep first. A
    /// randomly drawn leader was often a roamer deep in an enemy frontier and
    /// missed the 20-minute staging window (244 of 2,099 warbands in one run).
    /// Realms take turns so one realm's nearby bots never crowd out another's
    /// formation; equal estimates keep the incoming (shuffled) order.
    /// </summary>
    public static List<T> ClosestToStagingFirst<T>(IEnumerable<T> candidates, Func<T, eRealm> realm,
        Func<T, double> minutesToStaging)
    {
        List<T>[] queues = candidates
            .Select((candidate, index) => (candidate, index, minutes: minutesToStaging(candidate)))
            .GroupBy(entry => realm(entry.candidate))
            .OrderBy(group => group.Key)
            .Select(group => group.OrderBy(entry => entry.minutes).ThenBy(entry => entry.index)
                .Select(entry => entry.candidate).ToList())
            .ToArray();
        var ordered = new List<T>();
        for (int rank = 0; queues.Any(queue => rank < queue.Count); rank++)
            foreach (List<T> queue in queues)
                if (rank < queue.Count)
                    ordered.Add(queue[rank]);
        return ordered;
    }

    /// <summary>Every formed warband member may acquire a local RvR target;
    /// PvE parties retain their single tank-or-leader puller.</summary>
    public static bool UsesIndependentCombatActors(eAutonomousObjectiveKind objectiveKind) =>
        objectiveKind == eAutonomousObjectiveKind.RvR;

    public static int RollWarbandSize(int maximumSize, double roll)
    {
        int maximum = Math.Clamp(maximumSize, 1, 8);
        double bounded = Math.Clamp(roll, 0d, Math.BitDecrement(1d));
        return 1 + (int)(bounded * maximum);
    }

    /// <summary>
    /// Spreads a warband deterministically across the closest visible enemies.
    /// Limiting the window to party size keeps the force converged instead of
    /// sending one member after a distant target.
    /// </summary>
    public static int TargetIndex(long actorKey, int candidateCount, int warbandSize)
    {
        if (candidateCount <= 1)
            return 0;
        int window = Math.Min(candidateCount, Math.Max(2, warbandSize));
        ulong positive = unchecked((ulong)actorKey);
        return (int)(positive % (uint)window);
    }
}

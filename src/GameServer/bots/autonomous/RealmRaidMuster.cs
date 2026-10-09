using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DOL.GS;

/// <summary>Service-hub assembly for expeditions only; never ordinary PvE matchmaking.</summary>
public static class RealmRaidMuster
{
    // Retain the validated seam sequence for this expedition. Replanning each
    // leg independently can bounce between two zones after rejecting a blocked
    // direct seam (Vigilant Rock/Caillte Garran on the Galladoria approach).
    /// <summary>Why the last failed <see cref="TryRoute"/> on this thread failed (for the start log).</summary>
    [ThreadStatic] public static string LastRouteFailure;

    public static bool TryRoute(Region region, IPathfindingMgr nav, eRealm realm, Vector3 start, Vector3 goal, out Vector3[] seams, Vector3? via = null)
    {
        seams = [];
        Zone from = region?.GetZone((int)start.X,(int)start.Y);
        Zone target = region?.GetZone((int)goal.X,(int)goal.Y);
        LastRouteFailure = from == null || target == null ? "start or goal outside every zone" : null;
        if (from == null || target == null) return false;
        var visited = new HashSet<Zone>();
        var steps = new List<Vector3>();
        Zone viaZone = via.HasValue ? region.GetZone((int)via.Value.X,(int)via.Value.Y) : null;
        while (from != target && visited.Count < 16)
        {
            visited.Add(from);
            if (from == viaZone) viaZone = null;
            Zone nextTarget=viaZone ?? target;
            Vector3 guide=viaZone != null ? via.Value : goal;
            if (!AutonomousZoneItinerary.TryNextStep(region,from,nextTarget,start,guide,nav,out var step,
                z => !visited.Contains(z) && AutonomousRealmBoundary.Allows(realm,region.ID,z.ID)))
            { LastRouteFailure = $"no seam from zone {from.ID} toward zone {nextTarget.ID} at {(int)start.X},{(int)start.Y},{(int)start.Z}"; return false; }
            steps.Add(step.Outside);
            start=step.Outside;
            from=region.GetZone((int)start.X,(int)start.Y);
            if (from == null || visited.Contains(from)) return false;
        }
        if (from != target || !AutonomousZoneItinerary.HasCompleteCorridor(nav,from,start,goal))
        { LastRouteFailure = from != target ? $"stopped in zone {from.ID} after {visited.Count} zones" : $"no corridor inside zone {from.ID} to the staging post"; return false; }
        seams=steps.ToArray();
        return true;
    }
    public sealed record Hub(string Event, string Name, ushort Region, ushort Zone, int OffsetX, int OffsetY, Vector3 Center, Vector3? Via = null);
    // Anchors are existing service settlements in this server's world data.
    // Z comes from service NPCs, not the occasionally stale Area records.
    public static readonly Hub[] Hubs =
    [
        new("dragon-albion", "Yarley's farm", 1, 6, 43, 77, new(370140,680047,5531)),
        new("dragon-midgard", "West Skona", 100, 106, 84,110, new(711984,924337,5062)),
        new("dragon-hibernia", "Innis Carthaig", 200,204,35,83,new(334820,719979,4296),new(361354,750434,4944)),
        new("epic-albion", "Fort Gwyntell",51,53,46,46,new(426905,416817,5712)),
        new("epic-midgard", "Hagall",151,154,44,40,new(379260,385996,7752)),
        new("epic-hibernia", "Dalniver's service settlement (World's End)",181,185,42,28,new(368881,263409,3472)),
        // Neutral expeditions muster at a service settlement of their own realm near that
        // realm's entrance (Darkness Falls) or the dungeon leading to Summoner's Hall.
        new("summoners-albion", "Snowdonia Station", 1, 3, 59, 45, new(523562,408847,4204)),
        new("summoners-midgard", "Svasud Faste", 100, 111, 88, 74, new(765706,668538,5736)),
        new("summoners-hibernia", "Druim Cain", 200, 206, 47, 59, new(421782,485582,1824)),
        new("summoners-hibernia", "Druim Ligen", 200, 207, 35, 51, new(334804,419842,5187)),
        new("darkness-albion", "Camelot Hills camp by the Darkness Falls entrance", 1, 0, 67, 59, new(588797,515656,2885)),
        new("darkness-midgard", "Svasud Faste", 100, 111, 88, 74, new(765706,668538,5736)),
        new("darkness-hibernia", "Basar", 200, 207, 35, 51, new(338400,444017,5077))
    ];

    public const int MusterPostAttempts = 160;
    public const float MusterPostSpacing = 340;

    public static bool TryPost(IPathfindingMgr nav, Zone zone, Hub hub, int slot,
        IReadOnlyCollection<Vector3> occupied, out Vector3 post, Func<Vector3, bool> safe = null)
    {
        post = default;
        if (nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone) || slot < 0 || slot >= RealmRaidRecruitmentPolicy.MaximumParties) return false;
        // 38 parties need 38 posts. Border-keep hubs among now-walled mountains (Svasud Faste,
        // Druim Cain) fit only ~20 within 2,250 units, so musters stalled below 200 (2026-10-06).
        for (int attempt = 0; attempt < MusterPostAttempts; attempt++)
        {
            double angle = (slot * 137.5 + attempt * 47.5) * Math.PI / 180;
            float radius = 450 + attempt % 16 * 220;
            var raw = hub.Center + new Vector3((float)Math.Cos(angle)*radius, (float)Math.Sin(angle)*radius, 0);
            var floor = nav.GetClosestPoint(zone, raw, 64, 64, 512, nav.DefaultFilters);
            if (!floor.HasValue || Math.Abs(floor.Value.Z-hub.Center.Z) > 512 ||
                occupied.Any(p => Vector3.DistanceSquared(p,floor.Value) < MusterPostSpacing*MusterPostSpacing) ||
                safe?.Invoke(floor.Value) == false ||
                !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value) ||
                !AutonomousZonePointApproach.TryResolve(nav, zone, floor.Value, hub.Center, 160, out _) ||
                !RealmRaidStaging.HasOpenPartySpace(nav, zone, floor.Value)) continue;
            post = floor.Value;
            return true;
        }
        return false;
    }
}

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
    public static bool TryRoute(Region region, IPathfindingMgr nav, eRealm realm, Vector3 start, Vector3 goal, out Vector3[] seams, Vector3? via = null)
    {
        seams = [];
        Zone from = region?.GetZone((int)start.X,(int)start.Y);
        Zone target = region?.GetZone((int)goal.X,(int)goal.Y);
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
                z => !visited.Contains(z) && AutonomousRealmBoundary.Allows(realm,region.ID,z.ID))) return false;
            steps.Add(step.Outside);
            start=step.Outside;
            from=region.GetZone((int)start.X,(int)start.Y);
            if (from == null || visited.Contains(from)) return false;
        }
        if (from != target || !AutonomousZoneItinerary.HasCompleteCorridor(nav,from,start,goal)) return false;
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
        new("epic-hibernia", "Dalniver's service settlement (World's End)",181,185,42,28,new(368881,263409,3472))
    ];

    public static bool TryPost(IPathfindingMgr nav, Zone zone, Hub hub, int slot,
        IReadOnlyCollection<Vector3> occupied, out Vector3 post, Func<Vector3, bool> safe = null)
    {
        post = default;
        if (nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone) || slot < 0 || slot >= RealmRaidRecruitmentPolicy.MaximumParties) return false;
        for (int attempt = 0; attempt < 96; attempt++)
        {
            double angle = (slot * 137.5 + attempt * 47.5) * Math.PI / 180;
            float radius = 450 + attempt % 10 * 200;
            var raw = hub.Center + new Vector3((float)Math.Cos(angle)*radius, (float)Math.Sin(angle)*radius, 0);
            var floor = nav.GetClosestPoint(zone, raw, 64, 64, 512, nav.DefaultFilters);
            if (!floor.HasValue || Math.Abs(floor.Value.Z-hub.Center.Z) > 384 ||
                occupied.Any(p => Vector3.DistanceSquared(p,floor.Value) < 420*420) ||
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

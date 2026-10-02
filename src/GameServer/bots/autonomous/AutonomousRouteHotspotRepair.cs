using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.Database;

namespace DOL.GS;

/// <summary>
/// Bounded repairs for audited live-world coordinates whose saved/runtime Z is
/// offset from the connected Detour floor. These signatures are deliberately local:
/// they must never turn the general nearest-poly query into a bridge, shelf, or
/// multi-floor teleport.
/// </summary>
public static class AutonomousRouteHotspotRepair
{
    private readonly record struct CrossingSourceHotspot(
        int EdgeId,
        ushort RegionId,
        Vector3 Center,
        float SignatureRadius,
        int SearchHorizontal,
        int SearchVertical,
        float MaximumHorizontalCorrection,
        float MaximumVerticalCorrection);

    private readonly record struct FloorDriftHotspot(
        ushort RegionId,
        Vector2 Center,
        float Radius,
        float MaximumVerticalCorrection,
        float? SourceZ = null);

    private readonly record struct TerminalPocket(
        ushort RegionId,
        Vector2 Center,
        float Radius,
        Vector3 Escape,
        Vector3 NetworkWitness,
        float? SourceZ = null);

    private readonly record struct AuditedCampPocket(
        ushort RegionId,
        string MonsterName,
        Vector2 Center,
        float Radius,
        Vector3 Escape,
        Vector3 NetworkWitness);

    private static readonly FloorDriftHotspot[] FloorDriftHotspots =
    [
        // Shannon Estuary: the route toward the Tir na Nog/Connacht side and
        // Pheuloc approach can retain hillside Z while its XY is already over
        // the connected valley-floor polygon.
        new(200, new Vector2(318807, 629660), 1_200, 384),
        new(200, new Vector2(316050, 622600), 2_200, 2_048),

        // Salisbury Plains: this exact road pocket was saved 271 units above
        // its connected floor, making every otherwise-valid camp path fail.
        new(1, new Vector2(556103, 560268), 1_200, 512),

        // The Mularn stable route can persist its authoritative arrival 168
        // units below the actual connected town floor. This is the inverse of
        // the older hillside drift, so use a bounded absolute correction.
        new(100, new Vector2(803743, 722129), 600, 256),
        // Sept 14 captures: unchanged XY is on the entrance-connected floor,
        // but the interpolated actor height is 108 units above it.
        new(191, new Vector2(32195, 30562), 200, 160, 16127),
        // Fort Gwyntell muster: member positions retain the center's height
        // across sloping ground. Only this measured height band is eligible.
        new(51, new Vector2(426905, 416817), 2400, 128, 5757),
    ];

    // Live records can retain a position just outside (or above) the installed
    // polygon even though the real SI exit and its road corridor are healthy.
    // Match the exact observed source pocket and authoritative edge; no other
    // portal, bot, region, or general nearest-poly query inherits this repair.
    private static readonly CrossingSourceHotspot[] CrossingSourceHotspots =
    [
        // September 13 expedition captures: native probes verify these small
        // same-floor corrections and the unchanged SI entrance corridor.
        new(165, 100, new Vector3(768012, 737271, 5558), 120, 16, 96, 16, 96),
        new(167, 200, new Vector3(330501, 483419, 7480), 120, 16, 96, 16, 96),
        new(154, 51, new Vector3(532550, 544857, 3702), 320, 320, 128, 320, 128),
        new(154, 51, new Vector3(530813, 543371, 3702), 160, 64, 128, 64, 128),
        // Mularn's stable route stores its arrival 168 units below the real
        // town floor.  Correct only travelers using the authentic SI edge 165;
        // the corrected point has a proven corridor to the unchanged portal.
        new(165, 100, new Vector3(803743, 722129, 4684), 420, 8, 256, 8, 192),
        new(166, 151, new Vector3(304535, 370789, 3241), 160, 8, 128, 8, 128),
    ];

    private static readonly Vector2[] JordheimServicePockets =
    [
        new(31189, 27479), // Tora
        new(32499, 28664), // Cruella de Vil
        new(32250, 28294), // Brynhild: separate raised realm-exchange service pocket
    ];
    private static readonly Vector3 CotswoldEastGateRoad = new(553118, 513222, 2896);
    // The portal landing and its former 313848,474886 escape belong to the
    // repeatedly failing transfer loop seen in live logs. Carsten's nearby
    // road is on the main Connacht component; the witness point proves that
    // component continues into the ordinary hunting network.
    private static readonly Vector3 ConnachtMainRoad = new(311960, 470002, 5200);
    private static readonly Vector3 ConnachtMainNetworkWitness = new(330135, 464853, 5571);

    // Repeated live failures from the same four Hibernian source components.
    // These are intentionally point signatures, not zone-wide shortcuts. Every
    // escape is projected and must prove a real corridor into the ordinary
    // network before it can be used after normal recovery has failed.
    private static readonly TerminalPocket[] TerminalPockets =
    [
        // Sept 13: this tiny Emain surface terminates below the connected
        // hillside. Only the measured 64-unit pocket uses this recovery;
        // the adjacent floor must still prove its onward corridor at runtime.
        new(200, new(474010, 318918), 64, new(474044, 318952, 5838), new(476053, 343166, 4105), 5786),
        new(200, new(298241, 636847), 700, new(296110, 642245, 4853), new(318807, 629660, 4959)),
        new(200, new(311024, 638475), 700, new(296110, 642245, 4853), new(318807, 629660, 4959)),
        new(200, new(340988, 469188), 700, ConnachtMainNetworkWitness, ConnachtMainRoad),
        new(181, new(423162, 443589), 700, new(423900, 440147, 5998), new(424924, 445575, 5977)),
        new(200, new(312250, 472500), 4_000, ConnachtMainRoad, ConnachtMainNetworkWitness),

        // Four measured Lough Derg terrain pockets are locally present but do
        // not connect to the ordinary travel/formation network. Each escape was
        // independently projected and proven to the logged onward corridor.
        new(200, new(335101, 496030), 420, new(335226, 496030, 5201), new(342015, 498967, 4980)),
        new(200, new(338246, 498319), 420, new(338371, 498319, 4756), new(342015, 498967, 4980)),
        new(200, new(335558, 493005), 420, new(335683, 493005, 5203), new(342015, 498967, 4980)),
        new(200, new(335542, 499585), 520, new(335155, 499434, 4525), new(342015, 498967, 4980)),
        // Orchard travelers repeatedly stop on this exact collision strip.
        // The adjacent point is on the same floor and proves a corridor to the
        // ordinary Lough Derg hunting network.
        new(200, new(337920, 496976), 220, new(338045, 496976, 5136), new(342015, 498967, 4980)),

        // Domnann's catty sylvanshade approach stops on a tiny strip 134 units
        // from the connected floor. Preserve the target and move only actors
        // that exhausted normal local recovery in this exact strip.
        new(181, new(404222, 443278), 360, new(404347, 443278, 4436), new(404356, 447740, 4434)),

        // Tomb of Mithra's common entry-room endpoint is a collision corner
        // shared by decaying-spirit and undead-guardsman routes.
        new(21, new(32585, 32067), 260, new(32500, 32000, 16006), new(33150, 32732, 16480)),

        // One Muire traveler exhausted all three physical collision side
        // steps on the entry-room corner even though the authored interior
        // mesh is connected.  Keep this to that corner and move it only to
        // the adjacent, verified entry-room floor after normal recovery fails.
        new(221, new(32960, 31892), 420, new(33207, 31902, 16003), new(31120, 29939, 16239)),
        new(221, new(31900, 32669), 360, new(32036, 32669, 16003), new(31120, 29939, 16239)),
        new(221, new(30567, 34164), 360, new(30681, 34192, 15763), new(31120, 29939, 16239)),
        new(221, new(29811, 32799), 520, new(29437, 32846, 15520), new(31120, 29939, 16239)),
    ];

    // These five outdoor camps were audited from live recovery records. Their
    // tiny collision pockets are not general zone shortcuts: a bot must be on
    // the matching objective, in the matching region and inside the measured
    // footprint, and ordinary local recovery must already have failed. The
    // exit and witness are checked against the installed mesh every time.
    private static readonly AuditedCampPocket[] AuditedCampPockets =
    [
        new(51, "large dragonfly", new(526102, 543403), 850,
            new(528150, 539061, 3140), new(529168, 539735, 3141)),
        new(151, "boobrie hatchling", new(285151, 346544), 2_100,
            new(282972, 344890, 3456), new(288755, 343565, 3477)),
        new(200, "feccan", new(337977, 479973), 1_600,
            new(337734, 478720, 5325), new(339331, 476773, 5247)),
        new(100, "huldu outcast", new(812733, 722524), 1_400,
            new(810855, 719902, 5034), new(811550, 721706, 5104)),
        new(100, "green serpent", new(759936, 751403), 1_600,
            new(759817, 749618, 4568), new(757921, 743637, 4410)),
    ];

    public static bool IsKnownFloorDriftArea(ushort regionId, Vector3 position, out float maximumCorrection)
    {
        maximumCorrection = 0;
        foreach (FloorDriftHotspot hotspot in FloorDriftHotspots)
        {
            if (hotspot.RegionId != regionId ||
                hotspot.SourceZ.HasValue && MathF.Abs(position.Z - hotspot.SourceZ.Value) > 64 ||
                Vector2.DistanceSquared(new(position.X, position.Y), hotspot.Center) > hotspot.Radius * hotspot.Radius)
                continue;

            maximumCorrection = hotspot.MaximumVerticalCorrection;
            return true;
        }

        return false;
    }

    public static bool TryResolveFloor(
        IPathfindingMgr nav,
        Zone zone,
        ushort regionId,
        Vector3 position,
        out Vector3 floor)
    {
        floor = position;
        if (nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone) ||
            !IsKnownFloorDriftArea(regionId, position, out float maximumCorrection))
            return false;

        Vector3? candidate = nav.GetClosestPoint(zone, position, 2, 2,
            (int)MathF.Ceiling(maximumCorrection), nav.DefaultFilters);
        if (!candidate.HasValue || !float.IsFinite(candidate.Value.X) ||
            !float.IsFinite(candidate.Value.Y) || !float.IsFinite(candidate.Value.Z) ||
            Vector2.DistanceSquared(new(position.X, position.Y), new(candidate.Value.X, candidate.Value.Y)) > 4)
            return false;

        float verticalCorrection = MathF.Abs(position.Z - candidate.Value.Z);
        if (verticalCorrection <= 32 || verticalCorrection > maximumCorrection ||
            !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, candidate.Value))
            return false;

        floor = candidate.Value;
        return true;
    }

    /// <summary>
    /// Corrects only the audited SI route-source signatures onto the nearest
    /// installed floor after proving that floor has a real, bidirectional
    /// corridor to the unchanged database portal. This preserves the bot's
    /// objective and makes it walk to and use the real exit normally.
    /// </summary>
    public static bool TryResolveAuditedCrossingSource(
        IPathfindingMgr nav,
        Region region,
        DbZonePoint edge,
        Vector3 actor,
        out Vector3 corrected)
    {
        corrected = default;
        if (nav == null || region == null || edge == null || !nav.IsAvailable ||
            edge.SourceRegion != region.ID)
            return false;

        foreach (CrossingSourceHotspot hotspot in CrossingSourceHotspots)
        {
            if (hotspot.EdgeId != edge.Id || hotspot.RegionId != region.ID ||
                Vector3.DistanceSquared(actor, hotspot.Center) > hotspot.SignatureRadius * hotspot.SignatureRadius)
                continue;

            Zone actorZone = region.GetZone((int)actor.X, (int)actor.Y);
            Zone portalZone = region.GetZone(edge.SourceX, edge.SourceY);
            if (actorZone == null || actorZone != portalZone || !nav.HasNavmesh(actorZone))
                return false;

            // These are repairs, not a second movement controller. A normal
            // usable floor must not be relocated on every crossing pulse just
            // because slope interpolation differs from Detour by a few units.
            if (AutonomousNavigationSurface.TryFloor(nav, actorZone, actor, out _))
                return false;

            Vector3? actorFloor = nav.GetClosestPoint(actorZone, actor,
                hotspot.SearchHorizontal, hotspot.SearchHorizontal, hotspot.SearchVertical, nav.DefaultFilters);
            Vector3 portal = new(edge.SourceX, edge.SourceY, edge.SourceZ);
            Vector3? portalFloor = nav.GetClosestPoint(actorZone, portal, 64, 64, 256, nav.DefaultFilters);
            if (!actorFloor.HasValue || !portalFloor.HasValue ||
                !float.IsFinite(actorFloor.Value.X) || !float.IsFinite(actorFloor.Value.Y) ||
                !float.IsFinite(actorFloor.Value.Z) ||
                Vector2.Distance(new(actorFloor.Value.X, actorFloor.Value.Y), new(actor.X, actor.Y)) >
                    hotspot.MaximumHorizontalCorrection ||
                MathF.Abs(actorFloor.Value.Z - actor.Z) > hotspot.MaximumVerticalCorrection ||
                !AutonomousRendezvousNavigation.HasLocalExit(nav, actorZone, actorFloor.Value) ||
                !AutonomousRendezvousNavigation.HasLocalExit(nav, actorZone, portalFloor.Value) ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, actorZone, actorFloor.Value, portalFloor.Value) ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, actorZone, portalFloor.Value, actorFloor.Value))
                return false;

            corrected = actorFloor.Value;
            return true;
        }

        return false;
    }

    public static bool IsJordheimServicePocket(ushort regionId, Vector3 position) =>
        regionId == 101 &&
        JordheimServicePockets.Any(center =>
            Vector2.DistanceSquared(new(position.X, position.Y), center) <= 360 * 360) &&
        position.Z is >= 8_700 and <= 8_950;

    /// <summary>
    /// Audited Jordheim merchant points are close enough for a real
    /// transaction, but client collision around their exact point traps actors
    /// even though Detour reports a complete path. Choose a point in
    /// real interaction range which is connected to Jordheim's lower city.
    /// Other services retain the general resolver.
    /// </summary>
    public static bool TryResolveJordheimServiceApproach(
        IPathfindingMgr nav,
        Zone zone,
        ushort regionId,
        Vector3 actor,
        Vector3 service,
        int interactionRadius,
        out Vector3 approach)
    {
        approach = default;
        if (!IsJordheimServicePocket(regionId, service) || nav == null || zone == null ||
            !nav.IsAvailable || !nav.HasNavmesh(zone) || interactionRadius < 96)
            return false;

        if (!AutonomousNavigationSurface.TryFloor(nav, zone, actor, out Vector3 start) ||
            !AutonomousRendezvousNavigation.TryChooseFixedPoint(nav, zone,
                AutonomousRendezvousNavigation.JordheimMeetingPoint, out Vector3 network))
            return false;

        // Leave enough room for integer rounding and the actor's collision
        // radius. A point on the exact 256-unit boundary was reported by the
        // pathfinder as reached while the real 3-D interaction test remained
        // a fraction outside range.
        int maximumRadius = Math.Max(96, interactionRadius - 16);
        var candidates = new List<Vector3>();
        foreach (int radius in new[] { 112, 144, 176, 208, maximumRadius }.Distinct())
        {
            if (radius > maximumRadius)
                continue;
            for (int angle = 0; angle < 360; angle += 15)
            {
                float radians = angle * MathF.PI / 180f;
                Vector3 raw = new(service.X + MathF.Cos(radians) * radius,
                    service.Y + MathF.Sin(radians) * radius, service.Z);
                Vector3? floor = nav.GetClosestPoint(zone, raw, 36, 36, 160, nav.DefaultFilters);
                if (!floor.HasValue ||
                    Vector3.Distance(floor.Value, service) > maximumRadius ||
                    Vector2.DistanceSquared(new(floor.Value.X, floor.Value.Y), new(service.X, service.Y)) < 96 * 96 ||
                    MathF.Abs(floor.Value.Z - service.Z) > 192)
                    continue;
                if (!candidates.Any(existing => Vector3.DistanceSquared(existing, floor.Value) < 24 * 24))
                    candidates.Add(floor.Value);
            }
        }

        foreach (Vector3 candidate in candidates.OrderBy(point => Vector3.DistanceSquared(start, point)))
        {
            if (!AutonomousRendezvousNavigation.HasLocalExit(nav, zone, candidate) ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, candidate, network) ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, network, candidate))
                continue;
            approach = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Two imported capital exits land on small, locally walkable components
    /// which do not connect to the ordinary outdoor road. Preserve the real
    /// DbZonePoint edge and target region, but resolve its server-side landing
    /// to an installed-mesh-proven point on the adjacent main component.
    /// </summary>
    public static bool TryResolveAuditedPortalLanding(
        IPathfindingMgr nav,
        Region region,
        DbZonePoint edge,
        out Vector3 landing)
    {
        landing = default;
        if (nav == null || region == null || edge == null || edge.TargetRegion != region.ID || !nav.IsAvailable)
            return false;

        Vector3 anchor;
        Vector3 witness;
        if (edge.Id == 5 && edge.SourceRegion == 10 && edge.TargetRegion == 1)
        {
            anchor = CotswoldEastGateRoad;
            witness = new(560054, 513908, 2619);
        }
        else if (edge.Id == 26 && edge.SourceRegion == 201 && edge.TargetRegion == 200)
        {
            anchor = ConnachtMainRoad;
            witness = ConnachtMainNetworkWitness;
        }
        else
        {
            return false;
        }

        Zone zone = region.GetZone((int)anchor.X, (int)anchor.Y);
        Zone witnessZone = region.GetZone((int)witness.X, (int)witness.Y);
        if (zone == null || zone != witnessZone || !nav.HasNavmesh(zone))
            return false;
        Vector3? floor = nav.GetClosestPoint(zone, anchor, 96, 96, 256, nav.DefaultFilters);
        Vector3? network = nav.GetClosestPoint(zone, witness, 96, 96, 256, nav.DefaultFilters);
        if (!floor.HasValue || !network.HasValue ||
            !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value) ||
            !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, network.Value))
            return false;
        landing = floor.Value;
        return true;
    }

    /// <summary>
    /// Identifies the narrowly audited Connacht transfer-loop footprint.
    /// </summary>
    public static bool IsConnachtTransferLoopArea(ushort regionId, Vector3 position) =>
        regionId == 200 && position.X is >= 310_000 and <= 314_500 &&
        position.Y is >= 469_000 and <= 476_000;

    /// The Connacht side of the Tir na Nog transfer can circulate between a
    /// portal landing and a locally walkable road component without reaching
    /// the wider zone. Only after normal path and side-step recovery fail do we
    /// expose this nearby, installed-mesh-validated main-road point.
    /// </summary>
    public static bool TryGetImmediateEscape(IPathfindingMgr nav, Region region, ushort regionId,
        Vector3 position, out Vector3 escape)
    {
        escape = default;
        if (region == null || nav?.IsAvailable != true)
            return false;

        foreach (TerminalPocket pocket in TerminalPockets)
        {
            if (pocket.RegionId != regionId ||
                pocket.SourceZ.HasValue && MathF.Abs(position.Z - pocket.SourceZ.Value) > 64 ||
                Vector2.DistanceSquared(new(position.X, position.Y), pocket.Center) > pocket.Radius * pocket.Radius)
                continue;
            Zone zone = region.GetZone((int)pocket.Escape.X, (int)pocket.Escape.Y);
            if (zone == null || zone != region.GetZone((int)pocket.NetworkWitness.X, (int)pocket.NetworkWitness.Y) ||
                !nav.HasNavmesh(zone))
                return false;
            Vector3? floor = nav.GetClosestPoint(zone, pocket.Escape, 96, 96, 512, nav.DefaultFilters);
            Vector3? witness = nav.GetClosestPoint(zone, pocket.NetworkWitness, 96, 96, 512, nav.DefaultFilters);
            if (!floor.HasValue || !witness.HasValue ||
                !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value) ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, witness.Value))
                return false;
            escape = floor.Value;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves only the measured physical-collision pockets for the audited
    /// outdoor objectives. This is called after normal side-step recovery has
    /// failed, so ordinary movement and all unrelated camps remain unchanged.
    /// </summary>
    public static bool TryGetAuditedCampEscape(IPathfindingMgr nav, Region region, ushort regionId,
        string monsterName, Vector3 position, out Vector3 escape)
    {
        escape = default;
        if (region == null || nav?.IsAvailable != true || string.IsNullOrWhiteSpace(monsterName))
            return false;

        foreach (AuditedCampPocket pocket in AuditedCampPockets)
        {
            if (pocket.RegionId != regionId ||
                !string.Equals(pocket.MonsterName, monsterName.Trim(), StringComparison.OrdinalIgnoreCase) ||
                Vector2.DistanceSquared(new(position.X, position.Y), pocket.Center) > pocket.Radius * pocket.Radius)
                continue;

            Zone zone = region.GetZone((int)pocket.Escape.X, (int)pocket.Escape.Y);
            Zone witnessZone = region.GetZone((int)pocket.NetworkWitness.X, (int)pocket.NetworkWitness.Y);
            if (zone == null || zone != witnessZone || !nav.HasNavmesh(zone))
                return false;

            Vector3? floor = nav.GetClosestPoint(zone, pocket.Escape, 96, 96, 512, nav.DefaultFilters);
            Vector3? witness = nav.GetClosestPoint(zone, pocket.NetworkWitness, 96, 96, 512, nav.DefaultFilters);
            if (!floor.HasValue || !witness.HasValue ||
                !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value) ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, witness.Value))
                return false;

            escape = floor.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The live starter-dungeon audit found outdoor actors on tiny imported
    /// terrain components, or with a stale height hundreds of units away from
    /// the installed floor.  Search only after ordinary routing has failed,
    /// only for the real Mithra/Muire entrances, and only within 1,750 units.
    /// A candidate is accepted only when the installed meshes prove a complete
    /// zone-by-zone route from it to the unchanged database portal.
    /// </summary>
    public static bool TryResolveStarterDungeonRouteSurface(IPathfindingMgr nav, Region region,
        ushort sourceRegion, ushort dungeonRegion, Vector3 position, out Vector3 recovery)
    {
        recovery = default;
        if (nav?.IsAvailable != true || region == null || region.ID != sourceRegion)
            return false;

        Vector3 portal;
        if (sourceRegion == 1 && dungeonRegion == 21)
            portal = new(603376, 523018, 3136);
        else if (sourceRegion == 200 && dungeonRegion == 221)
            portal = new(322927, 458013, 6673);
        else
            return false;

        Zone portalZone = region.GetZone((int)portal.X, (int)portal.Y);
        if (portalZone == null || !nav.HasNavmesh(portalZone))
            return false;
        Vector3? portalFloor = nav.GetClosestPoint(portalZone, portal, 96, 96, 512, nav.DefaultFilters);
        if (!portalFloor.HasValue || !AutonomousRendezvousNavigation.HasLocalExit(nav, portalZone, portalFloor.Value))
            return false;

        foreach (int radius in Enumerable.Range(0, 8).Select(index => index * 250))
        {
            int angleStep = radius == 0 ? 360 : 10;
            for (int angle = 0; angle < 360; angle += angleStep)
            {
                float radians = angle * MathF.PI / 180f;
                Vector3 raw = position + new Vector3(MathF.Cos(radians) * radius,
                    MathF.Sin(radians) * radius, 0);
                Zone candidateZone = region.GetZone((int)raw.X, (int)raw.Y);
                if (candidateZone == null || !nav.HasNavmesh(candidateZone))
                    continue;
                int horizontalSearch = radius == 0 ? 2 : 48;
                Vector3? candidate = nav.GetClosestPoint(candidateZone, raw,
                    horizontalSearch, horizontalSearch, 1_024, nav.DefaultFilters);
                if (!candidate.HasValue ||
                    Vector2.Distance(new(candidate.Value.X, candidate.Value.Y), new(position.X, position.Y)) > 1_850 ||
                    Vector3.DistanceSquared(candidate.Value, position) <= 32 * 32 ||
                    !AutonomousRendezvousNavigation.HasLocalExit(nav, candidateZone, candidate.Value) ||
                    !HasCompleteZoneRoute(nav, region, candidateZone, portalZone,
                        candidate.Value, portalFloor.Value))
                    continue;

                recovery = candidate.Value;
                return true;
            }
        }

        return false;
    }

    private static bool HasCompleteZoneRoute(IPathfindingMgr nav, Region region, Zone sourceZone,
        Zone destinationZone, Vector3 source, Vector3 destination)
    {
        Zone currentZone = sourceZone;
        Vector3 cursor = source;
        for (int hop = 0; hop < 16 && currentZone != destinationZone; hop++)
        {
            if (!AutonomousZoneItinerary.TryNextStep(region, currentZone, destinationZone,
                    cursor, destination, nav, out AutonomousZoneBoundaryRouting.Step step))
                return false;
            cursor = step.Outside;
            currentZone = region.GetZone((int)cursor.X, (int)cursor.Y);
            if (currentZone == null)
                return false;
        }
        return currentZone == destinationZone &&
            AutonomousZoneItinerary.HasCompleteCorridor(nav, destinationZone, cursor, destination);
    }
}

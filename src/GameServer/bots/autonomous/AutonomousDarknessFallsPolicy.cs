using System;
using System.Numerics;
using DOL.Database;

namespace DOL.GS;

/// <summary>
/// Pure policy seams for Darkness Falls. Live entrance authority remains in
/// DFEnterJumpPoint; these helpers make its keep-control and local-PvP rules
/// independently testable.
/// </summary>
public static class AutonomousDarknessFallsPolicy
{
    public const ushort RegionId = 249;

    // Solo bots may still grind Darkness Falls, but only from level 25 and only
    // against targets that con blue or easier. Groups keep their own rules.
    public const int SoloMinimumLevel = 25;
    public const ConColor SoloMaximumCon = ConColor.BLUE;

    public static ushort HomeRegion(eRealm realm) => realm switch
    {
        eRealm.Albion => 1,
        eRealm.Midgard => 100,
        eRealm.Hibernia => 200,
        _ => 0,
    };

    // Normal home-side entrances. Other real DF entries (including relic
    // portals) remain available to players, but autonomous bots must not
    // choose them solely because they are closer in a straight line.
    public static ushort HomeEntranceZonePointId(eRealm realm) => realm switch
    {
        eRealm.Albion => 81,
        eRealm.Midgard => 84,
        eRealm.Hibernia => 87,
        _ => 0,
    };

    // Each physical DF exit has a DB row for every realm. Matching the row's
    // Realm and TargetRegion alone would still let a bot leave via another
    // faction's corridor. These are the exits below each home entrance.
    public static ushort HomeExitZonePointId(eRealm realm) => realm switch
    {
        eRealm.Albion => 74,
        eRealm.Midgard => 70,
        eRealm.Hibernia => 72,
        _ => 0,
    };

    public static bool CanEnter(
        eRealm realm,
        eRealm currentOwner,
        eRealm previousOwner,
        long nowTick,
        long lastSwapTick,
        long gracePeriod,
        bool allowAllRealms,
        bool normalServer)
    {
        if (!normalServer || allowAllRealms)
            return true;
        if (realm == eRealm.None)
            return false;
        if (realm == previousOwner && lastSwapTick + Math.Max(0, gracePeriod) >= nowTick)
            return true;
        return realm == currentOwner;
    }

    public static bool CanUseRegionEdge(eRealm realm, ushort sourceRegion, ushort targetRegion, Func<eRealm, bool> canEnter)
    {
        if (sourceRegion == RegionId && targetRegion != RegionId)
            return HomeRegion(realm) != 0 && targetRegion == HomeRegion(realm);
        if (targetRegion != RegionId || sourceRegion == RegionId)
            return true;
        // Autonomous travel enters through its own realm's home-side tunnel.
        // Relic-keep portal rows for other realms are valid player content,
        // but must not become a shortcut to another realm's DF wing for bots.
        ushort homeRegion = HomeRegion(realm);
        return homeRegion != 0 && sourceRegion == homeRegion && canEnter?.Invoke(realm) == true;
    }

    /// <summary>Autonomous bots use only their normal home entrance and the
    /// physical exit below it. Hibernia's normal entrance is a realm-neutral
    /// DB row, so its exact ID and home source make that exception safe.
    /// Player portal behavior and the database are not changed.</summary>
    public static bool CanUsePortalRow(eRealm realm, DbZonePoint point)
    {
        if (point == null) return false;
        if (point.TargetRegion == RegionId && point.SourceRegion != RegionId)
            return HomeRegion(realm) != 0 && point.SourceRegion == HomeRegion(realm) &&
                point.Id == HomeEntranceZonePointId(realm) &&
                (realm == eRealm.Hibernia ? point.Realm == 0 : point.Realm == (ushort)realm);
        if (point.SourceRegion != RegionId || point.TargetRegion == RegionId)
            return true;
        return HomeRegion(realm) != 0 && point.TargetRegion == HomeRegion(realm) &&
            point.Realm == (ushort)realm && point.Id == HomeExitZonePointId(realm);
    }

    /// <summary>The installed outdoor itinerary reverses across this exact
    /// Lough Derg/Valley seam while heading for Hibernia's home DF entrance.
    /// A solo bot without a connected real ticket should replan here rather
    /// than oscillate until the forty-five-minute watchdog expires.</summary>
    public static bool IsAuditedHiberniaExteriorLoop(eRealm realm, DbZonePoint crossing,
        ushort currentRegion, ushort currentZone, Vector3 position, int groupSize) =>
        groupSize <= 1 && realm == eRealm.Hibernia &&
        crossing?.Id == HomeEntranceZonePointId(eRealm.Hibernia) &&
        CanUsePortalRow(realm, crossing) && currentRegion == HomeRegion(realm) &&
        currentZone == 206 &&
        Vector3.DistanceSquared(position, new Vector3(385088, 483424, 7262)) <= 192 * 192;

    /// <summary>An already-inside bot may still select its own physical exit
    /// after ordinary DF goals are closed by a stale or absent certificate.
    /// This never admits a new entrance or another faction's exit.</summary>
    public static bool CanEvacuateThroughHomeExit(eRealm realm, ushort currentRegion, DbZonePoint point) =>
        currentRegion == RegionId && point?.SourceRegion == RegionId &&
        point.TargetRegion != RegionId && CanUsePortalRow(realm, point);

    public static bool MustRetireOrdinaryGoal(ushort goalRegion, bool ordinaryCatalogReady) =>
        goalRegion == RegionId && !ordinaryCatalogReady;

    public static bool CanEngageLocalOpponent(
        eRealm attackerRealm,
        eRealm targetRealm,
        ushort attackerRegion,
        ushort targetRegion,
        bool targetAlive,
        bool allowedByServerRules) =>
        attackerRegion == RegionId && targetRegion == RegionId && targetAlive && allowedByServerRules &&
        attackerRealm != eRealm.None && targetRealm != eRealm.None && attackerRealm != targetRealm;
}

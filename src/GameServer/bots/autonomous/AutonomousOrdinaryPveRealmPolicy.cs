namespace DOL.GS;

/// <summary>
/// Ordinary XP camps belong to the realm whose frontier contains them. The
/// frontier itself remains traversable for RvR, defence and realm events;
/// those activities must not use this PvE-only assignment gate.
/// </summary>
public static class AutonomousOrdinaryPveRealmPolicy
{
    public static bool CanAssignCamp(eRealm botRealm, ushort regionId, ushort zoneId)
    {
        eRealm frontierOwner = (regionId, zoneId) switch
        {
            (1, 11 or 12 or 14 or 15) => eRealm.Albion,
            (100, 111 or 112 or 113 or 115) => eRealm.Midgard,
            (200, 210 or 211 or 212 or 214) => eRealm.Hibernia,
            _ => eRealm.None
        };

        // Other regions, including the shared Darkness Falls region, continue
        // through the existing realm, dungeon and navmesh access policies.
        return frontierOwner == eRealm.None || frontierOwner == botRealm;
    }
}

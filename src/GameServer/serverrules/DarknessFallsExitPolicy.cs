using System.Collections.Generic;
using System.Linq;
using DOL.Database;

namespace DOL.GS.ServerRules;

/// <summary>
/// The six Darkness Falls exit IDs each have one destination row per realm.
/// Even a GM must use the row for their character's realm; an ID-only lookup
/// otherwise picks whichever realm happens to be first in the database.
/// </summary>
public static class DarknessFallsExitPolicy
{
    public const ushort RegionId = 249;

    public static bool IsExitRequest(ushort currentRegion, ushort zonePointId) =>
        currentRegion == RegionId && zonePointId is 70 or 72 or 73 or 74 or 75 or 76;

    public static ushort HomeRegion(eRealm realm) => realm switch
    {
        eRealm.Albion => 1,
        eRealm.Midgard => 100,
        eRealm.Hibernia => 200,
        _ => 0,
    };

    public static DbZonePoint SelectRealmExit(IEnumerable<DbZonePoint> rows, eRealm realm, ushort zonePointId)
    {
        ushort home = HomeRegion(realm);
        if (home == 0 || rows == null)
            return null;

        return rows.FirstOrDefault(point => point != null && point.Id == zonePointId &&
            IsExitRequest(point.SourceRegion, point.Id) &&
            point.Realm == (ushort)realm && point.TargetRegion == home);
    }
}

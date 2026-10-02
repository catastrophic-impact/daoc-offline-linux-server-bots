using System.Numerics;

namespace DOL.GS;

public static class AutonomousRealmBoundary
{
    public static bool Allows(eRealm realm, ushort region, ushort zone)
    {
        eRealm owner = AutonomousWorldBotController.ProtectedRealm(region, zone);
        return owner == eRealm.None || owner == realm;
    }

    public static bool Allows(GameNPC actor, Vector3 point)
    {
        if (actor is not GameBot { IsAutonomousWorldBot: true } bot) return true;
        eRealm regionalOwner = AutonomousWorldBotController.ProtectedRealm(bot.CurrentRegionID, ushort.MaxValue);
        // Own homeland/shared regions need no coordinate lookup on movement ticks.
        if (regionalOwner == eRealm.None || regionalOwner == bot.Realm) return true;
        Zone zone = bot.CurrentRegion?.GetZone((int)point.X, (int)point.Y);
        return zone != null && Allows(bot.Realm, bot.CurrentRegionID, zone.ID);
    }
}

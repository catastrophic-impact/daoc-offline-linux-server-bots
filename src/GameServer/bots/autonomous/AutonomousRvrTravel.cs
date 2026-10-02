using System;
using System.Numerics;
using System.Linq;
using DOL.GS.Keeps;

namespace DOL.GS;

/// <summary>Route variation is a connected waypoint, not steering noise.</summary>
public static class AutonomousRvrTravel
{
    public static bool TraverseFriendlyDoor(GameBot bot, Vector3 destination)
    {
        if (!AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR) ||
            bot.TempProperties.GetProperty<long>("RvrDoorPassUntil") > GameLoop.GameLoopTime) return false;
        foreach (GameKeepDoor door in GameServer.KeepManager.GetKeepsOfRegion(bot.CurrentRegionID)
                     .Where(keep => keep.Realm == bot.Realm).SelectMany(keep => keep.Doors.Values))
        {
            if (!bot.IsWithinRadius(door, WorldMgr.INTERACT_DISTANCE) || Math.Abs(door.Z - bot.Z) > 160) continue;
            Vector2 toDoor = new(door.X - bot.X, door.Y - bot.Y);
            Vector2 toGoal = new(destination.X - bot.X, destination.Y - bot.Y);
            if (toGoal.LengthSquared() < toDoor.LengthSquared() || Vector2.Dot(toDoor, toGoal) <= 0) continue;
            // Only use a doorway lying along the current travel leg, never
            // hop sideways through an unrelated nearby wall/door.
            float cross = Math.Abs(toDoor.X * toGoal.Y - toDoor.Y * toGoal.X) / Math.Max(1, toGoal.Length());
            if (cross > 100 || !door.TryTraverse(bot)) continue;
            bot.TempProperties.SetProperty("RvrDoorPassUntil", GameLoop.GameLoopTime + 2000);
            return true;
        }
        return false;
    }
    private static readonly int[] BorderDoors =
        [11020501, 11020502, 12000101, 12000102, 102093501, 102093502,
         111161301, 111161302, 206016801, 206016802, 207156901, 207156902];

    public static bool OpenNearbyBorderDoors(GameBot bot, Vector3 destination)
    {
        if (bot?.IsAutonomousWorldBot != true ||
            !AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR)) return false;
        bool opened = false;
        foreach (int id in BorderDoors)
        {
            GameDoorBase door = DoorMgr.GetDoorByID(id);
            if (door == null || door.CurrentRegion != bot.CurrentRegion || door.Locked ||
                door.State != eDoorState.Closed || door.Realm != bot.Realm && door.Realm != eRealm.Door ||
                !bot.IsWithinRadius(door, ServerProperties.Properties.WORLD_PICKUP_DISTANCE * 3)) continue;
            Vector2 heading = new(destination.X - bot.X, destination.Y - bot.Y);
            Vector2 toDoor = new(door.X - bot.X, door.Y - bot.Y);
            if (Vector2.Dot(heading, toDoor) < 0 || Math.Abs(door.Z - bot.Z) > 400) continue;
            // This is the same authoritative operation sent by the player's
            // lever/door request. The native five-second closure remains intact.
            door.Open(bot);
            opened |= door.State == eDoorState.Open;
        }
        return opened;
    }

    public static Vector3 ChooseWaypoint(GameBot bot, Vector3 destination)
    {
        Vector3 start = new(bot.X, bot.Y, bot.Z);
        Vector2 delta = new(destination.X - start.X, destination.Y - start.Y);
        float distance = delta.Length();
        Zone zone = bot.CurrentZone;
        var nav = PathfindingProvider.Instance;
        if (distance < 3500 || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return destination;
        Vector2 direction = delta / distance;
        float side = ((bot.DatabaseID % 7) - 3) * 200;
        Vector3 raw = start + new Vector3(direction.X * 2400 - direction.Y * side,
            direction.Y * 2400 + direction.X * side, 0);
        if (bot.CurrentRegion.GetZone((int)raw.X, (int)raw.Y) != zone ||
            !AutonomousNavigationSurface.TryFloor(nav, zone, raw, out Vector3 floor) ||
            Math.Abs(floor.Z - raw.Z) > 256 ||
            !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, start, floor) ||
            (bot.CurrentRegion.GetZone((int)destination.X, (int)destination.Y) == zone &&
             !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor, destination))) return destination;
        return floor;
    }
}

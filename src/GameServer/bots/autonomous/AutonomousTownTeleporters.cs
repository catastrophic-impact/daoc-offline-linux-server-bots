using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.Database;
using DOL.GS.Scripts;
using DOL.GS.ServerProperties;

namespace DOL.GS;

/// <summary>
/// The free town teleporters (LiveTeleporter: Master Visur, Stor Gothi Annark, Channeler Glasny) used by
/// gamebots the way players use them: walk to the realm's own teleporter, wait through the channel and
/// arrive at one of that realm's listed destinations (never another realm's). A plan is only taken when
/// it beats walking (and the caller's stable-master estimate) clearly. Housing, battleground and epic
/// dungeon destinations are never planned. On by default (owner, 2026-10-07, after run 14: camps reached
/// about 40% sooner); the server property bot_use_town_teleporters switches it off. A new key, because the
/// first test build stored the old off-by-default key (bot_town_teleporters) as False.
/// </summary>
public static class AutonomousTownTeleporters
{
    [ServerProperty("autonomous", "bot_use_town_teleporters",
        "Gamebots may use the free town teleporters of their own realm when that is clearly faster than walking or a stable-master route.", true)]
    public static bool Enabled = true;

    public const string OverrideVariable = "OFFLINE_DAOC_BOT_TELEPORTERS";
    private static readonly bool EnvironmentEnabled = Environment.GetEnvironmentVariable(OverrideVariable) == "1";
    public static bool IsEnabled => Enabled || EnvironmentEnabled;

    public const double ChannelSeconds = 6;
    public const int ChannelMilliseconds = 6_000;
    public const float MaximumWalkToPorter = 15_000f;
    public const float SameTownRadius = 2_500f;
    public const int ArrivalRadius = 250;
    public const double MinimumSavingSeconds = 60;
    public const double MinimumSavingFraction = 0.30;

    public sealed record Plan(GameNPC Porter, DbTeleport Destination, double Seconds, double BaselineSeconds);

    /// <summary>How far from the teleporter NPC a bot stops to use it (inside ArrivalRadius).</summary>
    public const int StandOffDistance = 170;

    /// <summary>
    /// Where a bot stops to use a teleporter: a short way out from the NPC on the side it comes from, spread a little
    /// per bot so a group fans out around it (owner 2026-10-08: bots walked onto the NPC itself and players could not
    /// click the teleporter). Snapped to the navmesh; the NPC's own spot only when no ground is found around it.
    /// </summary>
    public static Vector3 StandOff(GameNPC porter, GameLiving bot)
    {
        double angle = bot.X == porter.X && bot.Y == porter.Y ? 0 : Math.Atan2(bot.Y - porter.Y, bot.X - porter.X);
        angle += ((int)(bot.ObjectID % 7) - 3) * 0.35;
        var point = new Vector3(porter.X + (float)(Math.Cos(angle) * StandOffDistance),
            porter.Y + (float)(Math.Sin(angle) * StandOffDistance), porter.Z);
        var spot = new Vector3(porter.X, porter.Y, porter.Z);
        Zone zone = porter.CurrentZone;
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
            return point;
        Vector3? ground = nav.GetClosestPoint(zone, point, 60, 60, 256, nav.DefaultFilters);
        if (ground.HasValue && Vector2.Distance(new(ground.Value.X, ground.Value.Y), new(spot.X, spot.Y)) >= 90)
            return ground.Value;
        return nav.GetClosestPoint(zone, spot, 160, 160, 256, nav.DefaultFilters) ?? spot;
    }

    private static readonly HashSet<ushort> NeverPlannedRegions = [2, 102, 202, 73, 130, 60, 160, 191, 249];

    private sealed record PorterCache(long Until, GameNPC[] Porters);
    private static readonly ConcurrentDictionary<ushort, PorterCache> PortersByRegion = new();

    public static GameNPC[] Porters(Region region)
    {
        if (region == null) return [];
        long now = GameLoop.GameLoopTime;
        if (PortersByRegion.TryGetValue(region.ID, out var cached) && now < cached.Until) return cached.Porters;
        GameNPC[] porters = region.Objects.OfType<LiveTeleporter>()
            .Where(npc => npc.ObjectState == GameObject.eObjectState.Active && npc.Realm != eRealm.None)
            .Cast<GameNPC>().ToArray();
        PortersByRegion[region.ID] = new(now + 600_000, porters);
        return porters;
    }

    public static IEnumerable<DbTeleport> Destinations(eRealm realm) => WorldMgr.GetTeleportLocations(realm, string.Empty)
        .Where(destination => destination.RegionID > 0 && !NeverPlannedRegions.Contains((ushort)destination.RegionID) &&
                              WorldMgr.GetRegion((ushort)destination.RegionID) is { IsDisabled: false } region &&
                              region.GetZone(destination.X, destination.Y) != null);

    /// <summary>
    /// Best teleporter trip from the bot to a goal (region + point): walk to an own-realm teleporter in the
    /// bot's region, channel, then walk from the destination. Null unless it saves at least a minute and
    /// 30% against <paramref name="baselineSeconds"/> (walking, or the stable route when cheaper).
    /// </summary>
    public static Plan TryPlan(GameBot bot, ushort goalRegion, Vector3 goal, double baselineSeconds,
        IReadOnlyDictionary<GameNPC, long> excluded = null)
    {
        if (bot?.CurrentRegion == null || !double.IsFinite(baselineSeconds) || baselineSeconds <= MinimumSavingSeconds)
            return null;
        double speed = Math.Max(150, (int)bot.MaxSpeed);
        long now = GameLoop.GameLoopTime;
        DbTeleport[] destinations = BattlegroundBrackets.IsBattlegroundRegion(goalRegion)
            ? BattlegroundDestinations(bot, goalRegion)
            : Destinations(bot.Realm).Where(destination => destination.RegionID == goalRegion).ToArray();
        if (destinations.Length == 0) return null;
        Vector3 actor = new(bot.X, bot.Y, bot.Z);
        Plan best = null;
        foreach (GameNPC porter in Porters(bot.CurrentRegion))
        {
            if (porter.Realm != bot.Realm || excluded != null && excluded.TryGetValue(porter, out long until) && now < until)
                continue;
            float toPorter = Vector2.Distance(new(actor.X, actor.Y), new(porter.X, porter.Y));
            if (toPorter > MaximumWalkToPorter) continue;
            foreach (DbTeleport destination in destinations)
            {
                if (destination.RegionID == porter.CurrentRegionID &&
                    Vector2.Distance(new(destination.X, destination.Y), new(porter.X, porter.Y)) < SameTownRadius)
                    continue;
                double seconds = toPorter / speed + ChannelSeconds +
                                 Vector2.Distance(new(destination.X, destination.Y), new(goal.X, goal.Y)) / speed;
                if (best == null || seconds < best.Seconds)
                    best = new(porter, destination, seconds, baselineSeconds);
            }
        }
        if (best == null || best.Seconds + MinimumSavingSeconds > baselineSeconds ||
            best.Seconds > baselineSeconds * (1 - MinimumSavingFraction))
            return null;
        return best;
    }

    /// <summary>
    /// The teleporters' [Battlegrounds] choice for a bot: only its own level bracket's battleground, arriving at its realm's portal keep exactly like a player would.
    /// </summary>
    public static DbTeleport[] BattlegroundDestinations(GameBot bot, ushort region)
    {
        BattlegroundBrackets.Bracket bracket = BattlegroundBrackets.ForRegion(region);
        if (bracket == null || !BattlegroundBrackets.CanEnter(bot, region)) return [];
        GameLocation entry = BattlegroundBrackets.BotArrival(bracket, bot.Realm);
        if (entry == null || WorldMgr.GetRegion(region) is not { IsDisabled: false } live || live.GetZone(entry.X, entry.Y) == null)
            return [];
        return [new DbTeleport { TeleportID = bracket.Name, Type = string.Empty, Realm = (int)bot.Realm, RegionID = region,
            X = entry.X, Y = entry.Y, Z = entry.Z, Heading = entry.Heading }];
    }

    /// <summary>The arrival floor for party slot N around a destination (snapped to the navmesh floor).</summary>
    public static Vector3 Arrival(DbTeleport destination, int slot)
    {
        Region region = WorldMgr.GetRegion((ushort)destination.RegionID);
        Vector3 raw = new(destination.X, destination.Y, destination.Z);
        if (slot > 0)
        {
            double angle = slot * 2.39996323;
            float radius = 90 + 70 * MathF.Sqrt(slot);
            raw += new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
        }
        Zone zone = region?.GetZone((int)raw.X, (int)raw.Y);
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return raw;
        // Some destinations sit above the floor (Mularn 61 units); bots land on the floor.
        bool battleground = BattlegroundBrackets.IsBattlegroundRegion((ushort)destination.RegionID);
        float lateral = 96, vertical = battleground ? 800 : 400;
        return nav.GetClosestPoint(zone, raw, lateral, lateral, vertical, nav.DefaultFilters) ??
               nav.GetClosestPoint(zone, new(destination.X, destination.Y, destination.Z), lateral, lateral, vertical, nav.DefaultFilters) ??
               new Vector3(destination.X, destination.Y, destination.Z);
    }
}

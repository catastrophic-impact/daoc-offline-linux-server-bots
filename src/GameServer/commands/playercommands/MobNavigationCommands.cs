using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Globalization;
using System.Text;
using System.Threading;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.Keeps;
using DOL.GS.Movement;
using DOL.GS.PacketHandler;
using DOL.GS.PacketHandler.Client.v168;
using DOL.GS.ServerRules;

namespace DOL.GS.Commands;

/// <summary>
/// Player-requested travel aid. This is intentionally independent from the
/// autonomous playerbot camp selector and never changes a bot's goal state.
/// </summary>
public static class PlayerMobNavigator
{
    private const int TickMilliseconds = 250;
    private const int CampCellSize = 5000;
    private const int ArrivalRadius = 325;
    internal const int DungeonExteriorMinimumDistance = 425;
    internal const int DungeonExteriorPreferredDistance = 575;
    internal const int DungeonExteriorMaximumDistance = 825;
    private static readonly ConcurrentDictionary<GamePlayer, TravelState> Travels = new();
    private static System.Threading.Timer _timer;
    private static int _polling;
    private static DbZonePoint[] _crossings;

    private sealed class TravelState
    {
        public required GamePlayer Player;
        public required Destination Final;
        public string Status = "Planning route";
        public long NextStatusTick;
        public long StableAttemptUntil;
        public string StablePathId = string.Empty;
        public int TickQueued;
        public readonly PlayerTravelPath Path = new();
        public volatile bool Paused;
        public bool WasRiding;
        public long NextStableSearch;
        public StableChoice? Stable;
        public Destination StableWaypoint;
        public readonly HashSet<string> FailedTickets = new(StringComparer.Ordinal);
        public DbZonePoint Crossing;
        public ushort CrossingFrom;
        public long TransferUntil;
        public IPlayerTravelInput Input;
        public bool RunSpeedSynchronized;
    }

    private readonly record struct Destination(ushort RegionId, int X, int Y, int Z, string Label, bool IsDungeonEntrance);
    private readonly record struct StableChoice(GameStableMaster Master, int Slot, DbItemTemplate Ticket, string PathId, double SecondsSaved);

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(Poll, null, TickMilliseconds, TickMilliseconds);
        _crossings = null;
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        _timer?.Dispose();
        _timer = null;
        foreach (var state in Travels.Values) state.Input?.Dispose();
        Travels.Clear();
    }

    public static bool IsActive(GamePlayer player) => player != null && Travels.ContainsKey(player);

    public static bool SuppressClientMovement(GamePlayer player)
    {
        // /travel steers normal CLIENT running. Accept its real positions,
        // animation/action flags and collision response; never spoof a receipt.
        return false;
    }

    public static void Stop(GamePlayer player, string reason, bool notify = true)
    {
        if (player == null || !Travels.TryRemove(player, out var stopped))
            return;
        stopped.Input?.Dispose();

        // Cancelling navigation must not dismount or interfere with a native taxi.
        bool riding = player.Steed != null || player.IsOnHorse;
        if (!riding) player.CurrentSpeed = 0;
        if (player.ObjectState is GameObject.eObjectState.Active)
        {
            if (notify)
                player.Out.SendMessage(reason, eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }

    public static IReadOnlyList<(string Name, byte Level)> GetAvailableMonsterNames(GamePlayer player, byte level)
    {
        var regions = new Dictionary<ushort, bool>();
        return ExactLevelNames(EligibleObjects(player, monstersOnly: true)
            .Where(npc => npc.Level == level && PathfindingProvider.Instance.HasNavmesh(npc.CurrentZone))
            .Where(npc =>
            {
                if (!regions.TryGetValue(npc.CurrentRegionID, out bool accessible))
                {
                    accessible = npc.CurrentRegionID == player.CurrentRegionID || FindNextCrossing(player, npc.CurrentRegionID) != null;
                    regions[npc.CurrentRegionID] = accessible;
                }
                return accessible;
            }).Select(npc => (npc.Name, npc.Level)), level);
    }

    public static IReadOnlyList<(string Name, byte Level)> ExactLevelNames(IEnumerable<(string Name, byte Level)> entries, byte level) =>
        entries.Where(entry => entry.Level == level && !string.IsNullOrWhiteSpace(entry.Name))
            .DistinctBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool TryStart(GamePlayer player, string exactName, out string message)
    {
        if (AutonomousPlayerPilot.IsActive(player))
        {
            message = "Turn /bot mode off before starting /travel; the two controllers are intentionally separate.";
            return false;
        }

        if (!TryResolveDestination(player, exactName, false, out Destination destination, out message)) return false;
        return StartJourney(player, destination, out message);
    }

    public static bool TryTeleportToMonster(GamePlayer player, string exactName, out string message)
    {
        if (!player.IsAlive || player.Steed != null || player.IsOnHorse || AutonomousPlayerPilot.IsActive(player))
        {
            message = "You must be alive and off a horse route before using /tele mob.";
            return false;
        }
        if (!TryResolveDestination(player, exactName, true, out Destination destination, out message)) return false;
        Stop(player, string.Empty, false);
        if (PlayerCompanionGrind.IsActive(player)) PlayerCompanionGrind.Stop(player, "you used /tele mob");
        if (!player.MoveTo(destination.RegionId, destination.X, destination.Y, destination.Z, player.Heading))
        {
            message = $"Could not teleport to {destination.Label}.";
            return false;
        }
        message = $"Teleported to {destination.Label}{(destination.IsDungeonEntrance ? " (outside the dungeon)" : "")}.";
        return true;
    }

    private static bool TryResolveDestination(GamePlayer player, string exactName, bool monstersOnly,
        out Destination destination, out string message)
    {
        destination = default;
        message = string.Empty;
        GameNPC[] available = EligibleObjects(player, monstersOnly).ToArray();
        GameNPC[] matches = available
            .Where(npc => npc.Name.Equals(exactName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            Destination? place = monstersOnly ? null : FindPlace(player, exactName, available);
            if (!place.HasValue)
            {
                message = $"No accessible Classic/SI {(monstersOnly ? "monster" : "mob, NPC, town or capital")} matches '{exactName}'. Use /mobs <level> for monster names.";
                return false;
            }
            destination = place.Value;
            return true;
        }

        GameNPC[] chosenCamp = matches
            .GroupBy(npc => (npc.CurrentRegionID, X: npc.X / CampCellSize, Y: npc.Y / CampCellSize))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => RouteBias(player, group.First()))
            .First()
            .ToArray();
        int x = (int)Math.Round(chosenCamp.Average(npc => npc.X));
        int y = (int)Math.Round(chosenCamp.Average(npc => npc.Y));
        // An average of several floors/spawn points can land inside a wall.
        // Use a real spawn nearest the camp center instead.
        GameNPC representative = chosenCamp.OrderBy(npc => DistanceSquared(npc.X, npc.Y, x, y)).First();
        x = representative.X; y = representative.Y; int z = representative.Z;
        destination = new(representative.CurrentRegionID, x, y, z, $"{representative.Name} camp", false);

        if (!representative.CurrentRegion.IsCapitalCity &&
            (representative.CurrentZone?.IsDungeon == true || representative.CurrentRegion?.IsDungeon == true))
        {
            DbZonePoint entrance = FindDungeonEntrance(player, representative.CurrentRegionID, x, y);
            if (entrance == null)
            {
                message = $"{representative.Name} is inside a dungeon, but this world database has no reachable exterior entrance for it.";
                return false;
            }

            if (!TryResolveDungeonExteriorApproach(entrance, out Vector3 approach))
            {
                message = $"{representative.Name} is inside a dungeon, but no safe overworld position could be found outside its entrance trigger.";
                return false;
            }

            destination = new(
                entrance.SourceRegion,
                (int)Math.Round(approach.X),
                (int)Math.Round(approach.Y),
                (int)Math.Round(approach.Z),
                $"entrance for {representative.Name}",
                true);
        }

        return true;
    }

    private static bool StartJourney(GamePlayer player, Destination destination, out string message)
    {
        Stop(player, string.Empty, false);
        if (!LocalPlayerTravelInput.TryCreate(player, out var input))
        {
            message = "/travel needs the local DAoC game window focused and an unmodified forward key. No movement was started.";
            return false;
        }
        if (PlayerCompanionGrind.IsActive(player)) PlayerCompanionGrind.Stop(player, "you started /travel");
        var state = new TravelState { Player = player, Final = destination, Input = input };
        Travels[player] = state;
        QueueTick(state);
        message = $"Running to {destination.Label}{(destination.IsDungeonEntrance ? " (stopping outside the dungeon)" : "")}. Manual keys/chat or alt-tab cancel travel; /travel stop also cancels.";
        return true;
    }

    public static string NormalizePlaceName(string value)
    {
        string trimmed = (value ?? "").Trim().ToLowerInvariant();
        foreach (string suffix in new[] { " village", " town", " city" })
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal)) trimmed = trimmed[..^suffix.Length];
        string normalized = new(trimmed.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).ToArray());
        return normalized == "tnn" ? "tirnanog" : normalized;
    }

    private static Destination? FindPlace(GamePlayer player, string name, IEnumerable<GameNPC> available)
    {
        string key = NormalizePlaceName(name);
        if (key.Length == 0) return null;
        // Named installed town areas plus a real friendly service NPC provide
        // coordinates; no invented map centers or bot town-goal state.
        GameNPC anchor = available.Where(npc => npc is GameMerchant or GameTrainer or GameStableMaster)
            .Where(npc => npc.Realm == eRealm.None || npc.Realm == player.Realm)
            .Where(npc => npc.CurrentRegion.IsCapitalCity
                ? NormalizePlaceName(npc.CurrentRegion.Description) == key
                : npc.CurrentAreas?.OfType<AbstractArea>().Any(area =>
                    (area.IsSafeArea || area is Area.BindArea || area is Area.Circle { Radius: >= 500 and <= 6000 }) &&
                    NormalizePlaceName(area.Description) == key) == true)
            .OrderBy(npc => npc is GameStableMaster ? 0 : npc is GameMerchant ? 1 : 2)
            .ThenBy(npc => RouteBias(player, npc)).FirstOrDefault();
        return anchor == null ? null : new Destination(anchor.CurrentRegionID, anchor.X, anchor.Y, anchor.Z, name.Trim(), false);
    }

    public static bool IsNearbyPartyCombatant(GameLiving player, GameLiving member, int radius = 2000)
    {
        if (player == null || member == null || !member.IsAlive || member.CurrentRegionID != player.CurrentRegionID ||
            member != player && (player.Group == null || member.Group != player.Group ||
                !player.Group.IsInTheGroup(member) || !player.IsWithinRadius(member, radius))) return false;
        return member.InCombat || member.IsAttacking || member is GameBot { Brain: BotBrain { HasAggro: true } } ||
            member.ControlledBrain?.Body is { IsAlive: true } pet && (pet.InCombat || pet.IsAttacking);
    }

    public static bool ShouldPauseForParty(GamePlayer player) => player.IsCasting || player.IsCrowdControlled ||
        IsNearbyPartyCombatant(player, player) || player.Group?.GetMembersInTheGroup().Any(member => IsNearbyPartyCombatant(player, member)) == true;

    public static bool IsWaitingForRegionLoad(GameClient.eClientState? clientState, bool active, long now, long deadline) =>
        deadline > now && (!active || clientState != GameClient.eClientState.Playing) &&
        clientState is GameClient.eClientState.Playing or GameClient.eClientState.WorldEnter;

    private static IEnumerable<GameNPC> EligibleObjects(GamePlayer player, bool monstersOnly)
    {
        foreach (Region region in WorldMgr.GetAllRegions())
        {
            if (region == null || !AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(region.Expansion))
                continue;

            foreach (GameNPC npc in region.Objects.OfType<GameNPC>())
            {
                if (npc?.ObjectState is not GameObject.eObjectState.Active || string.IsNullOrWhiteSpace(npc.Name) ||
                    npc is GameBot or GameSummonedPet or GameTaxi ||
                    npc.CurrentZone == null || !IsZoneAccessible(player.Realm, npc.CurrentZone))
                    continue;

                if (monstersOnly && !IsExperienceMonster(npc))
                    continue;

                yield return npc;
            }
        }
    }

    public static bool IsExperienceMonster(GameNPC npc) => npc != null &&
        npc.Realm == eRealm.None && npc.Level > 0 && npc.Brain != null &&
        (npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) == 0 &&
        npc is not GameMerchant && npc is not GameGuard && npc is not GameTaxi &&
        npc is not GameBot && npc is not GameSummonedPet && npc is not GameKeepGuard && npc is not GameTrainer &&
        npc is not GameTeleporter && npc is not GameHealer && npc is not CraftNPC;

    private static bool IsZoneAccessible(eRealm realm, Zone zone)
    {
        if (zone?.ZoneRegion == null || zone.ZoneRegion.IsDisabled ||
            zone.ZoneRegion.ID == 249 && !DFEnterJumpPoint.CanRealmEnter(realm) ||
            !AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(zone.ZoneRegion.Expansion))
            return false;
        eRealm owner = ProtectedRealm(zone.ZoneRegion.ID, zone.ID);
        return owner == eRealm.None || owner == realm;
    }

    // The imported 1.65 zone rows do not populate Zones.Realm, so relying on
    // that column would leak all three protected PvE homelands into /mobs.
    // Old-frontier zones embedded in regions 1/100/200 are deliberately
    // realm-neutral and therefore remain available as RvR destinations.
    private static eRealm ProtectedRealm(ushort regionId, ushort zoneId)
    {
        if (regionId == 1)
            return zoneId is 11 or 12 or 14 or 15 ? eRealm.None : eRealm.Albion;
        if (regionId == 100)
            return zoneId is 111 or 112 or 113 or 115 ? eRealm.None : eRealm.Midgard;
        if (regionId == 200)
            return zoneId is 210 or 211 or 212 or 214 ? eRealm.None : eRealm.Hibernia;

        if (regionId is 10 or 20 or 21 or 22 or 23 or 24 or 50 or 51 or 60 or 61 or 62)
            return eRealm.Albion;
        if (regionId is 101 or 125 or 126 or 127 or 128 or 129 or 150 or 151 or 160 or 161)
            return eRealm.Midgard;
        if (regionId is 180 or 181 or 190 or 191 or 192 or 193 or 194 or 201 or 220 or 221 or 222 or 223 or 224)
            return eRealm.Hibernia;
        return eRealm.None;
    }

    private static double RouteBias(GamePlayer player, GameNPC npc)
    {
        if (player.CurrentRegionID != npc.CurrentRegionID)
            return 1_000_000_000d;
        long dx = (long)player.X - npc.X;
        long dy = (long)player.Y - npc.Y;
        return dx * dx + dy * dy;
    }

    private static DbZonePoint FindDungeonEntrance(GamePlayer player, ushort dungeonRegion, int targetX, int targetY)
    {
        return DOLDB<DbZonePoint>.SelectObjects(DB.Column("TargetRegion").IsEqualTo(dungeonRegion))
            .Where(point => point.SourceRegion != 0 && (point.Realm == 0 || point.Realm == (ushort)player.Realm))
            .Where(point => IsExteriorPointAccessible(player.Realm, point.SourceRegion, point.SourceX, point.SourceY))
            .OrderBy(point => DistanceSquared(point.TargetX, point.TargetY, targetX, targetY))
            .FirstOrDefault();
    }

    private static bool IsExteriorPointAccessible(eRealm realm, ushort regionId, int x, int y)
    {
        Region region = WorldMgr.GetRegion(regionId);
        Zone zone = region?.GetZone(x, y);
        return region != null && !region.IsDungeon && zone?.IsDungeon != true && IsZoneAccessible(realm, zone);
    }

    private static bool TryResolveDungeonExteriorApproach(DbZonePoint entrance, out Vector3 approach)
    {
        approach = default;
        Region region = WorldMgr.GetRegion(entrance.SourceRegion);
        Zone zone = region?.GetZone(entrance.SourceX, entrance.SourceY);
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (region == null || region.IsDungeon || zone == null || zone.IsDungeon ||
            !nav.IsAvailable || !nav.HasNavmesh(zone))
            return false;

        DbZonePoint[] nearbyTriggers = DOLDB<DbZonePoint>.SelectObjects(
                DB.Column("SourceRegion").IsEqualTo(entrance.SourceRegion))
            .Where(point => point.SourceX != 0 || point.SourceY != 0)
            .ToArray();
        return TryResolveDungeonExteriorApproach(nav, region, zone, entrance, nearbyTriggers, out approach);
    }

    public static bool TryResolveDungeonExteriorApproach(IPathfindingMgr nav, Region region, Zone zone,
        DbZonePoint entrance, IReadOnlyCollection<DbZonePoint> nearbyTriggers, out Vector3 approach)
    {
        approach = default;
        if (nav == null || region == null || zone == null || entrance == null ||
            region.IsDungeon || zone.IsDungeon || !nav.IsAvailable || !nav.HasNavmesh(zone))
            return false;

        Vector3 trigger = new(entrance.SourceX, entrance.SourceY, entrance.SourceZ);
        Vector3? triggerFloor = nav.GetClosestPoint(zone, trigger, 96, 96, 192, nav.DefaultFilters);
        Vector3? best = null;
        double bestScore = double.MaxValue;
        int[] radii = [DungeonExteriorPreferredDistance, 700, DungeonExteriorMaximumDistance, DungeonExteriorMinimumDistance];
        foreach (int radius in radii)
        {
            for (int sector = 0; sector < 24; sector++)
            {
                double angle = sector * Math.PI * 2 / 24;
                Vector3 desired = trigger + new Vector3(
                    (float)(Math.Cos(angle) * radius),
                    (float)(Math.Sin(angle) * radius), 0);
                if (desired.X < zone.XOffset || desired.X >= zone.XOffset + zone.Width ||
                    desired.Y < zone.YOffset || desired.Y >= zone.YOffset + zone.Height)
                    continue;

                Vector3? grounded = nav.GetClosestPoint(zone, desired, 96, 96, 256, nav.DefaultFilters);
                if (!grounded.HasValue || region.GetZone((int)grounded.Value.X, (int)grounded.Value.Y) != zone ||
                    !IsSafeDungeonExteriorDistance(entrance.SourceX, entrance.SourceY,
                        (int)grounded.Value.X, (int)grounded.Value.Y) ||
                    nearbyTriggers.Any(point => DistanceSquared(point.SourceX, point.SourceY,
                        (int)grounded.Value.X, (int)grounded.Value.Y) <
                        (long)DungeonExteriorMinimumDistance * DungeonExteriorMinimumDistance))
                    continue;

                // Prefer a point connected directly to the entrance polygon. Some
                // original dungeon triggers (Stonehenge Barrows included) sit just
                // beyond the exterior mesh edge, so the trigger itself cannot be a
                // valid corridor endpoint. In that case a nearby point on the normal
                // outdoor network is still a safe teleport landing: the command is
                // deliberately placing the player outside, not walking them through
                // the trigger.
                bool connectedToTrigger = triggerFloor.HasValue &&
                    AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, triggerFloor.Value, grounded.Value) &&
                    AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, grounded.Value, triggerFloor.Value);
                if (!connectedToTrigger &&
                    !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, grounded.Value))
                    continue;

                double distance = Math.Sqrt(DistanceSquared(entrance.SourceX, entrance.SourceY,
                    (int)grounded.Value.X, (int)grounded.Value.Y));
                double score = Math.Abs(distance - DungeonExteriorPreferredDistance) +
                               Math.Abs(grounded.Value.Z - (triggerFloor?.Z ?? entrance.SourceZ)) * 0.25 +
                               (connectedToTrigger ? 0 : 1000);
                if (score >= bestScore)
                    continue;
                best = grounded;
                bestScore = score;
            }
        }

        if (!best.HasValue)
            return false;
        approach = best.Value;
        return true;
    }

    public static bool IsSafeDungeonExteriorDistance(int triggerX, int triggerY, int x, int y)
    {
        long distanceSquared = DistanceSquared(triggerX, triggerY, x, y);
        return distanceSquared >= (long)DungeonExteriorMinimumDistance * DungeonExteriorMinimumDistance &&
               distanceSquared <= (long)DungeonExteriorMaximumDistance * DungeonExteriorMaximumDistance;
    }

    private static void Poll(object state)
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
            return;
        try
        {
            foreach (TravelState travel in Travels.Values)
                QueueTick(travel);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static void QueueTick(TravelState state)
    {
        if (Interlocked.Exchange(ref state.TickQueued, 1) != 0)
            return;
        NpcService.Instance.Post(TickOnGameLoop, state);
    }

    private static void TickOnGameLoop(TravelState state)
    {
        try
        {
            GamePlayer player = state.Player;
            if (!Travels.TryGetValue(player, out TravelState current) || current != state)
                return;
            if (state.Input?.Cancelled != false)
            {
                Stop(player, "/travel stopped; manual control restored.");
                return;
            }
            // MoveTo removes the player until the client finishes loading the next
            // region. Preserve this journey without moving an inactive character.
            if (IsWaitingForRegionLoad(player.Client?.ClientState,
                player.ObjectState == GameObject.eObjectState.Active, GameLoop.GameLoopTime, state.TransferUntil))
            {
                StopMovement(player);
                return;
            }
            if (player.Client?.ClientState is not GameClient.eClientState.Playing || player.ObjectState is not GameObject.eObjectState.Active)
            {
                Stop(player, string.Empty, false);
                return;
            }
            state.TransferUntil = 0;
            if (AutonomousPlayerPilot.IsActive(player))
            {
                Stop(player, "/travel stopped because /bot mode was enabled.");
                return;
            }
            if (!player.IsAlive)
            {
                Stop(player, "/travel cancelled because you died.");
                return;
            }
            if (player.Steed != null || player.IsOnHorse)
            {
                StopMovement(player); // No keyboard movement during a native horse route.
                state.WasRiding = true;
                state.StableAttemptUntil = 0;
                state.Stable = null;
                state.Path.Clear();
                state.Status = $"Riding a stable route toward {state.Final.Label}";
                ShowStatus(state);
                return;
            }
            if (state.WasRiding)
            {
                state.WasRiding = false; state.NextStableSearch = 0;
                state.Path.Clear();
            }
            if (ShouldPauseForParty(player))
            {
                StopMovement(player);
                state.Paused = true;
                state.Path.Clear();
                state.Status = "Paused while you or your nearby party are in combat/casting";
                ShowStatus(state);
                return;
            }
            state.Paused = false;
            if (player.IsSitting) player.Sit(false);

            Destination waypoint = state.Final;
            DbZonePoint crossing = null;
            if (player.CurrentRegionID != state.Final.RegionId)
            {
                if (state.Crossing == null || state.CrossingFrom != player.CurrentRegionID)
                {
                    state.Crossing = FindNextCrossing(player, state.Final.RegionId);
                    state.CrossingFrom = player.CurrentRegionID;
                }
                crossing = state.Crossing;
                if (crossing == null)
                {
                    Stop(player, $"No legitimate Classic/SI route from here to {state.Final.Label} was found.");
                    return;
                }
                waypoint = new(crossing.SourceRegion, crossing.SourceX, crossing.SourceY, crossing.SourceZ, "zone connection", false);
            }

            if (player.CurrentRegionID != waypoint.RegionId)
            {
                Stop(player, $"The route to {state.Final.Label} became invalid after a region change.");
                return;
            }

            int distance = Distance(player.X, player.Y, waypoint.X, waypoint.Y);
            if (crossing != null && distance <= 175 && Math.Abs(player.Z - crossing.SourceZ) <= 192)
            {
                StopMovement(player);
                if (!TryCross(player, crossing))
                {
                    Stop(player, "That zone connection is not currently open to your character.");
                    return;
                }
                state.Path.Clear(); state.Crossing = null; state.Stable = null; state.NextStableSearch = 0;
                state.TransferUntil = GameLoop.GameLoopTime + 120_000;
                state.Status = $"Crossing toward {state.Final.Label}";
                ShowStatus(state, true);
                return;
            }

            if (crossing == null && distance <= ArrivalRadius && Math.Abs(player.Z - waypoint.Z) <= 192)
            {
                StopMovement(player);
                Stop(player, state.Final.IsDungeonEntrance
                    ? $"Arrived outside the dungeon entrance for {state.Final.Label.Replace("entrance for ", string.Empty)}."
                    : $"Arrived near {state.Final.Label}.");
                return;
            }

            if (TryUseFasterStableRoute(state, waypoint))
            {
                ShowStatus(state);
                return;
            }

            state.Status = $"Running toward {state.Final.Label}";
            StepToward(state, waypoint);
            ShowStatus(state);
        }
        catch (Exception exception)
        {
            Stop(state.Player, $"The /travel route stopped safely: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref state.TickQueued, 0);
        }
    }

    private static DbZonePoint FindNextCrossing(GamePlayer player, ushort targetRegion)
    {
        DbZonePoint[] points = (_crossings ??= DOLDB<DbZonePoint>.SelectAllObjects().ToArray())
            .Where(point => point.SourceRegion != 0 && point.TargetRegion != 0 &&
                            point.SourceRegion != point.TargetRegion &&
                            (point.TargetRegion != 249 || DFEnterJumpPoint.CanRealmEnter(player.Realm)) &&
                            (point.Realm == 0 || point.Realm == (ushort)player.Realm) &&
                            IsRegionPointAccessible(player.Realm, point.SourceRegion, point.SourceX, point.SourceY) &&
                            IsRegionPointAccessible(player.Realm, point.TargetRegion, point.TargetX, point.TargetY))
            .ToArray();
        var previous = new Dictionary<ushort, DbZonePoint>();
        var seen = new HashSet<ushort> { player.CurrentRegionID };
        var queue = new Queue<ushort>();
        queue.Enqueue(player.CurrentRegionID);
        while (queue.Count > 0)
        {
            ushort region = queue.Dequeue();
            foreach (DbZonePoint edge in points.Where(point => point.SourceRegion == region)
                         .OrderBy(point => region == player.CurrentRegionID ? DistanceSquared(player.X, player.Y, point.SourceX, point.SourceY) : 0))
            {
                if (!seen.Add(edge.TargetRegion))
                    continue;
                previous[edge.TargetRegion] = edge;
                if (edge.TargetRegion == targetRegion)
                {
                    DbZonePoint first = edge;
                    while (first.SourceRegion != player.CurrentRegionID && previous.TryGetValue(first.SourceRegion, out DbZonePoint prior))
                        first = prior;
                    return first;
                }
                queue.Enqueue(edge.TargetRegion);
            }
        }
        return null;
    }

    private static bool IsRegionPointAccessible(eRealm realm, ushort regionId, int x, int y)
    {
        Region region = WorldMgr.GetRegion(regionId);
        if (region == null || !AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(region.Expansion))
            return false;
        Zone zone = region.GetZone(x, y);
        return IsZoneAccessible(realm, zone);
    }

    private static bool TryCross(GamePlayer player, DbZonePoint source)
    {
        // Handler may adjust a destination. Never mutate a cached/database edge.
        var edge = new DbZonePoint { Id = source.Id, SourceRegion = source.SourceRegion,
            SourceX = source.SourceX, SourceY = source.SourceY, SourceZ = source.SourceZ,
            TargetRegion = source.TargetRegion, TargetX = source.TargetX, TargetY = source.TargetY,
            TargetZ = source.TargetZ, TargetHeading = source.TargetHeading, Realm = source.Realm, ClassType = source.ClassType };
        if (!player.CurrentRegion.OnZonePoint(player, edge)) return false;
        if (!string.IsNullOrWhiteSpace(edge.ClassType))
        {
            Type type = ScriptMgr.GetType(edge.ClassType);
            if (type == null || !typeof(IJumpPointHandler).IsAssignableFrom(type) ||
                !((IJumpPointHandler)Activator.CreateInstance(type)).IsAllowedToJump(edge, player)) return false;
        }
        Region destination = WorldMgr.GetRegion(edge.TargetRegion);
        if (destination == null || destination.IsDisabled || !IsRegionPointAccessible(player.Realm, edge.TargetRegion, edge.TargetX, edge.TargetY)) return false;
        return player.MoveTo(edge.TargetRegion, edge.TargetX, edge.TargetY, edge.TargetZ, edge.TargetHeading);
    }

    private static bool TryUseFasterStableRoute(TravelState state, Destination waypoint)
    {
        GamePlayer player = state.Player;
        if (state.StableAttemptUntil > 0)
        {
            if (GameLoop.GameLoopTime < state.StableAttemptUntil)
            {
                StopMovement(player);
                state.Status = "Waiting for the stable horse to board";
                return true; // Native boarding is asynchronous. Do not walk away.
            }
            state.FailedTickets.Add(state.StablePathId);
            state.StableAttemptUntil = 0; state.Stable = null; state.Path.Clear();
        }
        if (player.CurrentRegion == null) return false;
        if (state.StableWaypoint != waypoint)
        {
            state.Stable = null; state.NextStableSearch = 0; state.StableWaypoint = waypoint;
        }
        if (state.Stable == null && GameLoop.GameLoopTime >= state.NextStableSearch)
        {
            state.NextStableSearch = GameLoop.GameLoopTime + 30_000;
            if (Distance(player.X, player.Y, waypoint.X, waypoint.Y) >= 9000)
                state.Stable = FindStableChoice(player, waypoint, state.FailedTickets);
        }
        if (!state.Stable.HasValue) return false;
        StableChoice stable = state.Stable.Value;
        if (stable.Master.ObjectState != GameObject.eObjectState.Active ||
            stable.Master.CurrentRegionID != player.CurrentRegionID || player.GetCurrentMoney() < stable.Ticket.Price ||
            player.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack) == eInventorySlot.Invalid)
        {
            state.Stable = null;
            return false;
        }
        if (!player.IsWithinRadius(stable.Master, 225))
        {
            state.Status = $"Running to {stable.Master.Name} for a faster stable route";
            StepToward(state, new(stable.Master.CurrentRegionID, stable.Master.X, stable.Master.Y, stable.Master.Z, stable.Master.Name, false));
            return true;
        }

        StopMovement(player);
        state.StableAttemptUntil = GameLoop.GameLoopTime + 8000;
        state.StablePathId = stable.PathId;
        stable.Master.OnPlayerBuy(player, stable.Slot, 1);
        state.Status = $"Buying {stable.Ticket.Name} with your real coin";
        return true;
    }

    private static StableChoice? FindStableChoice(GamePlayer player, Destination waypoint, HashSet<string> failedTickets)
    {
        double directSeconds = Distance(player.X, player.Y, waypoint.X, waypoint.Y) / Math.Max(1d, player.MaxSpeed);
        StableChoice? best = null;
        foreach (GameStableMaster master in player.CurrentRegion.Objects.OfType<GameStableMaster>()
                     .Where(master => master.ObjectState is GameObject.eObjectState.Active && (master.Realm == player.Realm || master.Realm == eRealm.None)))
        {
            if (master.TradeItems == null)
                continue;
            foreach (DictionaryEntry entry in master.TradeItems.GetAllItems())
            {
                if (entry.Value is not DbItemTemplate ticket || ticket.Price < 0 || ticket.Price > player.GetCurrentMoney() ||
                    failedTickets.Contains(ticket.Id_nb))
                    continue;
                PathPoint path = MovementMgr.LoadPath(ticket.Id_nb);
                if (path == null || Distance(master.X, master.Y, path.X, path.Y) > 500)
                    continue;
                double routeLength = 0;
                PathPoint endpoint = path;
                int guard = 0;
                while (endpoint.Next != null && guard++ < 5000)
                {
                    routeLength += Distance(endpoint.X, endpoint.Y, endpoint.Next.X, endpoint.Next.Y);
                    endpoint = endpoint.Next;
                }
                if (endpoint.Next != null || routeLength <= 0) continue;
                double estimated = Distance(player.X, player.Y, master.X, master.Y) / Math.Max(1d, player.MaxSpeed) +
                                   routeLength / 1500d + Distance(endpoint.X, endpoint.Y, waypoint.X, waypoint.Y) / Math.Max(1d, player.MaxSpeed);
                double saved = directSeconds - estimated;
                if (saved < directSeconds * 0.15 || best.HasValue && saved <= best.Value.SecondsSaved)
                    continue;
                best = new StableChoice(master, Convert.ToInt32(entry.Key), ticket, ticket.Id_nb, saved);
            }
        }
        return best;
    }

    private static void StepToward(TravelState state, Destination destination)
    {
        GamePlayer player = state.Player;
        Vector3 current = new(player.X, player.Y, player.Z);
        Vector3 target = new(destination.X, destination.Y, destination.Z);
        // Look ahead on this private nav corridor, but let the client's normal
        // running/collision determine the actual next position and ground Z.
        float step = Math.Max(0, player.MaxSpeed * TickMilliseconds / 1000f);
        if (step < 1) { StopMovement(player); return; }
        if (!state.Path.TryStep(player.CurrentRegion, player.CurrentZone, current, target, step,
            GameLoop.GameLoopTime, PathfindingProvider.Instance, out Vector3 next, out string failure))
        {
            Stop(player, $"/travel: {failure}");
            return;
        }
        if (state.Path.DoorNode is Vector3 doorPoint && Vector3.Distance(current, doorPoint) <= 240)
        {
            var doors = new List<GameDoorBase>();
            player.CurrentRegion.GetInRadius(new Point3D((int)doorPoint.X, (int)doorPoint.Y, (int)doorPoint.Z), eGameObjectType.DOOR, 128, doors);
            foreach (var door in doors.Where(door => door.State == eDoorState.Closed))
            {
                if (!door.CanBeOpenedViaInteraction || door.Realm != eRealm.None && door.Realm != player.Realm)
                {
                    Stop(player, "/travel stopped at a closed door you cannot open.");
                    return;
                }
                door.Open(player);
            }
        }
        Zone zone = player.CurrentRegion?.GetZone((int)next.X, (int)next.Y);
        if (zone == null)
        {
            Stop(player, "/travel stopped before leaving the mapped world.");
            return;
        }
        bool run = Vector3.DistanceSquared(current, next) >= 1;
        if (!run) { StopMovement(player); return; }
        if (!state.RunSpeedSynchronized)
        {
            // Refresh the real allowed run speed once per on-foot leg. Never
            // grant sprint, override slows/encumbrance, or fabricate velocity.
            player.Out.SendUpdateMaxSpeed();
            state.RunSpeedSynchronized = true;
        }
        ushort heading = player.GetHeading((int)next.X, (int)next.Y);
        if (NeedsTravelHeadingUpdate(player.Heading, heading))
        {
            player.Heading = heading;
            // Face only at a real turn, not every 250ms on straight roads.
            // Leave the held forward key and native run animation uninterrupted.
            player.Out.SendPlayerJump(true);
        }
        if (!state.Input.Steer(true))
            Stop(player, "/travel input stopped; manual control restored.");
    }

    public static bool NeedsTravelHeadingUpdate(ushort current, ushort desired)
    {
        int difference = Math.Abs((current & 0xfff) - (desired & 0xfff));
        return Math.Min(difference, 4096 - difference) > 8;
    }

    private static void StopMovement(GamePlayer player)
    {
        if (Travels.TryGetValue(player, out var state))
        {
            state.Input?.Steer(false);
            state.RunSpeedSynchronized = false;
        }
    }

    private static void ShowStatus(TravelState state, bool force = false)
    {
        if (!force && GameLoop.GameLoopTime < state.NextStatusTick)
            return;
        state.NextStatusTick = GameLoop.GameLoopTime + 5000;
        state.Player.Out.SendMessage($"/travel — {state.Status}", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
    }

    private static int Distance(int ax, int ay, int bx, int by) => (int)Math.Sqrt(DistanceSquared(ax, ay, bx, by));

    private static long DistanceSquared(int ax, int ay, int bx, int by)
    {
        long dx = (long)ax - bx;
        long dy = (long)ay - by;
        return dx * dx + dy * dy;
    }
}

[CmdAttribute("&mobs", ePrivLevel.Player, "Lists accessible monsters at an exact level", "/mobs <level> [page]", "/mobs (your current level)")]
public sealed class MobsCommandHandler : AbstractCommandHandler, ICommandHandler
{
    public void OnCommand(GameClient client, string[] args)
    {
        GamePlayer player = client.Player;
        if (!TryReadLevel(args, player.Level, out byte level))
        {
            DisplayMessage(client, "Usage: /mobs <level> [page], levels 1–255. Example: /mobs 1. With no number, uses your current level.");
            return;
        }
        IReadOnlyList<(string Name, byte Level)> entries = PlayerMobNavigator.GetAvailableMonsterNames(player, level);
        DisplayMessage(client, $"{entries.Count} accessible level {level} monster names in {player.Realm} territory, dungeons and shared RvR areas.");
        if (entries.Count == 0)
        {
            DisplayMessage(client, "No matching XP monsters are available in your realm territory or shared RvR areas.");
            return;
        }
        var pages = BuildPages(entries);
        int page = args.Length == 3 ? int.Parse(args[2]) : 1;
        if (page > pages.Count)
        {
            DisplayMessage(client, $"There are {pages.Count} pages. Use /mobs {level} <page>.");
            return;
        }
        client.Out.SendCustomTextWindow($"Level {level} monsters — {page}/{pages.Count}", new[]
            { "Use /tele mob <exact monster name>.", "Dungeon targets place you outside the entrance.", $"Page {page}/{pages.Count}. Use /mobs {level} <page>.", "" }
            .Concat(pages[page - 1]).ToList());
        if (page < pages.Count) DisplayMessage(client, $"More names: /mobs {level} {page + 1} (page {page + 1}/{pages.Count}).");
        DisplayMessage(client, "Use /tele mob <exact name>. Dungeon targets place you outside the entrance.");
    }

    public static bool TryReadLevel(string[] args, byte currentLevel, out byte level)
    {
        level = currentLevel;
        return args.Length == 1 ? level > 0 : args.Length is 2 or 3 && byte.TryParse(args[1], out level) && level > 0 &&
            (args.Length == 2 || int.TryParse(args[2], out int page) && page > 0);
    }

    public static IReadOnlyList<string[]> BuildPages(IEnumerable<(string Name, byte Level)> entries)
    {
        // Native detail windows silently truncate at their packet/200-line
        // limit. Small explicit pages keep every monster name accessible.
        var pages = new List<string[]>(); var lines = new List<string>(); int length = 0;
        foreach (var entry in entries)
        {
            string line = $"{entry.Name} ({entry.Level})";
            if (lines.Count > 0 && (length + line.Length + 2 > 1200 || lines.Count >= 40))
            { pages.Add(lines.ToArray()); lines.Clear(); length = 0; }
            lines.Add(line); length += line.Length + 2;
        }
        if (lines.Count > 0) pages.Add(lines.ToArray());
        return pages;
    }
}

public sealed class TravelCommandHandler : AbstractCommandHandler, ICommandHandler
{
    public void OnCommand(GameClient client, string[] args)
    {
        if (args.Length < 2)
        {
            DisplayMessage(client, "Usage: /travel <mob, NPC, town or capital name>. Use /travel stop to cancel.");
            return;
        }
        string requested = string.Join(' ', args.Skip(1)).Trim();
        if (requested.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (PlayerMobNavigator.IsActive(client.Player))
                PlayerMobNavigator.Stop(client.Player, "/travel cancelled; manual control restored.");
            else
                DisplayMessage(client, "No /travel route is active.");
            return;
        }
        if (PlayerMobNavigator.TryStart(client.Player, requested, out string message))
            DisplayMessage(client, message);
        else
            DisplayMessage(client, message);
    }
}

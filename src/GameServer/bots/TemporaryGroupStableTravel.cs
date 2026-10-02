using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Threading;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.Movement;
using DOL.GS.PacketHandler;

namespace DOL.GS;

/// <summary>
/// Keeps only ephemeral /spawn companions with their real-player leader over
/// authoritative travel.  It never considers persistent bots, normal group
/// members, money, inventory, or ticket ownership.
/// </summary>
public static class TemporaryGroupStableTravel
{
    private const int CompanionBoardingRadius = 650;
    private const int BoardAfterPlayerMountMilliseconds = 550;
    private const int PlayerRouteStartMilliseconds = 4000;
    private const int CompanionStaggerMilliseconds = 125;
    private const int FormationRadius = 180;
    private const int RouteAbortGraceMilliseconds = 5000;
    private static readonly TimeSpan TransferMarkerLifetime = TimeSpan.FromSeconds(30);

    private sealed class RouteMonitor
    {
        public required GamePlayer Player;
        public required GameBot[] Companions;
        public required DateTime ExpectedArrivalUtc;
        public required DateTime StartedUtc;
        public int Queued;
    }

    private static readonly ConcurrentDictionary<GamePlayer, RouteMonitor> ActiveRoutes = new();
    // Zone/region differences occur during ordinary follower movement.  A marker
    // is created only by a successful GamePlayer.MoveTo, then consumed once the
    // player's temporary helpers have joined that completed transfer.
    private static readonly ConcurrentDictionary<GamePlayer, DateTime> PendingPlayerTransfers = new();
    private static Timer _monitorTimer;

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        _monitorTimer?.Dispose();
        _monitorTimer = new Timer(PollRoutes, null, 250, 250);
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        _monitorTimer?.Dispose();
        _monitorTimer = null;
        ActiveRoutes.Clear();
        PendingPlayerTransfers.Clear();
    }

    /// <summary>
    /// Called only after GameStableMaster accepted and consumed the real
    /// player's ticket.  Companion rides cost nothing because helpers have no
    /// persistent purse or inventory; the player's normal transaction remains
    /// the sole authority for the route.
    /// </summary>
    public static void QueueMirroredStableRoute(GamePlayer player, PathPoint route, DbItemTemplate ticket)
    {
        if (player == null || route == null || ticket == null)
            return;

        ECSGameTimer boardAfterPlayer = new(player, _ =>
        {
            StartMirroredStableRoute(player, route, ticket);
            return 0;
        });
        boardAfterPlayer.Start(BoardAfterPlayerMountMilliseconds);
    }

    public static bool IsEligibleForMirroredRide(
        bool isTemporaryHelper,
        bool isAlive,
        bool isActive,
        bool sharesGroup,
        bool followsPlayer,
        int distanceToPlayer) =>
        isTemporaryHelper && isAlive && isActive && sharesGroup && followsPlayer &&
        distanceToPlayer <= CompanionBoardingRadius;

    public static bool ShouldRelocateForOwnerTransfer(
        bool hasConfirmedPlayerTransfer,
        bool isTemporaryHelper,
        bool sharesGroup,
        bool followsPlayer,
        ushort companionRegion,
        ushort playerRegion,
        ushort companionZone,
        ushort playerZone) =>
        hasConfirmedPlayerTransfer && isTemporaryHelper && sharesGroup && followsPlayer;

    public static void MarkAcceptedPlayerTransfer(GamePlayer player)
    {
        if (player != null)
            PendingPlayerTransfers[player] = DateTime.UtcNow;
    }

    private static void StartMirroredStableRoute(GamePlayer player, PathPoint route, DbItemTemplate ticket)
    {
        // The delayed callback ensures the native stable master mounted the
        // player first.  Cancel cleanly if that boarding was interrupted.
        if (player?.ObjectState is not GameObject.eObjectState.Active || !player.IsAlive ||
            player.Steed == null || !player.IsOnHorse || player.Group == null ||
            !AutonomousStableRoutePlanner.TryMeasureRide(route, out _, out double rideSeconds))
            return;

        DateTime started = DateTime.UtcNow;
        GameBot[] eligible = player.Group.GetMembersInTheGroup()
            .OfType<GameBot>()
            .Where(bot => IsEligibleForMirroredRide(
                bot.IsTemporaryGroupHelper,
                bot.IsAlive,
                bot.ObjectState is GameObject.eObjectState.Active,
                bot.Group == player.Group,
                bot.PlayerGroupLeader == player,
                Distance(bot, player)))
            .ToArray();
        if (eligible.Length == 0)
            return;

        ActiveRoutes.TryRemove(player, out _);
        string destination = string.IsNullOrWhiteSpace(ticket.Name) ? "the next stable" : ticket.Name;
        GameBot[] boarded = eligible
            .Select((bot, index) => new { Bot = bot, Index = index })
            .Where(entry => entry.Bot.BeginStableMasterRoute(
                destination,
                TimeSpan.FromSeconds(rideSeconds),
                CloneRoute(route),
                ticket,
                Math.Max(0, PlayerRouteStartMilliseconds - BoardAfterPlayerMountMilliseconds) + entry.Index * CompanionStaggerMilliseconds))
            .Select(entry => entry.Bot)
            .ToArray();
        if (boarded.Length == 0)
            return;

        ActiveRoutes[player] = new RouteMonitor
        {
            Player = player,
            Companions = boarded,
            StartedUtc = started,
            ExpectedArrivalUtc = started + TimeSpan.FromMilliseconds(PlayerRouteStartMilliseconds - BoardAfterPlayerMountMilliseconds) +
                                 TimeSpan.FromSeconds(rideSeconds) + TimeSpan.FromSeconds(2),
        };
    }

    /// <summary>
    /// Event and failsafe entry point after a real player has completed an
    /// authoritative MoveTo/portal/region transfer.  Existing helper objects
    /// move; none are created, saved, disbanded, or applied to other players.
    /// </summary>
    public static int RelocateHelpersAfterPlayerTransfer(GamePlayer player, bool explicitMoveTo = false)
    {
        if (DragonCombatGeometry.IsDisplacing(player))
            return 0;
        bool hasConfirmedTransfer = explicitMoveTo || HasPendingPlayerTransfer(player);
        if (player?.ObjectState is not GameObject.eObjectState.Active || !player.IsAlive || player.Group == null || player.CurrentZone == null ||
            !hasConfirmedTransfer)
            return 0;

        int relocated = 0;
        int ordinal = 0;
        foreach (GameBot bot in player.Group.GetMembersInTheGroup().OfType<GameBot>())
        {
            if (!bot.IsAlive || bot.ObjectState != GameObject.eObjectState.Active)
                continue; // Dead helpers retain their corpse/recovery timer.
            bool needsRelocation = ShouldRelocateForOwnerTransfer(
                    hasConfirmedTransfer,
                    bot.IsTemporaryGroupHelper,
                    bot.Group == player.Group,
                    bot.PlayerGroupLeader == player,
                    bot.CurrentRegionID,
                    player.CurrentRegionID,
                    bot.CurrentZone?.ID ?? 0,
                    player.CurrentZone.ID);
            if (!needsRelocation || !bot.IsTemporaryGroupHelper || bot.Group != player.Group || bot.PlayerGroupLeader != player)
                continue;

            bot.CompleteStableMasterRoute();
            // A portal or /release ends the old encounter for helpers. Do not
            // carry a queued pull, cast or walking request to the new location.
            bot.StopCurrentSpellcast();
            (bot.Brain as BotBrain)?.PrepareForTankPull();
            bot.StopMoving();
            Vector3 destination = FormationPoint(player, ordinal++);
            if (!bot.MoveTo(player.CurrentRegionID, (int)Math.Round(destination.X), (int)Math.Round(destination.Y),
                    (int)Math.Round(destination.Z), player.Heading))
                continue;

            bot.EnterPlayerLedGroup(player);
            bot.Follow(player, BotManager.FOLLOW_DISTANCE, BotManager.MAX_FOLLOW_DISTANCE);
            if (bot.Brain is BotBrain brain)
                brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);
            relocated++;
        }
        PendingPlayerTransfers.TryRemove(player, out _);
        return relocated;
    }

    /// <summary>
    /// The bot-brain fallback covers an unusual missed transfer event without
    /// changing ordinary same-zone following.
    /// </summary>
    public static bool EnsureOwnerTransferCohesion(GameBot bot)
    {
        if (bot is not { IsTemporaryGroupHelper: true, IsOnStableMasterRoute: false } || bot.Owner == null ||
            !HasPendingPlayerTransfer(bot.Owner))
            return false;

        GamePlayer player = bot.Owner;
        if (!ShouldRelocateForOwnerTransfer(
                true,
                true,
                bot.Group == player.Group,
                bot.PlayerGroupLeader == player,
                bot.CurrentRegionID,
                player.CurrentRegionID,
                bot.CurrentZone?.ID ?? 0,
                player.CurrentZone?.ID ?? 0))
            return false;

        return RelocateHelpersAfterPlayerTransfer(player) > 0;
    }

    private static bool HasPendingPlayerTransfer(GamePlayer player)
    {
        if (player == null || !PendingPlayerTransfers.TryGetValue(player, out DateTime markedUtc))
            return false;
        if (DateTime.UtcNow - markedUtc <= TransferMarkerLifetime)
            return true;
        PendingPlayerTransfers.TryRemove(player, out _);
        return false;
    }

    // NpcMovementComponent toggles PathPoint.FiredFlag as it progresses.  Give
    // every companion an independent Once-path chain so staggered riders cannot
    // mutate the native rider's route state or each other's route state.
    private static PathPoint CloneRoute(PathPoint route)
    {
        PathPoint first = null;
        PathPoint previous = null;
        for (PathPoint current = route; current != null; current = current.Next)
        {
            var copy = new PathPoint(current.X, current.Y, current.Z, current.MaxSpeed, current.Type)
            {
                WaitTime = current.WaitTime,
            };
            first ??= copy;
            copy.Prev = previous;
            if (previous != null)
                previous.Next = copy;
            previous = copy;
        }
        return first;
    }

    private static void PollRoutes(object state)
    {
        foreach (RouteMonitor monitor in ActiveRoutes.Values)
        {
            if (Interlocked.Exchange(ref monitor.Queued, 1) != 0)
                continue;
            NpcService.Instance.Post(MonitorRouteOnGameLoop, monitor);
        }
    }

    private static void MonitorRouteOnGameLoop(RouteMonitor monitor)
    {
        try
        {
            if (!ActiveRoutes.TryGetValue(monitor.Player, out RouteMonitor current) || current != monitor)
                return;

            DateTime now = DateTime.UtcNow;
            bool playerStillRiding = monitor.Player.ObjectState is GameObject.eObjectState.Active &&
                                     monitor.Player.IsAlive && monitor.Player.Steed != null && monitor.Player.IsOnHorse;
            bool routeMayHaveFinished = now >= monitor.ExpectedArrivalUtc;
            bool interrupted = !playerStillRiding && now - monitor.StartedUtc > TimeSpan.FromMilliseconds(RouteAbortGraceMilliseconds) &&
                               !routeMayHaveFinished;
            if (interrupted)
            {
                foreach (GameBot bot in monitor.Companions)
                    bot.CompleteStableMasterRoute();
                ActiveRoutes.TryRemove(monitor.Player, out _);
                return;
            }

            if (routeMayHaveFinished || monitor.Companions.All(bot => !bot.IsOnStableMasterRoute))
                ActiveRoutes.TryRemove(monitor.Player, out _);
        }
        finally
        {
            Volatile.Write(ref monitor.Queued, 0);
        }
    }

    internal static Vector3 FormationPoint(GamePlayer player, int ordinal)
    {
        double angle = player.Heading * Math.PI * 2d / 4096d + ordinal * Math.PI * 2d / 6d;
        double radius = FormationRadius;
        if (player.Group?.IsCompanionRaid == true)
        {
            (angle, radius) = CompanionRaidFormation.Slot(ordinal, player.CurrentRegion?.IsDungeon == true);
        }
        Vector3 current = new(player.X, player.Y, player.Z);
        Vector3 desired = new(
            player.X + (float)(Math.Cos(angle) * radius),
            player.Y + (float)(Math.Sin(angle) * radius),
            player.Z);
        return PathfindingProvider.Instance.GetMoveAlongSurface(
                   player.CurrentZone,
                   current,
                   desired,
                   PathfindingProvider.Instance.DefaultFilters) ?? current;
    }

    private static int Distance(GameObject first, GameObject second)
    {
        long dx = (long)first.X - second.X;
        long dy = (long)first.Y - second.Y;
        long dz = (long)first.Z - second.Z;
        return (int)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using DOL.AI.Brain;
using DOL.Events;
using DOL.GS.PacketHandler;

namespace DOL.GS.Commands
{
    /// <summary>
    /// Opt-in, stationary human-party pull scheduler. It never owns a bot goal,
    /// changes a combat brain, or runs for a persistent playerbot group.
    /// </summary>
    public static class PlayerCompanionGrind
    {
        public const int PullRadius = 1000;
        public const int RegroupRadius = 350;
        public const int ContactTimeout = 35_000;
        // The legacy native client can close an otherwise healthy localhost
        // session after a long period with no player-originated movement. A
        // same-coordinate CharacterJump is the protocol's normal position
        // resynchronization packet; it creates no movement or combat action.
        private const int ClientSessionRefreshInterval = 4 * 60_000;
        private static readonly ConcurrentDictionary<GamePlayer, Session> Sessions = new();
        private static System.Threading.Timer _timer;

        private sealed class Session
        {
            public GamePlayer Player;
            public Group Group;
            public ushort Region;
            public Vector3 Anchor;
            public int Queued;
            public GameNPC Target;
            public long ContactDeadline;
            public long ReadySince;
            public long NextSearch;
            public long NextStatus;
            public long NextClientSessionRefresh;
            public string Detail = "Waiting for the party";
            public readonly HashSet<GameLiving> SeatedByMode = new();
            public readonly Dictionary<GameNPC, long> FailedTargets = new();
        }

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ =>
            {
                foreach (Session session in Sessions.Values)
                    if (Interlocked.Exchange(ref session.Queued, 1) == 0)
                        NpcService.Instance.Post(Tick, session);
            }, null, 1000, 1000);
        }

        [GameServerStoppedEvent]
        public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
        {
            _timer?.Dispose(); _timer = null; Sessions.Clear();
        }

        public static bool IsActive(GamePlayer player) => player != null && Sessions.ContainsKey(player);

        public static bool IsOwnedCompanion(GamePlayer player, Group group, GameLiving member) =>
            player != null && group != null && member is GameBot bot && bot.IsTemporaryGroupHelper && !bot.IsAutonomousWorldBot &&
            bot.Owner == player && bot.Group == group && bot.Realm == player.Realm;

        public static bool IsExclusiveParty(GamePlayer player, Group group, IReadOnlyList<GameLiving> members) =>
            player != null && group != null && player.Group == group && members.Count is >= 2 and <= 8 &&
            members.Count(member => member == player) == 1 &&
            members.All(member => member == player || IsOwnedCompanion(player, group, member));

        public static bool NeedsRecovery(GameLiving member) => member.MaxHealth > 0 && member.Health < member.MaxHealth ||
            member.MaxMana > 0 && member.Mana < member.MaxMana ||
            member.MaxEndurance > 0 && member.Endurance < member.MaxEndurance;

        public static GameBot SelectPuller(IEnumerable<GameBot> companions) => companions
            .Where(bot => bot.IsAlive && !bot.IsCrowdControlled && bot.CharacterClass != null &&
                BotPartyRoles.For((eCharacterClass)bot.CharacterClass.ID) != BotPartyRole.Support)
            .OrderBy(bot => BotPartyRoles.IsTank(bot) ? 0 : 1).FirstOrDefault();

        public static bool IsSuitableLevel(int playerLevel, int monsterLevel, int conLevel, int companionCount) =>
            monsterLevel > 0 && conLevel > -3;

        public static bool TryStart(GamePlayer player, out string message)
        {
            if (player?.Client?.ClientState != GameClient.eClientState.Playing || !player.IsAlive)
            { message = "You must be alive and in the world to use /grind."; return false; }
            if (IsActive(player))
            { message = "Grind mode is already active. Use /grind stop to end it."; return false; }
            if (AutonomousPlayerPilot.IsActive(player) || PlayerMobNavigator.IsActive(player))
            { message = "Stop any active automated movement before starting stationary /grind mode."; return false; }
            if (player.Steed != null || player.IsOnHorse)
            { message = "Finish your ride before using /grind."; return false; }
            GameLiving[] members = player.Group?.GetMembersInTheGroup().ToArray() ?? Array.Empty<GameLiving>();
            if (!IsExclusiveParty(player, player.Group, members))
            { message = "/grind requires a party containing only you and your own temporary /spawn companions."; return false; }
            if (SelectPuller(members.OfType<GameBot>()) == null)
            { message = "You need a living tank or damage-dealing companion. Healers and buffers will not pull."; return false; }
            var session = new Session { Player = player, Group = player.Group, Region = player.CurrentRegionID,
                Anchor = new(player.X, player.Y, player.Z),
                NextClientSessionRefresh = GameLoop.GameLoopTime + ClientSessionRefreshInterval };
            if (!Sessions.TryAdd(player, session))
            { message = "Grind mode is already active."; return false; }
            // Park once, not a movement-suppression loop. Walking manually cancels
            // the mode instead of fighting the client's movement or teleporting it.
            player.CurrentSpeed = 0;
            player.Out.SendPlayerJump(false);
            ShowStatus(session, true);
            message = "Grind mode active: stay here; tank pulls first, or attackers pull if no tank. " +
                "The next pull waits for everyone to return and fully recover HP/power/endurance. /grind stop cancels; moving also stops it.";
            return true;
        }

        public static void Stop(GamePlayer player, string reason, bool notify = true)
        {
            if (player == null || !Sessions.TryRemove(player, out Session session)) return;
            CancelUnengagedPull(session);
            StandModeSeats(session);
            if (notify && player.Client?.ClientState == GameClient.eClientState.Playing)
            {
                player.Out.SendMessage($"GRIND MODE OFF — {reason}", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
                player.Out.SendMessage($"/grind stopped: {reason}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
        }

        private static bool Fighting(GameLiving living) => living.InCombat ||
            living.ControlledBrain?.Body is { IsAlive: true, InCombat: true };

        private static bool CombatIntent(GameLiving living) => Fighting(living) || living.IsAttacking ||
            living is GameBot { Brain: BotBrain { HasAggro: true } } ||
            living.ControlledBrain?.Body is { IsAlive: true, IsAttacking: true };

        private static bool AtCamp(Session session, GameLiving member) => member.IsAlive &&
            member.ObjectState == GameObject.eObjectState.Active && member.CurrentRegionID == session.Region &&
            member.IsWithinRadius(session.Player, RegroupRadius) &&
            member is not GameBot { IsReturningAfterRelease: true } && member is not GameBot { IsOnStableMasterRoute: true };

        private static void Tick(Session session)
        {
            try
            {
                GamePlayer player = session.Player;
                if (!Sessions.TryGetValue(player, out var current) || current != session) return;
                if (player.Client?.ClientState != GameClient.eClientState.Playing || player.ObjectState != GameObject.eObjectState.Active)
                { Stop(player, "left the world", false); return; }
                if (!player.IsAlive) { Stop(player, "you died"); return; }
                if (player.CurrentRegionID != session.Region || player.IsMoving ||
                    Vector3.DistanceSquared(session.Anchor, new(player.X, player.Y, player.Z)) > 64 * 64 ||
                    player.Steed != null || player.IsOnHorse ||
                    AutonomousPlayerPilot.IsActive(player) || PlayerMobNavigator.IsActive(player))
                { Stop(player, "you moved, traveled or enabled another controller"); return; }
                GameLiving[] members = player.Group?.GetMembersInTheGroup().ToArray() ?? Array.Empty<GameLiving>();
                if (player.Group != session.Group || !IsExclusiveParty(player, session.Group, members))
                { Stop(player, "party changed; only your temporary companions are allowed"); return; }
                long now = GameLoop.GameLoopTime;
                if (now >= session.NextClientSessionRefresh)
                {
                    session.NextClientSessionRefresh = now + ClientSessionRefreshInterval;
                    // Also keep the server-side activity timestamp current if a
                    // portable install later enables the ordinary idle policy.
                    player.LastPlayerActivityTime = now;
                    player.Out.SendPlayerJump(false);
                }
                bool actualCombat = members.Any(Fighting);
                if (session.Target != null)
                {
                    if (!session.Target.IsAlive || session.Target.ObjectState != GameObject.eObjectState.Active)
                        session.Target = null;
                    else if (actualCombat)
                        session.ContactDeadline = now + ContactTimeout;
                    else if (now >= session.ContactDeadline)
                    {
                        GameNPC failed = session.Target;
                        CancelUnengagedPull(session);
                        session.FailedTargets[failed] = now + 120_000;
                        session.Target = null; session.NextSearch = now + 3000;
                        session.Detail = $"Skipping unreachable pull: {failed.Name}";
                    }
                }
                if (actualCombat || members.Any(CombatIntent) || session.Target != null)
                {
                    session.ReadySince = 0;
                    if (actualCombat) StandModeSeats(session);
                    string targetName = session.Target == null ? "nearby attackers" : $"{session.Target.Name} ({session.Target.Level})";
                    session.Detail = actualCombat ? $"Party fighting {targetName} — no new pulls" : $"Pulling {targetName}; waiting for contact";
                    return;
                }

                // Native companion heal/buff AI remains in charge. Do not run its
                // think loop here, stop beneficial casts, or refill resources.
                foreach (GameLiving member in members)
                {
                    if (AtCamp(session, member) && !member.IsCasting && !member.IsMoving &&
                        !member.IsCrowdControlled && NeedsRecovery(member) &&
                        (member is not GameBot helper || !helper.IsTemporaryCompanionRestLocked) &&
                        (member is not GameBot restingBot || !BotRestRecovery.BlocksRest(restingBot)) &&
                        (member is GameBot || !member.IsSitting))
                    {
                        session.SeatedByMode.Add(member);
                        if (member == player) player.Sit(true);
                        else ((GameBot)member).BeginTemporaryCompanionRest();
                    }
                }
                if (members.Any(member => !AtCamp(session, member)))
                { session.ReadySince = 0; session.Detail = "Waiting for companions to revive/return to camp"; return; }
                if (members.Any(member => NeedsRecovery(member) || member.IsCasting || member.IsCrowdControlled))
                { session.ReadySince = 0; session.Detail = "Resting/healing/buffing — waiting for full HP, power and endurance"; return; }
                GameBot puller = SelectPuller(members.OfType<GameBot>());
                if (puller == null)
                { session.ReadySince = 0; session.Detail = "Waiting for a tank or attacker (support classes will not pull)"; return; }
                if (session.ReadySince == 0) session.ReadySince = now;
                if (now - session.ReadySince < 2000 || now < session.NextSearch) return;
                session.NextSearch = now + 5000;
                GameNPC target = FindTarget(session, puller, members.Length - 1, now);
                if (target == null)
                { session.Detail = "Ready — no suitable reachable nearby monsters"; return; }
                StandModeSeats(session);
                foreach (GameBot helper in members.OfType<GameBot>()) helper.WakeTemporaryCompanionRest();
                session.Target = target; session.ContactDeadline = now + ContactTimeout; session.ReadySince = 0;
                // This is precisely the existing /pull command's shared combat
                // dispatch, protected by the strict temporary-only party gate.
                session.Detail = PlayerLedPullCoordinator.Begin(player, target);
            }
            catch (Exception exception)
            {
                Stop(session.Player, $"safety stop ({exception.GetType().Name}); manual control retained");
            }
            finally
            {
                if (Sessions.TryGetValue(session.Player, out var current) && current == session) ShowStatus(session);
                Volatile.Write(ref session.Queued, 0);
            }
        }

        private static GameNPC FindTarget(Session session, GameBot puller, int count, long now)
        {
            foreach (GameNPC expired in session.FailedTargets.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                session.FailedTargets.Remove(expired);
            while (session.FailedTargets.Count > 64) session.FailedTargets.Remove(session.FailedTargets.MinBy(pair => pair.Value).Key);
            GamePlayer player = session.Player;
            var nav = PathfindingProvider.Instance;
            if (puller.CurrentZone == null || !nav.HasNavmesh(puller.CurrentZone)) return null;
            // Local search only, at most four corridor checks per five seconds,
            // and only for explicitly active human grind sessions.
            foreach (GameNPC npc in player.GetNPCsInRadius(PullRadius).Where(npc =>
                PlayerMobNavigator.IsExperienceMonster(npc) && npc.IsAlive && npc.ObjectState == GameObject.eObjectState.Active &&
                !npc.InCombat && !npc.IsAttacking && npc.CurrentZone == puller.CurrentZone &&
                !session.FailedTargets.ContainsKey(npc) &&
                IsSuitableLevel(player.Level, npc.Level, player.GetConLevel(npc), count) &&
                GameServer.ServerRules.IsAllowedToAttack(player, npc, true))
                .OrderBy(npc => Math.Abs(npc.Level - player.Level)).ThenBy(npc => player.GetDistanceTo(npc)).Take(4))
            {
                Vector3 start = new(puller.X, puller.Y, puller.Z), end = new(npc.X, npc.Y, npc.Z);
                // Conservative camp pulls: no chasing behind walls or through
                // whole dungeon wings. Existing combat routing handles the pull.
                if (!nav.HasLineOfSight(puller.CurrentZone, start, end, nav.BlockingDoorAvoidanceFilters) ||
                    !PlayerTravelPath.TryBuildCorridor(nav, puller.CurrentZone, start, end, out _))
                { session.FailedTargets[npc] = now + 120_000; continue; }
                return npc;
            }
            return null;
        }

        private static void StandModeSeats(Session session)
        {
            foreach (GameLiving member in session.SeatedByMode)
            {
                if (!member.IsAlive || member.ObjectState != GameObject.eObjectState.Active) continue;
                if (member == session.Player)
                {
                    if (member.IsSitting) session.Player.Sit(false);
                }
                else if (member is GameBot helper && helper.IsTemporaryCompanionRestLocked &&
                         IsOwnedCompanion(session.Player, session.Group, member))
                    helper.WakeTemporaryCompanionRest();
            }
            session.SeatedByMode.Clear();
        }

        private static void CancelUnengagedPull(Session session)
        {
            if (session.Target == null || session.Target.InCombat || session.Group.GetMembersInTheGroup().Any(Fighting)) return;
            // Finish any actual battle normally. Only an unengaged /grind order
            // is revoked; do not clear defensive aggro or other players' orders.
            foreach (GameLiving member in session.Group.GetMembersInTheGroup())
                if (IsOwnedCompanion(session.Player, session.Group, member) && !Fighting(member) && member is GameBot { Brain: BotBrain brain })
                    brain.CancelOrderedPull(session.Target);
            if (IsExclusiveParty(session.Player, session.Group, session.Group.GetMembersInTheGroup().ToArray()))
                PlayerLedPullCoordinator.OnGroupThreat(session.Player);
        }

        private static void ShowStatus(Session session, bool force = false)
        {
            if (!force && GameLoop.GameLoopTime < session.NextStatus) return;
            session.NextStatus = GameLoop.GameLoopTime + 5000;
            session.Player.Out.SendMessage($"GRIND MODE ACTIVE • {session.Detail}", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
        }
    }

    [CmdAttribute("&grind", ePrivLevel.Player, "Stationary automatic pulls with your temporary companions", "/grind", "/grind stop")]
    public sealed class PlayerGrindCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length == 2 && args[1].Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                if (PlayerCompanionGrind.IsActive(client.Player)) PlayerCompanionGrind.Stop(client.Player, "cancelled by you");
                else DisplayMessage(client, "Grind mode is not active.");
                return;
            }
            if (args.Length != 1) { DisplayMessage(client, "Use /grind or /grind stop."); return; }
            PlayerCompanionGrind.TryStart(client.Player, out string message);
            DisplayMessage(client, message);
        }
    }
}

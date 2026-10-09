using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;
using DOL.GS.Keeps;

namespace DOL.GS;

public sealed partial class AutonomousWorldBotController
{
    private readonly AutonomousFrontierThreatPolicy _frontierThreat = new();

    // PvP stalemate (run 15: Hibernian bots in Darkness Falls stood "fighting" one Albion raider for 15
    // minutes, 117 watchdog relocations): an enemy-realm target that has not lost health for 90 s while
    // this bot has not moved is dropped and ignored for 5 minutes, like a player giving up on a target
    // they cannot reach or hurt.
    private const long PvpStalemateMilliseconds = 90_000;
    private const long PvpIgnoreMilliseconds = 5 * 60_000;
    private GameLiving _pvpWatch;
    private long _pvpWatchSince;
    private int _pvpWatchHealth;
    private Vector3 _pvpWatchPosition;
    private readonly Dictionary<GameLiving, long> _ignoredPvpTargets = new();

    private bool BreakPvpStalemate(BotBrain brain, GameBot bot)
    {
        if (bot.TargetObject is not GameLiving target || target is not (GamePlayer or GameBot) || !target.IsAlive ||
            target.Realm == bot.Realm || target.Realm == eRealm.None)
        {
            _pvpWatch = null;
            return false;
        }
        long now = GameLoop.GameLoopTime;
        Vector3 position = new(bot.X, bot.Y, bot.Z);
        if (_pvpWatch != target || target.Health < _pvpWatchHealth ||
            Vector3.DistanceSquared(position, _pvpWatchPosition) > 150f * 150f)
        {
            _pvpWatch = target;
            _pvpWatchSince = now;
            _pvpWatchHealth = target.Health;
            _pvpWatchPosition = position;
            return false;
        }
        if (now - _pvpWatchSince < PvpStalemateMilliseconds) return false;
        if (_ignoredPvpTargets.Count > 32)
            foreach (GameLiving expired in _ignoredPvpTargets.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                _ignoredPvpTargets.Remove(expired);
        _ignoredPvpTargets[target] = now + PvpIgnoreMilliseconds;
        brain.RemoveFromAggroList(target);
        bot.StopAttack();
        bot.TargetObject = null;
        _pvpWatch = null;
        Log.Info($"AUTONOMOUS_PVP_STALEMATE bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} target=\"{target.Name}\" " +
                 $"targetRealm={GlobalConstants.RealmToName(target.Realm)} region={bot.CurrentRegionID} position={bot.X},{bot.Y},{bot.Z} seconds={(now - _pvpWatchSince) / 1000}");
        return true;
    }

    private bool IsIgnoredPvpTarget(GameLiving target) =>
        _ignoredPvpTargets.TryGetValue(target, out long until) && until > GameLoop.GameLoopTime;

    /// <summary>A target this bot dropped as unreachable or stalemated; its hits do not pull the bot back in.</summary>
    internal bool IgnoresTarget(GameLiving target) => target != null && IsIgnoredPvpTarget(target);

    // Owner 2026-10-07: a melee bot shot at from a keep wall (an archer guard, or an enemy bot up on the wall) must
    // not run around below it for ever. A target with no walkable route (closed enemy doors block the way) is dropped
    // for two minutes after two failed checks four seconds apart, and left to the ranged bots, who shoot back at
    // anything firing on their realm (TryEngageFrontierThreat).
    private const long UnreachableCheckMilliseconds = 4_000;
    private const long UnreachableIgnoreMilliseconds = 2 * 60_000;
    private GameLiving _reachWatch;
    private int _reachFailures;
    private long _nextReachCheck;

    private bool BreakUnreachableTarget(BotBrain brain, GameBot bot)
    {
        long now = GameLoop.GameLoopTime;
        if (now < _nextReachCheck) return false;
        _nextReachCheck = now + UnreachableCheckMilliseconds;
        if (AutonomousRvrDefense.IsRangedDefender(bot) || bot.TargetObject is not GameLiving target || !target.IsAlive ||
            target.Realm == bot.Realm || target is GameSiegeWeapon or GameKeepDoor || bot.IsWithinRadius(target, 250) ||
            // Only a target up on a wall or cliff (run 20 also dropped level-ground PvP targets 2-72 units apart).
            Math.Abs(target.Z - bot.Z) < 150)
        {
            _reachWatch = null;
            return false;
        }
        var nav = AutonomousKeepApproachNavigation.ForRealm(PathfindingProvider.Instance, bot.CurrentRegion, bot.Realm);
        if (!nav.IsAvailable || !nav.HasNavmesh(bot.CurrentZone)) return false;
        Vector3? floor = nav.GetClosestPoint(bot.CurrentZone, new(target.X, target.Y, target.Z), 64, 64, 96, nav.DefaultFilters);
        bool reachable = floor.HasValue && Math.Abs(floor.Value.Z - target.Z) <= 96 &&
            AutonomousZoneItinerary.HasCompleteCorridor(nav, bot.CurrentZone, new(bot.X, bot.Y, bot.Z), floor.Value);
        if (reachable || _reachWatch != target)
        {
            _reachWatch = reachable ? null : target;
            _reachFailures = reachable ? 0 : 1;
            return false;
        }
        if (++_reachFailures < 2) return false;
        _ignoredPvpTargets[target] = now + UnreachableIgnoreMilliseconds;
        brain.RemoveFromAggroList(target);
        bot.StopAttack();
        bot.StopMovingOnPath();
        bot.TargetObject = null;
        _reachWatch = null;
        Log.Info($"AUTONOMOUS_UNREACHABLE_TARGET bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} target=\"{target.Name}\" " +
                 $"targetRealm={GlobalConstants.RealmToName(target.Realm)} heightAbove={target.Z - bot.Z} region={bot.CurrentRegionID} position={bot.X},{bot.Y},{bot.Z}");
        return true;
    }

    /// <summary>An enemy attacking this bot or a friend near it: it has a line of fire, so it can be fired back at
    /// (wall archers stand where the navmesh ray cannot reach).</summary>
    private static bool FiringOnRealm(GameBot bot, GameLiving target) =>
        (target.IsAttacking || target.IsCasting) && target.TargetObject is GameLiving victim &&
        victim.Realm == bot.Realm && (victim == bot || bot.IsWithinRadius(victim, 1000));

    // The frontier, plus the neutral raid dungeons (Darkness Falls, Summoner's Hall and the
    // dungeons leading to it), where realms meet and fight on sight.
    internal static bool IsBattleground(GameLiving living) =>
        IsInFrontier(living) || living != null && (RealmRaidNeutralEvents.IsBattleRegion(living.CurrentRegionID) ||
            BattlegroundBrackets.IsBattlegroundRegion(living.CurrentRegionID));

    // Called before optional upkeep and the world controller's rendezvous,
    // recovery and follow returns. Assignment/level do not restrict defense.
    // It starts real combat without replacing the bot's durable task or event.
    public bool TryEngageFrontierThreat(BotBrain brain)
    {
        GameBot bot = brain?.BotBody;
        if (bot?.IsAutonomousWorldBot == true && IsBattleground(bot) && (BreakPvpStalemate(brain, bot) || BreakUnreachableTarget(brain, bot))) return false;
        bool defending = AutonomousRvrDefense.IsCommittedDefender(bot);
        bool siegeFighter = AutonomousRvrDefense.IsCommittedSiegeFighter(bot);
        GameLiving previousEngine = defending ? bot.TargetObject as GameSiegeWeapon ?? _siegeWeapon?.TargetObject as GameSiegeWeapon : null;
        if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup ||
            !bot.IsAlive || bot.ObjectState != GameObject.eObjectState.Active || bot.IsReturningAfterRelease ||
            bot.IsOnStableMasterRoute || !IsBattleground(bot) ||
            (brain.HasAggro || bot.InCombat || bot.IsAttacking) && previousEngine == null ||
            brain.FSM.GetCurrentState()?.StateType == eFSMStateType.PASSIVE ||
            !_frontierThreat.Due(GameLoop.GameLoopTime, bot.DatabaseID) || IsSafeArea(bot)) return false;

        var nav = PathfindingProvider.Instance;
        if (!nav.IsAvailable || !nav.HasNavmesh(bot.CurrentZone)) return false;
        // Cheap rejections first (most nearby livings are friendly bots); the area lookup last
        // (IsSafeArea/GetAreasOfZone were ~64 MB per 40 s of allocations in run 11).
        bool Eligible(GameLiving target) => target != bot && !target.IsStealthed && !IsIgnoredPvpTarget(target) &&
            target.ObjectState == GameObject.eObjectState.Active && target.IsAlive &&
            target.Realm != bot.Realm && target.Realm != eRealm.None && target.CurrentRegionID == bot.CurrentRegionID &&
            AutonomousRvrTargetPolicy.IsEligible(bot.Realm, target.Realm, target.IsAlive,
                target.CurrentRegionID == bot.CurrentRegionID, IsBattleground(target), IsSafeArea(target),
                GameServer.ServerRules.IsAllowedToAttack(bot, target, true));

        GameLiving[] nearby = bot.GetPlayersInRadius(TargetSearchRadius).Where(Eligible).Cast<GameLiving>()
            .Concat(bot.GetNPCsInRadius(TargetSearchRadius)
                .Where(npc => (npc is GameBot or GameSiegeWeapon ||
                    npc is GameKeepGuard && AutonomousRvrDefense.IsRangedDefender(bot) && FiringOnRealm(bot, npc) ||
                    (siegeFighter ? AutonomousRvrDefense.IsCombatant(npc) :
                        npc.Brain is IControlledBrain pet && pet.GetLivingOwner() is IGamePlayer)) && Eligible(npc)))
            .Where(target => bot.GetDistanceTo(target) <= TargetSearchRadius)
            .Where(target => defending || !BotSiegeRuntime.Assigned(bot) || bot.IsWithinRadius(target, 450) && target is not GameSiegeWeapon)
            .OrderBy(target => target.TargetObject is GameBot friendly && friendly.Realm == bot.Realm &&
                bot.IsWithinRadius(friendly, 1000) && BotSiegeRuntime.HoldingPosition(friendly) ? 0 : 1)
            .ThenBy(bot.GetDistanceTo).ToArray();
        bool Visible(GameLiving target) => FiringOnRealm(bot, target) || nav.HasLineOfSight(bot.CurrentZone,
            new(bot.X, bot.Y, bot.Z + 48), new(target.X, target.Y, target.Z + 48), nav.BlockingDoorAvoidanceFilters);
        var visible = siegeFighter
            ? _frontierThreat.VisiblePriority(nearby.Where(AutonomousRvrDefense.IsCombatant).ToArray(),
                nearby.OfType<GameSiegeWeapon>().Cast<GameLiving>().ToArray(), Visible)
            : _frontierThreat.Visible(nearby, target => FiringOnRealm(bot, target) || nav.HasLineOfSight(bot.CurrentZone,
                new(bot.X, bot.Y, bot.Z), new(target.X, target.Y, target.Z), nav.DefaultFilters));
        var visibleTargets=visible.ToArray();
        var operatorThreats=visibleTargets.Where(target=>target.IsAttacking && target.TargetObject is GameBot friendly &&
            friendly.Realm==bot.Realm && bot.IsWithinRadius(friendly,1000) && BotSiegeRuntime.HoldingPosition(friendly)).ToArray();
        GameLiving enemy = SelectDistributedRvrTarget(bot, operatorThreats.Length>0 ? operatorThreats : visibleTargets);
        if (enemy == null || !Eligible(enemy)) return false;
        if (previousEngine != null && enemy is GameSiegeWeapon) return false;

        if (defending)
        {
            // Combat wins over buying, deploying or operating a siege engine.
            // Release control without deleting the deployed equipment; normal
            // abandoned-engine reclamation remains available after combat.
            if (BotSiegeRuntime.Assigned(bot) || _siegeJobKeep != null) ReleaseSiegeJob(bot);
            _siegeNextAttempt = GameLoop.GameLoopTime + 10_000;
            if (previousEngine != null)
            {
                bot.StopAttack();
                brain.RemoveFromAggroList(previousEngine);
            }
            SetRvrStatus(bot, "Engaging keep attackers", _rvrDestination?.MonsterName ?? "Keep defense", enemy.Name);
        }

        bot.StopMovingOnPath();
        bot.StopMoving();
        bot.WakeRecoveryRest();
        bot.TargetObject = enemy;
        brain.AddToAggroList(enemy, Math.Max(100, enemy.EffectiveLevel * 12));
        if (!brain.HasAggro) return false;
        AutonomousDefensivePull.OnThreat(bot, enemy);
        AutonomousBotGroupCoordinator.MarkCombatObserved(bot.Group);
        brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
        return true;
    }
}

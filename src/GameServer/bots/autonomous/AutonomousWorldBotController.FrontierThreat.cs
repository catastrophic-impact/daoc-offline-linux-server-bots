using System;
using System.Linq;
using DOL.AI.Brain;

namespace DOL.GS;

public sealed partial class AutonomousWorldBotController
{
    private readonly AutonomousFrontierThreatPolicy _frontierThreat = new();

    // Called before optional upkeep and the world controller's rendezvous,
    // recovery and follow returns. Assignment/level do not restrict defense.
    // It starts real combat without replacing the bot's durable task or event.
    public bool TryEngageFrontierThreat(BotBrain brain)
    {
        GameBot bot = brain?.BotBody;
        bool defending = AutonomousRvrDefense.IsCommittedDefender(bot);
        bool siegeFighter = AutonomousRvrDefense.IsCommittedSiegeFighter(bot);
        GameLiving previousEngine = defending ? bot.TargetObject as GameSiegeWeapon ?? _siegeWeapon?.TargetObject as GameSiegeWeapon : null;
        if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup ||
            !bot.IsAlive || bot.ObjectState != GameObject.eObjectState.Active || bot.IsReturningAfterRelease ||
            bot.IsOnStableMasterRoute || !IsInFrontier(bot) || IsSafeArea(bot) ||
            (brain.HasAggro || bot.InCombat || bot.IsAttacking) && previousEngine == null ||
            brain.FSM.GetCurrentState()?.StateType == eFSMStateType.PASSIVE ||
            !_frontierThreat.Due(GameLoop.GameLoopTime, bot.DatabaseID)) return false;

        var nav = PathfindingProvider.Instance;
        if (!nav.IsAvailable || !nav.HasNavmesh(bot.CurrentZone)) return false;
        bool Eligible(GameLiving target) => target != bot && !target.IsStealthed &&
            target.ObjectState == GameObject.eObjectState.Active &&
            AutonomousRvrTargetPolicy.IsEligible(bot.Realm, target.Realm, target.IsAlive,
                target.CurrentRegionID == bot.CurrentRegionID, IsInFrontier(target), IsSafeArea(target),
                GameServer.ServerRules.IsAllowedToAttack(bot, target, true));

        GameLiving[] nearby = bot.GetPlayersInRadius(TargetSearchRadius).Where(Eligible).Cast<GameLiving>()
            .Concat(bot.GetNPCsInRadius(TargetSearchRadius)
                .Where(npc => (npc is GameBot or GameSiegeWeapon ||
                    (siegeFighter ? AutonomousRvrDefense.IsCombatant(npc) :
                        npc.Brain is IControlledBrain pet && pet.GetLivingOwner() is IGamePlayer)) && Eligible(npc)))
            .Where(target => bot.GetDistanceTo(target) <= TargetSearchRadius)
            .Where(target => defending || !BotSiegeRuntime.Assigned(bot) || bot.IsWithinRadius(target, 450) && target is not GameSiegeWeapon)
            .OrderBy(target => target.TargetObject is GameBot friendly && friendly.Realm == bot.Realm &&
                bot.IsWithinRadius(friendly, 1000) && BotSiegeRuntime.HoldingPosition(friendly) ? 0 : 1)
            .ThenBy(bot.GetDistanceTo).ToArray();
        bool Visible(GameLiving target) => nav.HasLineOfSight(bot.CurrentZone,
            new(bot.X, bot.Y, bot.Z + 48), new(target.X, target.Y, target.Z + 48), nav.BlockingDoorAvoidanceFilters);
        var visible = siegeFighter
            ? _frontierThreat.VisiblePriority(nearby.Where(AutonomousRvrDefense.IsCombatant).ToArray(),
                nearby.OfType<GameSiegeWeapon>().Cast<GameLiving>().ToArray(), Visible)
            : _frontierThreat.Visible(nearby, target => nav.HasLineOfSight(bot.CurrentZone,
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

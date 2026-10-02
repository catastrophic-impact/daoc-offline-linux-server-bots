using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using DOL.AI.Brain;
using DOL.GS.PacketHandler;

namespace DOL.GS
{
    /// <summary>
    /// One short-lived gate per player group, no timers, database writes or world scans.
    /// Existing bot turns check the gate; actual attack events release it once.
    /// </summary>
    public static class PlayerLedPullCoordinator
    {
        public const int ContactTimeoutMilliseconds = 30_000;
        private sealed class GroupState { public Order Pending; }
        private sealed class Order
        {
            public Group Group;
            public GamePlayer Leader;
            public GameBot Tank;
            public GameLiving Target;
            public long Deadline;
            public int Finished;
        }
        private static readonly ConditionalWeakTable<Group, GroupState> States = new();

        public static string Begin(GamePlayer player, GameLiving target)
        {
            Group group = player?.Group;
            if (!ValidEnemy(player, target)) return "No valid nearby pull target.";
            if (group == null)
            {
                // Preserve /pull as a direct pet command when playing solo.
                player.ControlledBrain?.Attack(target);
                return player.ControlledBrain == null ? "No party companions or pet to pull with." : "Pet pull ordered.";
            }
            GroupState state = States.GetOrCreateValue(group);
            Cancel(state.Pending, false);
            GameBot[] helpers = group.GetMembersInTheGroup().OfType<GameBot>()
                .Where(bot => Available(bot, player) && bot.IsWithinRadius(target, BotBrain.GROUP_DEFENSE_ASSIST_RADIUS) &&
                    bot.Brain is BotBrain).ToArray();
            foreach (GameBot bot in helpers) bot.EnterPlayerLedGroup(player);
            if (CompanionPvpEngagement.Enemy(player, target) && helpers.Any(bot => CompanionPvpEngagement.Leader(bot) == player))
            {
                // RvR is not a PvE tank-contact pull. A resting companion or an
                // unbroken mez must not silently veto the human's explicit order.
                CompanionPvpEngagement.Order(player, target);
                CompanionEngagementMode.RememberPull(player, target);
                foreach (GameBot bot in helpers.Where(bot => CompanionPvpEngagement.Leader(bot) == player))
                    ((BotBrain)bot.Brain).AssistPlayerAttack(target);
                bool engaging = helpers.Any(bot => CompanionPvpEngagement.Leader(bot) == player && CompanionEngagementMode.Allows(bot, target));
                if (engaging) player.ControlledBrain?.Attack(target);
                return engaging
                    ? "Raid PvP target ordered: attackers and pets engage; healers support."
                    : "Defensive mode: target remembered. Bring it within 350 units, or use /aggressive to engage at range.";
            }
            if (helpers.Any(bot => bot.IsTemporaryCompanionRestLocked))
                return "Party is resting; the pull will be available when everyone has recovered.";
            GameBot tank = helpers.Where(BotPartyRoles.IsTank)
                .Where(bot => !bot.IsCrowdControlled)
                .OrderBy(bot => bot.GetDistanceTo(target)).ThenBy(bot => bot.ObjectID).FirstOrDefault();
            if (tank == null || helpers.Any(bot => bot.InCombat || bot.IsAttacking) || FindLeaderTarget(player) != null)
            {
                Engage(player, target, true);
                return "Pull ordered: attackers engage; healers and buffers support the party.";
            }

            var order = new Order { Group = group, Leader = player, Tank = tank, Target = target,
                Deadline = GameLoop.GameLoopTime + ContactTimeoutMilliseconds };
            Volatile.Write(ref state.Pending, order);
            foreach (GameBot bot in helpers)
                if (bot != tank) ((BotBrain)bot.Brain).PrepareForTankPull();
            if (!((BotBrain)tank.Brain).OrderPull(target))
            {
                Cancel(order, false);
                return "Pull cancelled: the tank could not engage that target.";
            }
            return $"{tank.Name} leads the pull. Attackers wait for tank contact; healers and buffers support.";
        }

        public static bool IsWaiting(GameBot bot)
        {
            if (bot?.Group == null || !States.TryGetValue(bot.Group, out GroupState state)) return false;
            Order order = Volatile.Read(ref state.Pending);
            if (order == null || Volatile.Read(ref order.Finished) != 0) return false;
            if (order.Leader.Group != order.Group || !order.Group.IsInTheGroup(order.Leader) ||
                !ValidEnemy(order.Leader, order.Target) || !Available(order.Tank, order.Leader) ||
                order.Tank.Group != order.Group || !order.Group.IsInTheGroup(order.Tank) ||
                GameLoop.GameLoopTime >= order.Deadline)
            {
                Cancel(order, true);
                return false;
            }
            GameLiving humanTarget = FindLeaderTarget(order.Leader);
            if (humanTarget != null)
            {
                LeaderEngaged(order.Leader, humanTarget);
                return false;
            }
            return bot != order.Tank;
        }

        public static void LeaderEngaged(GamePlayer player, GameLiving target)
        {
            CompanionEngagementMode.RememberPull(player, target);
            if (player?.Group == null || !player.Group.IsInTheGroup(player) || !ValidEnemy(player, target)) return;
            CompanionPvpEngagement.Order(player, target);
            if (States.TryGetValue(player.Group, out GroupState state))
            {
                Order order = Volatile.Read(ref state.Pending);
                if (order != null && Volatile.Read(ref order.Finished) == 0 && order.Leader != player) return;
                if (order != null && Interlocked.CompareExchange(ref order.Finished, 1, 0) == 0 && order.Target != target)
                    (order.Tank.Brain as BotBrain)?.CancelOrderedPull(order.Target);
            }
            Engage(player, target, false);
        }

        public static void OnAttack(GameLiving actor, AttackData attack)
        {
            if (attack?.Target is not GameLiving target || attack.Attacker != actor || !attack.CausesCombat) return;
            if (actor is GamePlayer player) { LeaderEngaged(player, target); return; }
            // Do not resolve a companion's pet through GetPlayerOwner: that would
            // mistake a bot's pet for a human-initiated override of the tank gate.
            if (actor is GameNPC { Brain: ControlledMobBrain { Owner: GamePlayer petOwner } } &&
                petOwner.ControlledBrain?.Body == actor)
            { LeaderEngaged(petOwner, target); return; }
            if (actor is not GameBot tank || tank.Group == null || !attack.IsMeleeAttack ||
                !States.TryGetValue(tank.Group, out GroupState state)) return;
            Order order = Volatile.Read(ref state.Pending);
            if (order?.Tank != tank || order.Target != target || order.Leader.Group != order.Group ||
                !order.Group.IsInTheGroup(order.Leader) || !Available(tank, order.Leader) || !IsContact(attack.AttackResult) ||
                Interlocked.CompareExchange(ref order.Finished, 1, 0) != 0) return;
            Engage(order.Leader, target, true);
        }

        public static bool IsContact(eAttackResult result) => result is eAttackResult.HitStyle or
            eAttackResult.HitUnstyled or eAttackResult.Blocked or eAttackResult.Parried or eAttackResult.Evaded or eAttackResult.Missed;

        public static void OnGroupThreat(GameLiving member)
        {
            if (member?.Group != null && States.TryGetValue(member.Group, out GroupState state))
            {
                // Incoming danger overrides ordered-pull waiting, not group/range
                // restrictions. The existing defensive broadcast chooses the foe.
                Order order = Volatile.Read(ref state.Pending);
                if (order != null && Interlocked.CompareExchange(ref order.Finished, 1, 0) == 0)
                    (order.Tank.Brain as BotBrain)?.CancelOrderedPull(order.Target);
            }
        }

        public static GameLiving FindLeaderTarget(GamePlayer player)
        {
            if (player?.IsAlive != true) return null;
            if ((player.IsAttacking || player.IsCasting && player.castingComponent?.SpellHandler?.Spell?.IsHarmful == true) &&
                player.TargetObject is GameLiving target && ValidEnemy(player, target)) return target;
            if (player.ControlledBrain is ControlledMobBrain brain && brain.Owner == player &&
                brain.Body?.IsAlive == true && brain.Body.CurrentRegionID == player.CurrentRegionID &&
                player.IsWithinRadius(brain.Body, BotBrain.GROUP_DEFENSE_ASSIST_RADIUS))
            {
                GameLiving petTarget = brain.OrderedAttackTarget ??
                    ((brain.Body.IsAttacking || brain.Body.IsCasting &&
                      brain.Body.castingComponent?.SpellHandler?.Spell?.IsHarmful == true)
                        ? brain.Body.TargetObject as GameLiving : null);
                if (ValidEnemy(player, petTarget)) return petTarget;
            }
            return null;
        }

        private static void Engage(GamePlayer player, GameLiving target, bool orderHumanPet)
        {
            if (player?.Group == null || !ValidEnemy(player, target)) return;
            foreach (GameLiving member in player.Group.GetMembersInTheGroup())
            {
                if (member is GameBot bot && bot.PlayerGroupLeader == player && Available(bot, player) &&
                    bot.IsWithinRadius(target, BotBrain.GROUP_DEFENSE_ASSIST_RADIUS) && bot.Brain is BotBrain brain)
                    brain.AssistPlayerAttack(target);
            }
            if (orderHumanPet) player.ControlledBrain?.Attack(target);
        }

        public static bool Available(GameBot bot, GamePlayer player) => bot != null && player?.IsAlive == true &&
            bot.IsAlive && bot.ObjectState == GameObject.eObjectState.Active && bot.Realm == player.Realm &&
            bot.Group == player.Group && bot.Group?.IsInTheGroup(bot) == true &&
            bot.CurrentRegionID == player.CurrentRegionID && bot.IsWithinRadius(player, BotBrain.GROUP_DEFENSE_ASSIST_RADIUS) &&
            !bot.IsOnStableMasterRoute && !bot.IsReturningAfterRelease;

        private static bool ValidEnemy(GamePlayer player, GameLiving target) => player != null && target?.IsAlive == true &&
            target.ObjectState == GameObject.eObjectState.Active && target.CurrentRegionID == player.CurrentRegionID &&
            player.IsWithinRadius(target, BotBrain.GROUP_DEFENSE_ASSIST_RADIUS) &&
            GameServer.ServerRules.IsAllowedToAttack(player, target, true);

        private static void Cancel(Order order, bool inform)
        {
            if (order == null || Interlocked.CompareExchange(ref order.Finished, 1, 0) != 0) return;
            (order.Tank.Brain as BotBrain)?.CancelOrderedPull(order.Target);
            if (inform && order.Leader.Group == order.Group)
                order.Leader.Out.SendMessage("Pull cancelled: the tank could not make contact. Reposition and use /pull again.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }
}

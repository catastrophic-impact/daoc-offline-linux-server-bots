using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>Human-led temporary companions only; no PvE or autonomous AI policy.</summary>
    public static class CompanionPvpEngagement
    {
        private sealed class State
        {
            public GameLiving Focus;
            public long FocusUntil, NextScan;
            public readonly Dictionary<GameLiving, long> Threats = new();
        }
        private static readonly ConditionalWeakTable<GamePlayer, State> States = new();

        // Validate each link, so released/replaced pets cannot command or enlist a raid.
        public static GameLiving Character(GameLiving actor)
        {
            for (int depth = 0; depth < 16 && actor != null; depth++)
            {
                if (actor is GamePlayer or GameBot) return actor;
                if (actor is not GameNPC { Brain: IControlledBrain brain } pet || brain.Owner is not GameLiving owner ||
                    owner.ControlledBrain != brain && (owner is not GameNPC npc || npc.ControlledNpcList?.Contains(brain) != true))
                    return null;
                if (brain.Body != pet) return null;
                actor = owner;
            }
            return null;
        }

        public static GamePlayer Leader(GameLiving actor)
        {
            if (Character(actor) is not GameBot { IsTemporaryGroupHelper: true, IsAutonomousWorldBot: false } bot) return null;
            GamePlayer leader = bot.PlayerGroupLeader ?? bot.Owner;
            return leader != null && bot.Group != null && bot.Group == leader.Group &&
                bot.Group.IsInTheGroup(bot) && bot.Group.IsInTheGroup(leader) ? leader : null;
        }

        public static bool Enemy(GamePlayer player, GameLiving target) => player != null &&
            Character(target) is GameBot bot && bot.Realm != eRealm.None && bot.Realm != player.Realm;

        private static bool Live(GamePlayer player, GameLiving target) => Enemy(player, target) && target.IsAlive &&
            target.ObjectState == GameObject.eObjectState.Active && target.CurrentRegionID == player.CurrentRegionID &&
            player.IsWithinRadius(target, BotBrain.GROUP_DEFENSE_ASSIST_RADIUS);

        public static void Reset(GamePlayer player) => States.Remove(player);

        public static void Order(GamePlayer player, GameLiving target)
        {
            if (player != null && !Enemy(player, target))
            {
                if (States.TryGetValue(player, out State previous))
                    lock (previous) { previous.Focus = null; previous.FocusUntil = 0; }
                return;
            }
            if (!Live(player, target)) return;
            State state = States.GetOrCreateValue(player);
            lock (state) { state.Focus = target; state.FocusUntil = GameLoop.GameLoopTime + 30_000; }
        }

        public static bool Focused(GameLiving actor, GameLiving target)
        {
            GamePlayer leader = Leader(actor);
            if (!Live(leader, target)) return false;
            if (leader.IsAttacking && leader.TargetObject == target) return true;
            if (leader.ControlledBrain is ControlledMobBrain pet && pet.Owner == leader && pet.Body?.IsAlive == true &&
                pet.Body.CurrentRegionID == leader.CurrentRegionID &&
                Character(pet.Body) == leader && leader.IsWithinRadius(pet.Body, BotBrain.GROUP_DEFENSE_ASSIST_RADIUS) &&
                (pet.OrderedAttackTarget == target || pet.Body.IsAttacking && pet.Body.TargetObject == target)) return true;
            if (!States.TryGetValue(leader, out State state)) return false;
            lock (state) return state.Focus == target && GameLoop.GameLoopTime < state.FocusUntil;
        }

        public static bool Defending(GameLiving actor, GameLiving target)
        {
            GamePlayer leader = Leader(actor);
            if (!Live(leader, target) || !States.TryGetValue(leader, out State state)) return false;
            lock (state) return state.Threats.TryGetValue(target, out long until) && GameLoop.GameLoopTime < until;
        }

        public static bool RecordThreat(GameBot helper, GameLiving victim, AttackData attack)
        {
            GamePlayer leader = Leader(helper);
            GameLiving member = Character(victim);
            GameLiving enemy = attack?.Attacker;
            if (leader == null || member?.Group != helper.Group || !helper.Group.IsInTheGroup(member) ||
                victim.CurrentRegionID != helper.CurrentRegionID || !helper.IsWithinRadius(victim, BotBrain.GROUP_DEFENSE_ASSIST_RADIUS) ||
                attack?.CausesCombat != true || !attack.IsHit || !Live(leader, enemy) ||
                !GameServer.ServerRules.IsAllowedToAttack(leader, enemy, true)) return false;
            State state = States.GetOrCreateValue(leader);
            lock (state)
            {
                long now = GameLoop.GameLoopTime;
                foreach (GameLiving old in state.Threats.Where(p => p.Value <= now || !p.Key.IsAlive).Select(p => p.Key).ToArray())
                    state.Threats.Remove(old);
                if (state.Threats.Count >= 64 && !state.Threats.ContainsKey(enemy))
                    state.Threats.Remove(state.Threats.MinBy(p => p.Value).Key);
                // A whole raid may be mezzed. Keep the attacker remembered until
                // its hostile duration can finish; do not cleanse or bypass CC.
                long hostileDuration = attack.SpellHandler?.Spell?.Duration ?? 0;
                state.Threats.TryGetValue(enemy, out long previous);
                state.Threats[enemy] = Math.Max(previous, now + 12_000 + Math.Clamp(hostileDuration * 2, 0, 120_000));
            }
            return true;
        }

        // Once per whole party/raid, not 80 independent proximity scans.
        public static void Observe(GameBot helper)
        {
            GamePlayer leader = Leader(helper);
            if (leader?.IsAlive != true || !PlayerLedPullCoordinator.Available(helper, leader)) return;
            State state = States.GetOrCreateValue(leader);
            GameLiving focus;
            lock (state)
            {
                long now = GameLoop.GameLoopTime;
                if (now < state.NextScan) return;
                state.NextScan = now + 500;
                focus = now < state.FocusUntil ? state.Focus : null;
                if (!Live(leader, focus)) state.Focus = focus = null;
                if (focus == null)
                    focus = state.Threats.Where(p => p.Value > now && Live(leader, p.Key))
                        .OrderBy(p => leader.GetDistanceTo(p.Key)).Select(p => p.Key).FirstOrDefault();
            }
            GameLiving ordered = PlayerLedPullCoordinator.FindLeaderTarget(leader);
            if (ordered != null) { Order(leader, ordered); focus = Enemy(leader, ordered) ? ordered : null; }
            bool defensive = CompanionEngagementMode.DefensiveLeader(helper) != null;
            if (focus != null && !CompanionEngagementMode.Allows(helper, focus)) focus = null;
            if (focus == null && defensive)
                focus = SelectNearby(helper, leader.GetNPCsInRadius(CompanionEngagementMode.DefensiveRadius), BotSiegeRuntime.Visible);
            if (focus == null) return;
            foreach (GameLiving member in leader.Group.GetMembersInTheGroup())
                if (member is GameBot bot && Leader(bot) == leader && PlayerLedPullCoordinator.Available(bot, leader) &&
                    bot.IsWithinRadius(focus, BotBrain.GROUP_DEFENSE_ASSIST_RADIUS) && bot.Brain is BotBrain brain)
                    brain.AssistPlayerAttack(focus);
        }

        public static GameLiving SelectNearby(GameBot helper, IEnumerable<GameNPC> candidates, Func<GameLiving, GameLiving, bool> visible)
        {
            GamePlayer leader = Leader(helper);
            if (leader == null || CompanionEngagementMode.DefensiveLeader(helper) == null) return null;
            return candidates.Where(t => Live(leader, t) && !t.IsStealthed &&
                    leader.IsWithinRadius(t, CompanionEngagementMode.DefensiveRadius) &&
                    GameServer.ServerRules.IsAllowedToAttack(helper, t, true) && visible(leader, t))
                .OrderBy(leader.GetDistanceTo).FirstOrDefault();
        }
    }
}

using System;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>Short-lived reservations scoped to player-versus-player targets only.</summary>
    public static class BotPvpCrowdControl
    {
        private sealed class Claim { public long Until; public eRealm Realm; public WeakReference<GameBot> Caster; }
        private static readonly ConditionalWeakTable<GameLiving, Claim> Claims = new();
        public static bool PlayerLike(GameLiving living) => living is GamePlayer or GameBot ||
            living is GameNPC npc && npc.Brain is IControlledBrain pet && pet.GetLivingOwner() is IGamePlayer;
        public static bool Protected(GameLiving attacker, GameLiving target)
        {
            if (!PlayerLike(target) || attacker == null || target.Realm == attacker.Realm || target.Realm == eRealm.None) return false;
            GameBot bot = attacker as GameBot;
            if (bot == null && attacker is GameNPC npc && npc.Brain is IControlledBrain pet) bot = pet.GetLivingOwner() as GameBot;
            if (bot == null) return false;
            if (CompanionPvpEngagement.Focused(attacker, target) || CompanionPvpEngagement.Defending(attacker, target)) return false;
            // A real player's explicit attack remains authoritative.
            if (bot.Owner is GamePlayer player && player.IsAttacking && player.TargetObject == target) return false;
            if (target.IsMezzed) return true;
            if (!Claims.TryGetValue(target, out Claim claim)) return false;
            lock (claim) return claim.Realm == bot.Realm && claim.Until > GameLoop.GameLoopTime &&
                claim.Caster != null && claim.Caster.TryGetTarget(out GameBot caster) && caster.IsAlive && caster != bot;
        }
        public static bool Reserve(GameBot caster, GameLiving target, int duration)
        {
            Claim claim = Claims.GetOrCreateValue(target);
            lock (claim)
            {
                if (claim.Until > GameLoop.GameLoopTime && claim.Caster != null && claim.Caster.TryGetTarget(out GameBot old) && old != caster && old.IsAlive) return false;
                claim.Realm = caster.Realm;
                claim.Caster = new(caster);
                claim.Until = GameLoop.GameLoopTime + Math.Clamp(duration, 1000, 10_000);
                return true;
            }
        }
        public static void Release(GameBot caster, GameLiving target)
        {
            if (Claims.TryGetValue(target, out Claim claim)) lock (claim)
                if (claim.Caster != null && claim.Caster.TryGetTarget(out GameBot owner) && owner == caster) claim.Until = 0;
        }
    }
}

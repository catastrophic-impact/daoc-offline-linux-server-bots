using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>Player-led companions only. Never alters autonomous world bots.</summary>
    public static class CompanionFollowPolicy
    {
        public const int SettleMilliseconds = 600;
        private const int NativeTravelSpeedStartRadius = 650;
        private static readonly ConditionalWeakTable<GameBot, State> States = new();

        public sealed class State
        {
            public GamePlayer Leader;
            public Vector3 LastLeaderPosition, FormationPoint, LeaderVelocity;
            public bool Observed, BuffsBlocked;
            public long BuffsAfter, FormationUntil, LastObservation;

            public bool Observe(Vector3 position, bool moving, long now)
            {
                if (Observed && now > LastObservation)
                    LeaderVelocity = moving ? (position - LastLeaderPosition) * (1000f / (now - LastObservation)) : Vector3.Zero;
                if (moving || Observed && Vector3.DistanceSquared(position, LastLeaderPosition) > 4 * 4)
                    BuffsAfter = now + SettleMilliseconds;
                Observed = true;
                LastLeaderPosition = position;
                LastObservation = now;
                return moving || now < BuffsAfter;
            }
        }

        public static bool Applies(GameBot bot) => bot is { IsAutonomousWorldBot: false, IsPlayerLedGroup: true } &&
            bot.PlayerGroupLeader is { IsAlive: true, ObjectState: GameObject.eObjectState.Active } leader &&
            bot.Group != null && bot.Group == leader.Group && bot.CurrentRegion == leader.CurrentRegion;

        private static State For(GameBot bot)
        {
            State state = States.GetOrCreateValue(bot);
            if (state.Leader != bot.PlayerGroupLeader)
            {
                state.Leader = bot.PlayerGroupLeader;
                state.Observed = false;
                state.LeaderVelocity = Vector3.Zero;
                state.BuffsAfter = state.FormationUntil = 0;
                state.BuffsBlocked = false;
            }
            return state;
        }

        public static bool WaitingForLeaderToStop(GameBot bot)
        {
            if (!Applies(bot)) return false;
            var leader = bot.PlayerGroupLeader;
            return For(bot).Observe(new(leader.X, leader.Y, leader.Z), leader.IsMoving, GameLoop.GameLoopTime);
        }

        public static bool IsOrdinaryBuff(GameLiving caster, Spell spell)
        {
            if (spell == null || spell.IsHarmful || spell.IsHealing || BotSongTwistPolicy.IsMobileSong(caster, spell) ||
                caster is GameBot bot && IsNativeCompanionTravelSpeed(bot, spell)) return false;
            if (spell.SpellType == eSpellType.PetSpell && spell.SubSpellID > 0)
            {
                Spell payload = SkillBase.GetSpellByID(spell.SubSpellID);
                return payload != null && payload.SpellType != eSpellType.PetSpell && IsOrdinaryBuff(caster, payload);
            }
            return BotBrain.IsMaintainableClassBuff(spell);
        }

        // These four classes learn a stationary, three-second group speed pulse.
        // Unlike a performer's mobile song, it requires a real stop and cast.
        // Restrict the exception to temporary player companions and learned
        // native speed spells; ordinary buffs and world-bot behavior stay put.
        private static bool HasNativeTravelSpeed(GameBot bot) =>
            bot is { IsTemporaryGroupHelper: true, IsAutonomousWorldBot: false, IsPlayerLedGroup: true } &&
            bot.CharacterClass != null &&
            (eCharacterClass)bot.CharacterClass.ID is
                (eCharacterClass.Enchanter or eCharacterClass.Sorcerer or
                 eCharacterClass.Healer or eCharacterClass.Runemaster);

        public static bool IsNativeCompanionTravelSpeed(GameBot bot, Spell spell) =>
            HasNativeTravelSpeed(bot) &&
            spell is { SpellType: eSpellType.SpeedEnhancement, Target: eSpellTarget.GROUP,
                       IsPulsing: true, IsHarmful: false, NeedInstrument: false } &&
            spell.CastTime > 0;

        public static bool CanStopForNativeTravelSpeed(GameBot bot) =>
            HasNativeTravelSpeed(bot) && Applies(bot) &&
            bot.PlayerGroupLeader is { IsMoving: true, InCombat: false, IsAttacking: false } leader &&
            !bot.InCombat && !bot.IsAttacking && !bot.IsOnStableMasterRoute &&
            bot.IsWithinRadius(leader, NativeTravelSpeedStartRadius);

        public static bool CanStopForNativeTravelSpeed(GameBot bot, Spell spell) =>
            CanStopForNativeTravelSpeed(bot) && IsNativeCompanionTravelSpeed(bot, spell);

        public static bool DeferBuff(GameLiving caster, Spell spell)
        {
            if (caster is not GameBot bot || !Applies(bot) || !WaitingForLeaderToStop(bot))
                return false;
            if (IsNativeCompanionTravelSpeed(bot, spell))
                return !CanStopForNativeTravelSpeed(bot, spell);
            return IsOrdinaryBuff(bot, spell);
        }

        // Do not discard a queued heal, resurrection, attack or mobile song.
        public static bool CancelOrdinaryBuffs(GameLiving caster)
        {
            var casting = caster?.castingComponent;
            if (casting == null) return false;
            Spell active = casting.SpellHandler?.Spell;
            Spell queued = casting.QueuedSpellHandler?.Spell;
            bool pending = casting.TryPeekPendingSpell(out Spell requested);
            if (active == null && queued == null && !pending) return false;
            if (active != null && !IsOrdinaryBuff(caster, active) ||
                queued != null && !IsOrdinaryBuff(caster, queued) ||
                pending && !IsOrdinaryBuff(caster, requested)) return false;
            caster.StopCurrentSpellcast();
            return true;
        }

        // True on settling, so existing selectors can retry without retaining
        // a canceled-cast backoff. No new timer, population scan or persistence.
        public static bool ObserveAndCancelBuffs(GameBot bot) => ObserveAndCancelBuffs(bot, out _);

        public static bool ObserveAndCancelBuffs(GameBot bot, out bool nativeSpeedTravelStarted)
        {
            nativeSpeedTravelStarted = false;
            if (!Applies(bot)) return false;
            State state = For(bot);
            bool waiting = WaitingForLeaderToStop(bot);
            bool settled = state.BuffsBlocked && !waiting;
            nativeSpeedTravelStarted = waiting && !state.BuffsBlocked &&
                bot.PlayerGroupLeader.IsMoving && HasNativeTravelSpeed(bot);
            state.BuffsBlocked = waiting;
            if (waiting)
            {
                CancelOrdinaryBuffs(bot);
                if (bot.ControlledBrain is NecromancerPetBrain servant)
                    servant.CancelCompanionTravelBuffs();
            }
            return settled;
        }

        public static bool NativeTravelSpeedCastInProgress(GameBot bot)
        {
            if (bot?.castingComponent == null) return false;
            var casting = bot.castingComponent;
            return IsNativeCompanionTravelSpeed(bot, casting.SpellHandler?.Spell) ||
                   IsNativeCompanionTravelSpeed(bot, casting.QueuedSpellHandler?.Spell) ||
                   casting.TryPeekPendingSpell(out Spell pending) &&
                   IsNativeCompanionTravelSpeed(bot, pending);
        }

        public static void BeginFormation(GameBot bot, Vector3 point)
        {
            if (!Applies(bot)) return;
            State state = For(bot);
            state.FormationPoint = point;
            state.FormationUntil = GameLoop.GameLoopTime + 750;
        }

        public static bool HasFormationOrder(GameBot bot) => Applies(bot) &&
            States.TryGetValue(bot, out State state) && GameLoop.GameLoopTime < state.FormationUntil;

        public static Vector3 Predict(Vector3 point, Vector3 velocity, int leaderSpeed)
        {
            float length = velocity.Length();
            if (!float.IsFinite(length) || length < 1 || leaderSpeed <= 0) return point;
            // A fifth of a second, never more than 80 units. This bridges the
            // decision interval without chasing a point the player just left.
            return point + velocity / length * Math.Min(80, Math.Min(length, leaderSpeed) * 0.2f);
        }

        public static Vector3 FormationDestination(GameBot bot, Vector3 point)
        {
            if (!Applies(bot)) return point;
            var leader = bot.PlayerGroupLeader;
            if (leader.IsMoving) point = Predict(point, For(bot).LeaderVelocity, leader.MaxSpeed);
            Vector3 origin = new(leader.X, leader.Y, leader.Z);
            if (leader.CurrentZone == null) return point;
            return PathfindingProvider.Instance.GetMoveAlongSurface(leader.CurrentZone, origin, point,
                PathfindingProvider.Instance.DefaultFilters) ?? origin;
        }

        public static bool SendsDruidPet(GameBot bot) => Applies(bot) &&
            bot.CharacterClass?.ID == (int)eCharacterClass.Druid && bot.Stance != eBotStance.Passive;

        public static int CalculateSpeed(int normal, int leaderSpeed, double distanceFromSlot)
        {
            if (normal <= 0) return normal;
            double catchup = Math.Clamp((distanceFromSlot - 60) / 900d, 0, 1) * 0.20;
            return (int)Math.Clamp(Math.Round(Math.Max(normal, leaderSpeed) * (1 + catchup)), 0, short.MaxValue);
        }

        public static short SpeedLimit(GameBot bot, short normal)
        {
            if (!Applies(bot) || !States.TryGetValue(bot, out State state) ||
                GameLoop.GameLoopTime >= state.FormationUntil || normal <= 0 || !bot.IsAlive ||
                bot.IsOnStableMasterRoute || bot.IsRecoveryResting || bot.InCombat || bot.IsAttacking ||
                bot.IsCrowdControlled || bot.IsDiseased || bot.HealthPercent < 33 ||
                bot.Brain is BotBrain { HasAggro: true } || bot.PlayerGroupLeader.InCombat ||
                bot.PlayerGroupLeader.IsAttacking || bot.PlayerGroupLeader.IsOnHorse ||
                bot.IsStealthed || bot.PlayerGroupLeader.IsStealthed ||
                GameRelic.IsPlayerCarryingRelic(bot) || GameRelic.IsPlayerCarryingRelic(bot.PlayerGroupLeader) ||
                bot.effectListComponent?.ContainsEffectForEffectType(eEffect.MovementSpeedDebuff) == true ||
                bot.BuffBonusMultCategory1.Get((int)eProperty.MaxSpeed) < 1 ||
                (bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true) && !BotSongTwistPolicy.HasMobileSongCast(bot))
                return normal;
            return (short)CalculateSpeed(normal, bot.PlayerGroupLeader.MaxSpeed,
                Vector3.Distance(new(bot.X, bot.Y, bot.Z), state.FormationPoint));
        }
    }
}

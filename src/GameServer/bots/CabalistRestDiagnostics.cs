using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Logging;

namespace DOL.GS
{
    // Temporary companions only. No world scans, database writes, or AI decisions.
    internal static class CabalistRestDiagnostics
    {
        private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly ConditionalWeakTable<GameBot, State> States = new();
        private sealed class State
        {
            public long NextReport;
            public string Attempt = "none";
            public string Message = "none";
        }

        internal static bool Applies(GameBot bot) => bot?.IsTemporaryGroupHelper == true &&
            !bot.IsAutonomousWorldBot && bot.CharacterClass?.ID == (int)eCharacterClass.Cabalist;

        internal static void Attempt(GameBot bot, Spell spell, bool accepted)
        {
            if (!Applies(bot) || spell == null || spell.IsHarmful) return;
            States.GetOrCreateValue(bot).Attempt = $"{spell.Name}({spell.ID}), target={bot.TargetObject?.Name}, accepted={accepted}";
        }

        internal static void Message(GameLiving caster, Spell spell, string message)
        {
            GameBot bot = caster as GameBot;
            if (bot == null && caster is GameNPC { Brain: IControlledBrain brain })
                bot = brain.Owner as GameBot;
            if (!Applies(bot)) return;
            States.GetOrCreateValue(bot).Message = $"{caster.Name}: {spell?.Name}({spell?.ID}): {message}";
        }

        internal static void Observe(GameBot bot, GamePlayer leader)
        {
            if (!Applies(bot)) return;
            State state = States.GetOrCreateValue(bot);
            long now = GameLoop.GameLoopTime;
            if (bot.IsRecoveryResting || !bot.IsAlive || leader == null || leader.IsMoving ||
                bot.InCombat || leader.InCombat || !BotRestRecovery.HasAnyResourceDeficit(
                    bot.Health, bot.MaxHealth, bot.Mana, bot.MaxMana, bot.Endurance, bot.MaxEndurance))
            {
                state.NextReport = now + 30_000;
                return;
            }
            if (state.NextReport == 0) state.NextReport = now + 30_000;
            if (now < state.NextReport) return;
            state.NextReport = now + 30_000;
            GameNPC pet = bot.ControlledBrain?.Body;
            bot.castingComponent.TryPeekPendingSpell(out Spell pending);
            Log.Warn($"CABALIST_REST name={bot.Name} level={bot.Level} hp={bot.Health}/{bot.MaxHealth} mana={bot.Mana}/{bot.MaxMana} " +
                $"end={bot.Endurance}/{bot.MaxEndurance} moving={bot.IsMoving} leaderDistance={bot.GetDistanceTo(leader)} " +
                $"blocked={BotRestRecovery.BlocksRest(bot)} aggro={(bot.Brain as BotBrain)?.HasAggro} " +
                $"cast={bot.castingComponent.SpellHandler?.Spell?.Name} pending={pending?.Name} " +
                $"pet={pet?.Name} petMoving={pet?.IsMoving} petCombat={pet?.InCombat} petTarget={pet?.TargetObject?.Name} " +
                $"petCast={pet?.castingComponent?.SpellHandler?.Spell?.Name} lastAttempt=[{state.Attempt}] lastMessage=[{state.Message}]");
        }
    }
}

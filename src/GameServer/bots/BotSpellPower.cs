using System;

namespace DOL.GS
{
    /// <summary>
    /// Bot affordability mirrors native costs without guessing free-cast procs.
    /// Only native settlement rolls free-cast effects, never AI evaluation.
    /// Quickcast is never used by a GameBot. Human spell costs are unchanged.
    /// </summary>
    public static class BotSpellPower
    {
        public static bool IsOffensiveCaster(GameBot bot) => bot.CharacterClass?.ClassType == eClassType.ListCaster &&
            bot.CharacterClass.ID is not (int)eCharacterClass.Valewalker and not (int)eCharacterClass.Vampiir;

        // A focus root channels until canceled, spending power while preventing
        // the aggressive caster rotation. Use ordinary roots/DoTs/nukes instead.
        // Human spell selection and damaging channels are unaffected.
        public static bool BlocksAttackerRotation(GameBot bot, Spell spell) =>
            IsOffensiveCaster(bot) && spell?.IsFocus == true &&
            spell.IsHarmful && spell.Damage <= 0;

        // A native channeled spell pays on each real pulse, not every AI tick.
        // Preserve the spell's native upkeep cost for bots and players alike.
        public static int PulseCost(GameLiving caster, Spell spell) => spell.PulsePower;

        public static int Cost(GameBot bot, Spell spell, SpellLine line)
        {
            if (spell == null) return 0;
            // These native handlers use health/endurance instead of mana.
            if (bot.CharacterClass?.ID == (int)eCharacterClass.Savage ||
                spell.SpellType is eSpellType.Archery)
                return 0;

            line = bot.ResolvePowerSpellLine(spell, line);
            double cost = spell.Power;
            if (cost < 0)
            {
                int basePool = bot.CharacterClass?.ManaStat is eStat stat && stat != eStat.UNDEFINED
                    ? bot.CalculateMaxMana(bot.Level, bot.GetBaseStat(stat))
                    : bot.MaxMana;
                cost *= basePool * -0.01;
            }

            if (bot.CharacterClass?.IsFocusCaster == true && line != null)
            {
                eProperty focusProperty = SkillBase.SpecToFocus(line.Spec);
                if (focusProperty != eProperty.Undefined)
                {
                    double focus = bot.GetModified(focusProperty) * 0.4;
                    if (spell.Level > 0) focus /= spell.Level;
                    focus = Math.Clamp(focus, 0, 0.4);
                    focus *= Math.Min(1, bot.GetModifiedSpecLevel(line.Spec) / (double)Math.Max(1, spell.Level));
                    cost *= 1.2 - focus;
                }
            }

            return Math.Max(0, (int)cost);
        }
    }
}

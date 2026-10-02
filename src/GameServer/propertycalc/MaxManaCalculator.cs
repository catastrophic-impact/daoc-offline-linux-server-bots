using System;

namespace DOL.GS.PropertyCalc
{
    /// <summary>
    /// The Power Pool calculator
    /// 
    /// BuffBonusCategory1 unused
    /// BuffBonusCategory2 unused
    /// BuffBonusCategory3 unused
    /// BuffBonusCategory4 unused
    /// BuffBonusMultCategory1 unused
    /// </summary>
    [PropertyCalculator(eProperty.MaxMana)]
    public class MaxManaCalculator : PropertyCalculator
    {
        public MaxManaCalculator() {}

        public override int CalcValue(GameLiving living, eProperty property)
        {
            // NPC-backed playerbots use the same modified stat and capped
            // equipment/ability bonuses as human characters, not a bare pool.
            if (living is not GamePlayer player)
            {
                if (living is IGamePlayer bot && bot.CharacterClass?.ManaStat is eStat stat && stat != eStat.UNDEFINED)
                    return ApplyBonuses(living, bot.CalculateMaxMana(bot.Level, living.GetModified((eProperty)stat)));
                return 0;
            }

            eStat manaStat;

            if (player.CharacterClass.ManaStat is not eStat.UNDEFINED)
                manaStat = player.CharacterClass.ManaStat;
            else
            {
                // Special handling for Vampiirs:
                /* There is no stat that affects the Vampiir's power pool or the damage done by its power based spells.
                 * The Vampiir is not a focus based class like, say, an Enchanter.
                 * The Vampiir is a lot more cut and dried than the typical casting class.
                 * EDIT, 12/13/04 - I was told today that this answer is not entirely accurate.
                 * While there is no stat that affects the damage dealt (in the way that intelligence or piety affects how much damage a more traditional caster can do),
                 * the Vampiir's power pool capacity is intended to be increased as the Vampiir's strength increases.
                 *
                 * This means that strength ONLY affects a Vampiir's mana pool
                 */
                if ((eCharacterClass) player.CharacterClass.ID is eCharacterClass.Vampiir)
                    manaStat = eStat.STR;
                else if (player.Champion && player.ChampionLevel > 0)
                    return player.CalculateMaxMana(player.Level, 0);
                else
                    return 0;
            }

            int manaBase = player.CalculateMaxMana(player.Level, player.GetModified((eProperty) manaStat));
            return ApplyBonuses(living, manaBase);
        }

        private static int ApplyBonuses(GameLiving living, int manaBase)
        {
            int flatItemBonusCap = living.Level / 2 + 1;
            int poolItemBonusCap = living.Level / 2 + Math.Min(living.ItemBonus[eProperty.PowerPoolCapBonus], living.Level);
            int flatItemBonus = Math.Min(flatItemBonusCap, living.ItemBonus[eProperty.MaxMana]); // Pre-ToA flat bonus.
            int poolItemBonus = Math.Min(poolItemBonusCap, living.ItemBonus[eProperty.PowerPool]); // ToA bonus.
            int flatAbilityBonus = living.AbilityBonus[eProperty.MaxMana]; // New Ethereal Bond.
            int poolAbilityBonus = living.AbilityBonus[eProperty.PowerPool]; // Old Ethereal Bond.

            double result = manaBase;
            result *= 1 + poolAbilityBonus * 0.01;
            result += flatItemBonus + flatAbilityBonus;
            result *= 1 + poolItemBonus * 0.01;
            return (int) result;
        }
    }
}

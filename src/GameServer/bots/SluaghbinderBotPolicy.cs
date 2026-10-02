using System;
using System.Linq;
using DOL.Database;
using DOL.GS.Spells;

namespace DOL.GS
{
    /// <summary>Small, class-local guards shared by the dedicated bot brain and pet upkeep.</summary>
    public static class SluaghbinderBotPolicy
    {
        // These IDs are the five epic-quest service summons in the isolated
        // test data.  The spell type check protects a future reseed that uses
        // different numeric IDs.
        public static bool IsPlayerOnlyServiceSpell(Spell spell) =>
            spell != null &&
            ((spell.ID >= 59080 && spell.ID <= 59084) ||
             spell.SpellType == eSpellType.SluaghbinderEpicSummon);

        public static void RemovePlayerOnlyServiceSpells(GameBot bot)
        {
            if (bot?.Spells == null || bot.CharacterClass?.ID != (int)eCharacterClass.Sluaghbinder)
                return;
            bot.Spells.RemoveAll(IsPlayerOnlyServiceSpell);
        }

        public static bool IsValidCombatTarget(GameBot bot, GameLiving target) =>
            bot != null && target?.IsAlive == true &&
            target.ObjectState == GameObject.eObjectState.Active &&
            target.CurrentRegionID == bot.CurrentRegionID &&
            GameServer.ServerRules.IsAllowedToAttack(bot, target, true);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>Unlimited supplies, not unlimited procs: a native coat per weapon outside combat.</summary>
    public static class BotPoisonSupply
    {
        private sealed record Coat(int Level, int SpellId, eSpellType Type);
        private static Coat[] _catalog = Array.Empty<Coat>();
        public static bool IsAssassin(eCharacterClass characterClass) => characterClass is
            eCharacterClass.Infiltrator or eCharacterClass.Shadowblade or eCharacterClass.Nightshade;
        public static int EligibleLevel(int level, int envenom) => Math.Clamp(Math.Min(level, envenom), 0, 50);

        // One startup query; never reads SQLite during AI turns.
        public static void Load()
        {
            Load(GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Object_Type").IsEqualTo((int)eObjectType.Poison)),
                SkillBase.GetSpellList(GlobalSpellsLines.Mundane_Poisons));
        }

        public static void Load(IEnumerable<DbItemTemplate> items, IEnumerable<Spell> spellList)
        {
            var spells = spellList.GroupBy(s => s.ID).ToDictionary(g => g.Key, g => g.First());
            _catalog = items
                .Where(item => item.Level is > 0 and <= 50 && item.PoisonCharges > 0 &&
                    spells.TryGetValue(item.PoisonSpellID, out Spell spell) &&
                    spell.SpellType is eSpellType.DamageOverTime or eSpellType.StrengthConstitutionDebuff)
                .Select(item => new Coat(Math.Max(item.Level, spells[item.PoisonSpellID].Level),
                    item.PoisonSpellID, spells[item.PoisonSpellID].SpellType))
                .Distinct().OrderByDescending(coat => coat.Level).ToArray();
        }

        public static bool Maintain(GameBot bot)
        {
            if (bot?.CharacterClass == null || !IsAssassin((eCharacterClass)bot.CharacterClass.ID) ||
                !bot.IsAlive || bot.InCombat || bot.IsAttacking || bot.IsCasting || bot.IsCrowdControlled ||
                bot.castingComponent?.HasPendingSkillRequests == true) return false;
            int level = EligibleLevel(bot.Level, bot.GetModifiedSpecLevel(Specs.Envenom));
            Coat damage = null, debuff = null;
            foreach (Coat coat in _catalog)
            {
                if (coat.Level > level) continue;
                if (damage == null && coat.Type == eSpellType.DamageOverTime) damage = coat;
                if (debuff == null && coat.Type == eSpellType.StrengthConstitutionDebuff) debuff = coat;
                if (damage != null && debuff != null) break;
            }
            bool changed = Apply(bot, eInventorySlot.RightHandWeapon, damage);
            changed |= Apply(bot, eInventorySlot.TwoHandWeapon, damage);
            changed |= Apply(bot, eInventorySlot.LeftHandWeapon, debuff ?? damage);
            if (changed) Changed(bot);
            return changed;
        }

        private static bool Apply(GameBot bot, eInventorySlot slot, Coat coat)
        {
            DbInventoryItem weapon = bot.Inventory?.GetItem(slot);
            if (coat == null || !BotWeaponStats.FitsConfiguredSlot(bot, weapon, slot) || weapon.PoisonCharges > 0)
                return false;
            weapon.PoisonSpellID = coat.SpellId;
            weapon.PoisonMaxCharges = 1;
            weapon.PoisonCharges = 1;
            return true;
        }

        public static void Changed(GameBot bot)
        {
            if (!bot.IsAutonomousWorldBot) return;
            bot.MarkAutonomousStateDirty();
            // A coat changes neither equipment suitability nor backpack space;
            // don't invalidate the expensive vendor/upgrade appraisal cache.
            AutonomousBotStatusPersistence.Queue(bot, true);
        }
    }
}

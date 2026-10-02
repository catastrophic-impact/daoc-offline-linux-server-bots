using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS
{
    /// <summary>Only ephemeral /spawn companions receive the endgame exception.</summary>
    public static class TemporaryCompanionBalance
    {
        public static bool IsEndgame(bool temporary, int level) => temporary && level == 50;

        public static bool EquipRegularSlot(bool temporary, int level, double roll) => true;

        public static bool EquipOffhand(bool temporary, int level, bool permitted, double roll) =>
            !temporary || permitted;

        public static int GearLevel(bool temporary, int level, int requested) =>
            IsEndgame(temporary, level) || requested == 0 ? level : Math.Clamp(requested, Math.Max(1, level - 10), level);

        public static Spell HighestSpell(IEnumerable<Spell> available) => available
            .Where(spell => spell != null).OrderByDescending(spell => spell.Level)
            .ThenByDescending(spell => spell.ID).FirstOrDefault();

        // Rank upgrades often change cast time, range, radius or power cost.
        // Those numbers must not keep obsolete ranks in the level-50 book.
        // Instant/cast, single/area, pulsing/lasting and different spell roles
        // remain distinct. Necromancer wrappers are grouped by their payload
        // role, never all collapsed into a single PetSpell.
        private static object RankKey(Spell spell, Func<int, Spell> resolvePayload)
        {
            Spell payload = spell.SubSpellID > 0 ? resolvePayload?.Invoke(spell.SubSpellID) : null;
            return (spell.SpellType, spell.DamageType, spell.Target, spell.Group,
                spell.IsInstantCast, spell.IsAoE, spell.IsPBAoE, spell.IsPulsing,
                spell.IsConcentration, spell.Frequency > 0,
                Payload: payload == null ? (object)spell.SubSpellID :
                    (payload.SpellType, payload.DamageType, payload.Target, payload.IsAoE, payload.Frequency > 0));
        }

        public static List<Spell> HighestRanks(IEnumerable<Spell> learned, int level, Func<int, Spell> resolvePayload = null) =>
            learned.Where(spell => spell != null && spell.Level <= level)
                .GroupBy(spell => RankKey(spell, resolvePayload)).Select(HighestSpell)
                .OrderByDescending(spell => spell.Level).ThenByDescending(spell => spell.ID).ToList();
    }
}

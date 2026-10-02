using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;

namespace DOL.GS
{
    // Installed player spell IDs identify the rank actually cast, including
    // deliberately casting an older rank. Bot upkeep does not use this policy.
    public static class PlayerGeneratedCharmPolicy
    {
        public static bool TryGetRank(eCharacterClass characterClass, int spellId,
            out int unlockLevel, out int levelBonus)
        {
            (unlockLevel, levelBonus) = (characterClass, spellId) switch
            {
                (eCharacterClass.Sorcerer, 951) => (1, -3),
                (eCharacterClass.Sorcerer, 952) => (7, -2),
                (eCharacterClass.Sorcerer, 953) => (12, -1),
                (eCharacterClass.Sorcerer, 954) => (20, 0),
                (eCharacterClass.Sorcerer, 955) => (32, 1),
                (eCharacterClass.Mentalist, 4211) => (4, -3),
                (eCharacterClass.Mentalist, 4212) => (10, -2),
                (eCharacterClass.Mentalist, 4213) => (17, -1),
                (eCharacterClass.Mentalist, 4214) => (25, 0),
                (eCharacterClass.Mentalist, 4215) => (33, 1),
                (eCharacterClass.Mentalist, 4216) => (42, 2),
                (eCharacterClass.Minstrel, 1151) => (5, -3),
                (eCharacterClass.Minstrel, 1152) => (13, -2),
                (eCharacterClass.Minstrel, 1153) => (20, -1),
                (eCharacterClass.Minstrel, 1154) => (28, 0),
                (eCharacterClass.Minstrel, 1155) => (34, 1),
                (eCharacterClass.Minstrel, 1156) => (41, 2),
                (eCharacterClass.Hunter, 3551) => (1, -3),
                (eCharacterClass.Hunter, 3552) => (7, -2),
                (eCharacterClass.Hunter, 3553) => (13, -1),
                (eCharacterClass.Hunter, 3554) => (20, 0),
                (eCharacterClass.Hunter, 3555) => (32, 1),
                (eCharacterClass.Hunter, 3576) => (3, -3),
                (eCharacterClass.Hunter, 3577) => (9, -2),
                (eCharacterClass.Hunter, 3578) => (15, -1),
                (eCharacterClass.Hunter, 3579) => (22, 0),
                (eCharacterClass.Hunter, 3580) => (35, 1),
                _ => (0, 0)
            };
            return unlockLevel > 0;
        }

        public static int TargetLevel(eCharacterClass characterClass, int spellId, int playerLevel) =>
            TryGetRank(characterClass, spellId, out int unlock, out int bonus) && playerLevel >= unlock
                ? Math.Max(1, playerLevel + bonus) : 0;

        public static DbMob[] SelectTemplates(IEnumerable<DbMob> eligible, int targetLevel)
        {
            // Equal chances per creature name, rather than per world spawn.
            var distinct = eligible.Where(mob => mob.Level > 0 && Math.Abs(mob.Level - targetLevel) <= 2)
                .GroupBy(mob => mob.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(mob => Math.Abs(mob.Level - targetLevel)).First())
                .OrderBy(mob => Math.Abs(mob.Level - targetLevel)).ToArray();
            // Show every eligible creature in the existing near-level pool, not
            // the former small random shortlist. Summon level is unchanged.
            return distinct;
        }
    }
}

using System;
using System.Collections.Generic;

namespace DOL.GS
{
    /// <summary>Saved as the quest's "difficulty" property (0, 1, 2).</summary>
    public enum BountyDifficulty
    {
        Normal = 0,
        Hard = 1,
        VeryHard = 2
    }

    /// <summary>
    /// Leveling bounty difficulty. Only the monster level, XP bulbs and gear
    /// level change; kill counts are the same for every difficulty. Level-50
    /// great-foe bounties have no difficulty and always use Normal.
    /// </summary>
    public static class BountyDifficultyRules
    {
        /// <summary>Hard and Very Hard never need monsters above this level (49 + 8).</summary>
        public const byte HighestTargetLevel = 61;

        /// <summary>
        /// A Hard or Very Hard pool with fewer distinct monsters than this at the
        /// exact level also takes monsters one, then two levels lower.
        /// </summary>
        public const int MinimumSpeciesPerPool = 8;

        public static int MonsterLevelOffset(BountyDifficulty difficulty) => difficulty switch
        {
            BountyDifficulty.Hard => 6,
            BountyDifficulty.VeryHard => 12,
            _ => 0
        };

        /// <summary>Rounding down stops here, so a hard pool always stays harder than normal.</summary>
        public static int LowestMonsterLevelOffset(BountyDifficulty difficulty) => difficulty switch
        {
            BountyDifficulty.Hard => 4,
            BountyDifficulty.VeryHard => 10,
            _ => 0
        };

        /// <summary>Monster levels to try, best first: the exact level, then one and two lower.</summary>
        public static IReadOnlyList<byte> TargetLevels(int playerLevel, BountyDifficulty difficulty)
        {
            if (difficulty == BountyDifficulty.Normal || playerLevel is < 1 or > 49)
                return Array.Empty<byte>();

            int target = Math.Min(HighestTargetLevel, playerLevel + MonsterLevelOffset(difficulty));
            int lowest = playerLevel + LowestMonsterLevelOffset(difficulty);
            var levels = new List<byte>(3);
            for (int level = target; level >= lowest; level--)
                levels.Add((byte)level);
            return levels;
        }

        /// <summary>XP bulbs: Normal 2, Hard 4, Very Hard 8; a reroll halves them.</summary>
        public static int Bulbs(BountyDifficulty difficulty, bool rerolled)
        {
            int bulbs = difficulty switch
            {
                BountyDifficulty.Hard => 4,
                BountyDifficulty.VeryHard => 8,
                _ => 2
            };
            return rerolled ? bulbs / 2 : bulbs;
        }

        /// <summary>Reward gear level: assigned level +1, +3 or +5, never above 51.</summary>
        public static byte GearLevel(byte assignedLevel, BountyDifficulty difficulty)
        {
            int bonus = difficulty switch
            {
                BountyDifficulty.Hard => 3,
                BountyDifficulty.VeryHard => 5,
                _ => 1
            };
            return (byte)Math.Min(51, assignedLevel + bonus);
        }

        /// <summary>
        /// Outdoor bounties accept the same-named monster anywhere in the realm.
        /// On Hard and Very Hard a weaker same-named monster must not count.
        /// </summary>
        public static bool KillCounts(BountyDifficulty difficulty, int targetLevel, int killedLevel) =>
            difficulty == BountyDifficulty.Normal || killedLevel >= targetLevel - 1;

        public static string DisplayName(BountyDifficulty difficulty) => difficulty switch
        {
            BountyDifficulty.Hard => "Hard",
            BountyDifficulty.VeryHard => "Very Hard",
            _ => "Normal"
        };

        public static BountyDifficulty Parse(string saved) => saved switch
        {
            "1" => BountyDifficulty.Hard,
            "2" => BountyDifficulty.VeryHard,
            _ => BountyDifficulty.Normal
        };

        public static string Save(BountyDifficulty difficulty) => ((int)difficulty).ToString();

        /// <summary>Dialogue words after "reroll " or "update ", or before " bounty".</summary>
        public static bool TryParseChoice(string text, out BountyDifficulty difficulty)
        {
            switch (text?.Trim().ToLowerInvariant())
            {
                case "normal":
                    difficulty = BountyDifficulty.Normal;
                    return true;
                case "hard":
                    difficulty = BountyDifficulty.Hard;
                    return true;
                case "very hard":
                    difficulty = BountyDifficulty.VeryHard;
                    return true;
                default:
                    difficulty = BountyDifficulty.Normal;
                    return false;
            }
        }
    }
}

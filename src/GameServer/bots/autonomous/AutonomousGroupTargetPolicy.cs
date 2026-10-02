using System;
using System.Collections.Generic;

namespace DOL.GS
{
    /// <summary>Planning and recovery levels only; execution must not re-cap an assigned target.</summary>
    public static class AutonomousGroupTargetPolicy
    {
        public static int PreferredBonus(int groupSize) => Math.Clamp(groupSize, 2, 8) switch
        {
            <= 3 => 1,
            <= 6 => 2,
            _ => 3,
        };

        public static int PreferredLevel(int averageLevel, int groupSize, int wipePenalty = 0) =>
            Math.Max(1, averageLevel + PreferredBonus(groupSize) - Math.Max(0, wipePenalty));

        public static int PenaltyAfterWipe(int averageLevel, int groupSize, int oldPenalty, int selectedLevel)
        {
            int previousTarget = selectedLevel > 0 ? selectedLevel : PreferredLevel(averageLevel, groupSize, oldPenalty);
            return Math.Clamp(Math.Max(oldPenalty + 1,
                PreferredLevel(averageLevel, groupSize) - Math.Max(1, previousTarget - 1)), 0, 50);
        }

        // Exact preferred level first, then closest easier level. If repeated
        // deaths request a level below every XP-bearing mob, take the lowest
        // valid level available, never above the original size-based ceiling.
        // Caller has already applied realm, live-spawn, reachability and grey checks.
        public static int SelectAvailableLevel(IEnumerable<int> levels, int averageLevel, int groupSize, int wipePenalty = 0)
        {
            int preferred = PreferredLevel(averageLevel, groupSize, wipePenalty);
            int ceiling = PreferredLevel(averageLevel, groupSize);
            int best = 0;
            int lowest = int.MaxValue;
            foreach (int level in levels)
            {
                if (level < 1 || level > ceiling) continue;
                lowest = Math.Min(lowest, level);
                if (level <= preferred) best = Math.Max(best, level);
            }
            return best > 0 ? best : lowest == int.MaxValue ? 0 : lowest;
        }

        public static int SelectFixedEightManLevel(IEnumerable<int> levels, int averageLevel, int rolledBonus)
        {
            int minimum = averageLevel + 3;
            int maximum = averageLevel + Math.Clamp(rolledBonus, 3, 10);
            int best = 0;
            foreach (int level in levels ?? Array.Empty<int>())
                if (level >= minimum && level <= maximum)
                    best = Math.Max(best, level);
            return best;
        }

    }
}

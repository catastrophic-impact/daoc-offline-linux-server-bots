using System;

namespace DOL.GS
{
    /// <summary>Absolute player-character base attributes, independent of current NPC stats.</summary>
    public static class BotAttributeProgression
    {
        public static int Calculate(int racialBase, int level, eStat stat,
            eStat primary, eStat secondary, eStat tertiary, bool advanced)
        {
            int value = racialBase;
            int advancement = advanced ? 10 : 0;
            // Independent checks preserve classes whose growth roles overlap.
            if (stat == primary) value += advancement + Math.Max(0, level - 5);
            if (stat == secondary) value += advancement + (level >= 6 ? 1 + (level - 6) / 2 : 0);
            if (stat == tertiary) value += advancement + (level >= 6 ? 1 + (level - 6) / 3 : 0);
            return value;
        }

        public static byte InitialLevel(byte ownerLevel, byte requestedLevel, bool temporaryHelper)
        {
            return !temporaryHelper && requestedLevel > 0 ? requestedLevel : ownerLevel;
        }
    }
}

using System;

namespace DOL.GS
{
    /// <summary>Execution of an assigned PvE goal, separate from difficulty planning.</summary>
    public static class AutonomousPveTargetPolicy
    {
        // The caller separately checks life, region, attack legality and route.
        // Any real monster level is allowed, including grey or purple targets
        // and level 0 creatures (blue to a level 1; used by starter camps).
        public static bool IsAssignedTarget(string assignedName, string monsterName, int monsterLevel) =>
            monsterLevel >= 0 && !string.IsNullOrWhiteSpace(assignedName) &&
            string.Equals(assignedName, monsterName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Vertical gap beyond which a flying monster cannot be pulled from the bot's floor.</summary>
        public const int UnreachableFlyerHeight = 400;

        // Flying spawns such as the Gripklosa griffon gliders hover thousands of
        // units over the camp floor: melee could never reach them and pulls timed
        // out (1,195 times in one run). Low flyers (lights, elementals) still count.
        // A high flyer that attacks the bot is fought normally by its defense.
        public static bool IsUnreachableFlyer(GameNPC.eFlags flags, int monsterZ, int botZ) =>
            (flags & GameNPC.eFlags.FLYING) != 0 && Math.Abs(monsterZ - botZ) > UnreachableFlyerHeight;
    }
}

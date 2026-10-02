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
    }
}

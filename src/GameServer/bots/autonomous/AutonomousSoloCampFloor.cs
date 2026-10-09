using System;

namespace DOL.GS
{
    /// <summary>
    /// Level 50 solo grind spots. A camp used to qualify when any one of its spawns conned
    /// green, so a camp of grey monsters with one rare green spawn was valid and the bot
    /// stood waiting for that spawn. Level 50 solo bots now only count monsters of level 38
    /// or higher (the top of green, blue and up: con at 50 is grey 0-35, green 36-40, blue
    /// 41-45), need at least half of a camp's spawns and at least 3 to qualify, and never
    /// pick a lower monster at the camp (with none left, the normal empty-camp timeout moves
    /// them on). Group spots keep their own +3 to +10 rule.
    /// </summary>
    public static class AutonomousSoloCampFloor
    {
        public const int Level50MinimumMonsterLevel = 38;
        public const int MinimumQualifyingSpawns = 3;

        public static int MinimumMonsterLevel(int botLevel) => botLevel >= 50 ? Level50MinimumMonsterLevel : 0;

        public static bool Allows(int botLevel, int monsterLevel) => monsterLevel >= MinimumMonsterLevel(botLevel);

        public static bool CampQualifies(int botLevel, int qualifyingSpawns, int totalSpawns) =>
            botLevel < 50 || qualifyingSpawns >= Math.Max(MinimumQualifyingSpawns, (totalSpawns + 1) / 2);
    }
}

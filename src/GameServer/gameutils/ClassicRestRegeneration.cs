namespace DOL.GS
{
    /// <summary>One timing/stance rule for real players and NPC-backed playerbots.
    /// Native property calculators still apply level, buffs, debuffs and server modifiers.</summary>
    public static class ClassicRestRegeneration
    {
        public static int HealthAndPowerInterval(bool sitting, bool inCombat)
        {
            return (6 - (sitting ? 3 : 0) + (inCombat ? 8 : 0) - (sitting && inCombat ? 1 : 0)) * 1000;
        }

        public static int BaseEndurancePerTick(bool sitting, bool inCombat, bool moving)
        {
            return inCombat || moving ? 0 : sitting ? 4 : 1;
        }
    }
}

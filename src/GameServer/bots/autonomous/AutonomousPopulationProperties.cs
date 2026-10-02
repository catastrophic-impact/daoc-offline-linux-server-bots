using DOL.GS.ServerProperties;

namespace DOL.GS
{
    /// <summary>
    /// Safety gates for the active-only autonomous population. Defaults guarantee
    /// that installing or starting the server cannot create a bot on its own.
    /// </summary>
    public static class AutonomousPopulationProperties
    {
        [ServerProperty("autonomous_population", "population_enabled", "Master switch. False means no autonomous profiles are created or spawned. Once enabled, live bot activity does not require a human player online.", false)]
        public static bool POPULATION_ENABLED;

        [ServerProperty("autonomous_population", "active_target", "Legacy dashboard mirror of the non-retired roster size. The server always targets the complete roster.", 0)]
        public static int ACTIVE_TARGET;

        [ServerProperty("autonomous_population", "max_active_bots", "Most autonomous bots in the world at once. 0 = the whole roster. Lowering it (or turning the population off) logs the extra bots out in small batches; their characters are kept.", 0)]
        public static int MAX_ACTIVE_BOTS;

        [ServerProperty("autonomous_population", "hard_active_cap", "Legacy compatibility value only. It does not limit the autonomous population.", 0)]
        public static int HARD_ACTIVE_CAP;

        [ServerProperty("autonomous_population", "initial_cohort_size", "Recommended first observed cohort when population creation is explicitly enabled.", 3)]
        public static int INITIAL_COHORT_SIZE;

        [ServerProperty("autonomous_population", "active_only", "Reject time-skipped or offline simulated bot progression.", true)]
        public static bool ACTIVE_ONLY;

        [ServerProperty("autonomous_population", "startup_ramp_minutes", "Optional startup ramp in minutes (0 = none). When set, the first 33 percent stagger through five minutes and the rest through the remaining time. Login speed is otherwise limited by bot_logins_per_second.", 0)]
        public static int STARTUP_RAMP_MINUTES;

        [ServerProperty("autonomous_population", "bot_logins_per_second", "Most bots logged into the world per second. Smooths server load without making a small roster wait.", 8)]
        public static int BOT_LOGINS_PER_SECOND;

        [ServerProperty("autonomous_population", "allow_character_deletion", "Owner-only destructive gate. Keep false so shutdowns, restarts, and population reductions can only park bot characters.", false)]
        public static bool ALLOW_CHARACTER_DELETION;

        [ServerProperty("autonomous_population", "ai_budget_ms", "Maximum autonomous decision work per game-loop slice. Work over budget is deferred, never simulated.", 4.0)]
        public static double AI_BUDGET_MS;

        [ServerProperty("autonomous_population", "world_actor_only", "Require every progressing bot to remain a real loaded world actor.", true)]
        public static bool WORLD_ACTOR_ONLY;
    }
}

using DOL.AI.Brain;
using DOL.Logging;

namespace DOL.GS
{
    /// <summary>
    /// Tethered bosses (Summoner Roesia and about 50 other scripted encounters) take no damage while they are
    /// outside their tether range. Players stop hitting such a boss and the one it is chasing runs back toward
    /// its lair so it follows them home, then the fight resumes. Gamebots kept fighting out there: Summoner
    /// Roesia chased a raider 3,200 units from her spawn and stayed immune at 28% until the raid skipped her
    /// (run 16, 2026-10-07). This does the player thing; the boss and its mechanics are unchanged.
    /// </summary>
    public static class AutonomousTetherReset
    {
        private static readonly Logger Log = LoggerManager.Create(typeof(AutonomousTetherReset));
        private static long _nextLog;

        // Epic encounters only: ordinary monsters also carry a 4,500 tether but simply walk home when they leave it
        // (run 18 led a small hill cat home).
        public static bool IsOutOfTether(GameNPC npc) =>
            npc is IGameEpicNpc && npc is { IsAlive: true, TetherRange: > 0 } && npc.Brain is not IControlledBrain && npc.IsOutOfTetherRange;

        /// <summary>
        /// For an autonomous gamebot whose target is a boss outside its tether: stop attacking it, and if the
        /// boss is after this bot, lead it back toward its spawn point. True when the bot's turn is used.
        /// </summary>
        public static bool TryHandle(GameBot bot, GameNPC boss)
        {
            if (bot?.IsAutonomousWorldBot != true || bot.IsPlayerLedGroup || !IsOutOfTether(boss)) return false;
            if (bot.IsCasting && bot.castingComponent?.SpellHandler?.Spell?.IsHarmful == true) bot.StopCurrentSpellcast();
            bot.StopAttack();
            (bot.Brain as BotBrain)?.RemoveFromAggroList(boss);
            bot.TargetObject = null;
            // The pet backs off too, or it keeps the boss busy out there.
            if (bot.ControlledBrain?.Body?.TargetObject == boss) bot.ControlledBrain.Disengage();
            bool chased = boss.TargetObject == bot;
            if (chased)
            {
                Point3D home = boss.SpawnPoint;
                bot.PathTo(new Point3D(home.X, home.Y, home.Z), bot.MaxSpeed);
            }
            if (Log.IsInfoEnabled && GameLoop.GameLoopTime >= _nextLog)
            {
                _nextLog = GameLoop.GameLoopTime + 10_000;
                Log.Info($"AUTONOMOUS_TETHER_RESET boss=\"{boss.Name}\" region={boss.CurrentRegionID} bot={bot.Name} " +
                         $"leading={chased} fromSpawn={(int)boss.GetDistanceTo(boss.SpawnPoint)} tether={boss.TetherRange}");
            }
            return true;
        }
    }
}

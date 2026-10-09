using System.Collections.Concurrent;

namespace DOL.GS
{
    /// <summary>
    /// Dragon fight telemetry (goal 12). The dragon brains heal the dragon to full on any think where the aggro list is
    /// empty; a 270-bot raid fought Cuuldurach for over an hour (2026-10-07) with no visible progress. DRAGON_HEALTH
    /// every minute while fighting, DRAGON_RESET when a reset heal undoes real damage.
    /// </summary>
    public static class DragonDiagnostics
    {
        private static readonly DOL.Logging.Logger Log = DOL.Logging.LoggerManager.Create(typeof(DragonDiagnostics));
        private static readonly ConcurrentDictionary<GameNPC, long> NextHealthLog = new();

        public static void BeforeReset(GameNPC dragon)
        {
            if (dragon == null || !dragon.IsAlive || dragon.HealthPercent >= 95) return;
            Log.Warn($"DRAGON_RESET name=\"{dragon.Name}\" healthPercent={dragon.HealthPercent} health={dragon.Health}/{dragon.MaxHealth} " +
                     $"position={dragon.X},{dragon.Y},{dragon.Z} reason=\"aggro list empty\"");
        }

        public static void Fighting(GameNPC dragon, int aggroCount)
        {
            if (dragon == null || !dragon.IsAlive) return;
            long now = GameLoop.GameLoopTime;
            if (NextHealthLog.TryGetValue(dragon, out long next) && now < next) return;
            NextHealthLog[dragon] = now + 60_000;
            Log.Info($"DRAGON_HEALTH name=\"{dragon.Name}\" healthPercent={dragon.HealthPercent} health={dragon.Health}/{dragon.MaxHealth} aggro={aggroCount}");
        }
    }
}

using DOL.Logging;

namespace DOL.GS
{
    /// <summary>Thresholds for putting a stuck bot pet back beside its owner.</summary>
    public static class BotPetStuckRecovery
    {
        internal static readonly Logger Log = LoggerManager.Create(typeof(BotPetStuckRecovery));

        // Only when the pet has fallen well behind; close pets settle normally.
        public const float MinimumDistance = 250f;
        // Less than this much movement counts as no progress.
        public const float MinimumProgress = 48f;
        public const long StuckMilliseconds = 10_000;

        public static bool IsStuck(long stuckSince, long now) => stuckSince > 0 && now - stuckSince >= StuckMilliseconds;
    }
}

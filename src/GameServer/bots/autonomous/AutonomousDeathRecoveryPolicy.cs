using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS
{
    /// <summary>The selected fallback and its live pull filter must agree.</summary>
    public static class AutonomousDeathRecoveryPolicy
    {
        public const int FailureMemoryMilliseconds = 15 * 60_000;

        public static int NextNoExperienceDeathStreak(int previousStreak, long previousDeathTick,
            long currentTick, bool earnedExperience)
        {
            if (earnedExperience || previousDeathTick <= 0 || currentTick < previousDeathTick ||
                currentTick - previousDeathTick > FailureMemoryMilliseconds)
                return 1;
            return Math.Min(10, Math.Max(1, previousStreak) + 1);
        }

        // The first two defeats still retry promptly after recovering resources.
        // Subsequent zero-XP deaths pause briefly instead of feeding a tight
        // release -> route -> death loop indefinitely.
        public static int RetryDelayMilliseconds(int noExperienceDeathStreak) =>
            noExperienceDeathStreak < 3 ? 0 : Math.Min(120_000, (noExperienceDeathStreak - 2) * 30_000);

        public static AutonomousBotDecisionEngine.Camp[] PreferFreshTargets(
            IEnumerable<AutonomousBotDecisionEngine.Camp> candidates,
            IReadOnlyDictionary<string, long> failedTargetUntil,
            long currentTick)
        {
            AutonomousBotDecisionEngine.Camp[] available = candidates?.ToArray() ?? [];
            if (available.Length == 0 || failedTargetUntil == null || failedTargetUntil.Count == 0)
                return available;
            AutonomousBotDecisionEngine.Camp[] fresh = available.Where(camp =>
                string.IsNullOrWhiteSpace(camp.MonsterName) ||
                !failedTargetUntil.TryGetValue(camp.MonsterName, out long until) || until <= currentTick).ToArray();
            return fresh.Length > 0 ? fresh : available;
        }

        public static ConColor EncounterMaximum(ConColor requested, ConColor natural,
            ConColor? selectedFallback, bool dynamicGroup)
        {
            if (dynamicGroup || !selectedFallback.HasValue || selectedFallback.Value <= ConColor.GREY)
                return requested;
            return (ConColor)System.Math.Clamp((int)selectedFallback.Value, (int)ConColor.GREEN, (int)natural);
        }

        public static bool IsEligible(ConColor target, ConColor maximum) =>
            target > ConColor.GREY && target <= maximum;
    }
}

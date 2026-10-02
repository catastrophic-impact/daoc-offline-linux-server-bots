using System;

namespace DOL.GS;

public enum eAutonomousFidelity
{
    Efficient,
    Standard,
    NearbyHuman,
}

/// <summary>
/// Scales decision detail by human proximity while retaining real actor combat,
/// movement and progression at every tier.
/// </summary>
public static class AutonomousFidelityPolicy
{
    public static eAutonomousFidelity Select(bool anyHumanOnline, bool sameZoneAsHuman, int nearestHumanDistance)
    {
        if (sameZoneAsHuman && nearestHumanDistance is >= 0 and <= 6000)
            return eAutonomousFidelity.NearbyHuman;
        if (sameZoneAsHuman)
            return eAutonomousFidelity.Standard;
        return eAutonomousFidelity.Efficient;
    }

    public static int IntervalMilliseconds(eAutonomousThinkMode mode, eAutonomousFidelity fidelity)
        => IntervalMilliseconds(mode, fidelity, 0);

    public static int IntervalMilliseconds(eAutonomousThinkMode mode, eAutonomousFidelity fidelity, int onlineBots)
    {
        int baseline = AutonomousAiBudget.IntervalMilliseconds(mode);
        if (mode == eAutonomousThinkMode.Combat)
            return fidelity switch
            {
                eAutonomousFidelity.NearbyHuman => baseline,
                eAutonomousFidelity.Standard => 350,
                _ => 500,
            };

        double multiplier = fidelity switch
        {
            eAutonomousFidelity.NearbyHuman => 1,
            eAutonomousFidelity.Standard => 1.6,
            _ => 2.6,
        };
        int interval = Math.Max(baseline, (int)Math.Round(baseline * multiplier));
        // At large populations, slow only background planning. Movement remains
        // continuous in NpcMovementComponent and attacks wake the brain at once.
        // Even at/above the 1,500-bot target no actor waits more than seconds.
        double pressure = Math.Clamp((onlineBots - 300) / 1200d, 0, 1);
        double populationMultiplier = mode switch
        {
            eAutonomousThinkMode.Travel => 1 + 0.80 * pressure,
            eAutonomousThinkMode.Planning => 1 + 0.60 * pressure,
            eAutonomousThinkMode.Resting => 1 + 0.40 * pressure,
            _ => 1,
        };
        int maximum = mode switch
        {
            eAutonomousThinkMode.Travel => 5_000,
            eAutonomousThinkMode.Planning => 12_000,
            eAutonomousThinkMode.Resting => 7_000,
            _ => interval,
        };
        return Math.Min(maximum, (int)Math.Round(interval * populationMultiplier));
    }

    public static int Stagger(int intervalMilliseconds, long stableBotId)
    {
        if (intervalMilliseconds <= 0)
            return intervalMilliseconds;
        int spread = Math.Max(1, intervalMilliseconds / 8);
        long positive = stableBotId == long.MinValue ? 0 : Math.Abs(stableBotId);
        return intervalMilliseconds + (int)(positive % spread);
    }

    public static int CandidateLimit(eAutonomousFidelity fidelity) => fidelity switch
    {
        eAutonomousFidelity.NearbyHuman => 24,
        eAutonomousFidelity.Standard => 12,
        _ => 6,
    };
}

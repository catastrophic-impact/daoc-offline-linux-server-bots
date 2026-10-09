namespace DOL.GS;

/// <summary>When a realm event party member cannot route to its rally hub.</summary>
public static class RealmRaidHubEscape
{
    // Three failed attempts, one minute apart, before the member leaves the raid party.
    public const int FailuresBeforeEscape = 3;

    public static bool ShouldEscape(int consecutiveFailures) => consecutiveFailures >= FailuresBeforeEscape;
}

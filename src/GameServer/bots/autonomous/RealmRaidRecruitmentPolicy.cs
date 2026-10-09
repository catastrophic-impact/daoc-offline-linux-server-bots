using System;

namespace DOL.GS;

public static class RealmRaidRecruitmentPolicy
{
    public const int MaximumBots = 300;
    public const int MaximumParties = (MaximumBots + 7) / 8;
    public const int AutonomousMinimumPresent = 200;
    public const int MinimumParties = AutonomousMinimumPresent / 8;
    public const long AutonomousMinimumStagingMilliseconds = 15 * 60_000L;
    public const long AutonomousStagingLimitMilliseconds = 90 * 60_000L;
    public const long BattleMilliseconds = 4 * 60 * 60_000L;
    public const double NewEventChance = .20;
    public const double JoinExistingChance = .95;

    // An automatic rally that has waited most of its window with a nearly full
    // roster goes in rather than failing outright. Caer Sidi failed twice in one
    // run with 196 and 186 of the 200 required bots at its entrance.
    public const long AutonomousLateStartMilliseconds = 75 * 60_000L;
    public const int AutonomousLateStartMinimumPresent = 180;

    public static bool Eligible(int level, bool autonomous, bool temporary, bool playerLed) =>
        level == 50 && autonomous && !temporary && !playerLed;

    public static bool CanOpenEvent(bool forced, bool sameRealmEvent, bool sameEncounterEvent) =>
        !sameEncounterEvent && (forced || !sameRealmEvent);

    // Forced raids (started by the player) go in as soon as 200 are staged and, for a dragon,
    // it has landed. They have no staging or battle time limit: they run until the encounter
    // is defeated, the player presses Stop event, or the server stops.
    public static bool Ready(bool forced, long elapsed, int present, bool landed) =>
        elapsed >= (forced ? 0 : AutonomousMinimumStagingMilliseconds) &&
        (present >= AutonomousMinimumPresent ||
         !forced && elapsed >= AutonomousLateStartMilliseconds && present >= AutonomousLateStartMinimumPresent) &&
        landed;

    public static bool DepartHub(bool alreadyDeparted, int presentBots) =>
        alreadyDeparted || presentBots >= AutonomousMinimumPresent;

    public static bool StagingExpired(bool forced, long elapsed, int present = 0, bool landed = true) =>
        !forced && elapsed >= AutonomousStagingLimitMilliseconds &&
        !(present >= AutonomousMinimumPresent && !landed);

    public static bool BattleExpired(bool forced, long now, long deadline) => !forced && now >= deadline;

    /// <summary>A raid party that lost members (but still exists) takes replacements up to its size when it joined.</summary>
    public static bool NeedsReplacement(int target, int members) => members > 0 && members < target;
}

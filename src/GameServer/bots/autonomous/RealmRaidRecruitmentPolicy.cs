using System;

namespace DOL.GS;

public static class RealmRaidRecruitmentPolicy
{
    public const int MaximumBots = 300;
    public const int MaximumParties = (MaximumBots + 7) / 8;
    public const int AutonomousMinimumPresent = 200;
    public const int MinimumParties = AutonomousMinimumPresent / 8;
    public const long ForcedStagingMilliseconds = 45 * 60_000L;
    public const long AutonomousMinimumStagingMilliseconds = 15 * 60_000L;
    public const long AutonomousStagingLimitMilliseconds = 90 * 60_000L;
    public const long ForcedStagingLimitMilliseconds = 90 * 60_000L;
    public const long BattleMilliseconds = 4 * 60 * 60_000L;
    public const double NewEventChance = .20;
    public const double JoinExistingChance = .95;

    public static bool Eligible(int level, bool autonomous, bool temporary, bool playerLed) =>
        level == 50 && autonomous && !temporary && !playerLed;

    public static bool CanOpenEvent(bool forced, bool sameRealmEvent, bool sameEncounterEvent) =>
        !sameEncounterEvent && (forced || !sameRealmEvent);

    public static bool Ready(bool forced, long elapsed, int present, bool landed) =>
        elapsed >= (forced ? ForcedStagingMilliseconds : AutonomousMinimumStagingMilliseconds) &&
        present >= AutonomousMinimumPresent && landed;

    public static bool DepartHub(bool alreadyDeparted, int presentBots) =>
        alreadyDeparted || presentBots >= AutonomousMinimumPresent;

    public static bool StagingExpired(bool forced, long elapsed, int present = 0, bool landed = true) =>
        elapsed >= (forced ? ForcedStagingLimitMilliseconds : AutonomousStagingLimitMilliseconds) &&
        !(present >= AutonomousMinimumPresent && !landed);
}

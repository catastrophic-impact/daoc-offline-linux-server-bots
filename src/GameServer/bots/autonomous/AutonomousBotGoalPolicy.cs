using System;
using System.IO;
using OfflineDaoc.Configuration;

namespace DOL.GS;

public static class AutonomousBotGoalPolicy
{
    public static BotGoalSettings Settings { get; private set; } = BotGoalSettings.Defaults;
    public static bool IsConfigured { get; private set; }

    // One startup read only. Never poll settings or query the database on AI turns.
    public static void Initialize(string serverDirectory)
    {
        string path = Path.Combine(serverDirectory, BotGoalSettings.FileName);
        Settings = BotGoalSettings.Load(path); // Reject corruption rather than silently ignoring 0% exclusions.
        IsConfigured = File.Exists(path);
    }

    public static eAutonomousObjectiveKind Choose(int level, Random random = null, bool excludeGroup = false) =>
        (eAutonomousObjectiveKind)Settings.ForLevel(level).Choose((random ?? Random.Shared).NextDouble(), excludeGroup);

    public static eAutonomousObjectiveKind EnsureAllowed(int level, eAutonomousObjectiveKind kind) =>
        Settings.ForLevel(level).Allows((int)kind) ? kind : Choose(level);

    // Called before an autonomous actor can enter the world. Keep inventory,
    // position, XP and valid saved task clocks; discard only a now-disabled task.
    public static bool ReconcileSavedAssignment(OfflineWorldBotRecord record)
    {
        if (!IsConfigured || record == null) return false;
        bool changed = false;
        if (!string.IsNullOrEmpty(record.ObjectiveRvrEligibleUtc))
        {
            record.ObjectiveRvrEligibleUtc = string.Empty;
            changed = true;
        }
        if (!AutonomousObjectiveAssignments.IsBetweenPveTasks(record) &&
            !Settings.ForLevel(record.Level).Allows((int)AutonomousObjectiveAssignments.Parse(record.ObjectiveKind)))
        {
            record.ObjectiveKind = Choose(record.Level).ToString();
            record.ObjectiveAssignmentId = string.Empty;
            record.ObjectiveAssignedUtc = record.ObjectiveExpiresUtc = string.Empty;
            record.CurrentCampId = record.TargetName = record.TravelDestination = string.Empty;
            record.ObjectivePhase = "Choosing a goal using Bot Goals Setting";
            changed = true;
        }
        if (changed) record.Dirty = true;
        return changed;
    }
}

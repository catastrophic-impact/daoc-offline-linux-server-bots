using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS;

public enum eAutonomousRvrObjective
{
    Regroup,
    HuntEnemy,
    DefendKeep,
    AssaultKeep,
}

/// <summary>
/// Selects concrete live RvR objectives for an autonomous realm force.  It does
/// not award RP, change keep ownership, revive bots, or manufacture siege state;
/// the executor must travel and fight through the normal world systems.
/// Selection is independent of whether a human player is online.
/// </summary>
public static class AutonomousRvrDirector
{
    public sealed record Objective(
        string Id,
        string Name,
        eAutonomousRvrObjective Kind,
        eRealm OwningRealm,
        ushort RegionId,
        bool Reachable,
        int EnemyCount,
        int FriendlyCount,
        int GuardStrength,
        int DoorsRemaining,
        bool UnderAttack,
        double TravelMinutes);

    public sealed record Warband(
        eRealm Realm,
        int MemberCount,
        int AverageLevel,
        int HealerCount,
        int RecentWipes,
        bool InCombat);

    public sealed record Choice(eAutonomousRvrObjective Kind, string TargetId, string Reason);

    public static Choice Choose(Warband warband, IReadOnlyCollection<Objective> objectives)
    {
        if (warband.MemberCount < 2 || warband.RecentWipes >= 3)
            return new(eAutonomousRvrObjective.Regroup, string.Empty, "The force is too small or has wiped repeatedly; regroup before returning.");

        Objective defense = objectives
            .Where(objective => objective.Reachable && objective.Kind == eAutonomousRvrObjective.DefendKeep &&
                                objective.OwningRealm == warband.Realm && objective.UnderAttack)
            .OrderByDescending(objective => objective.EnemyCount - objective.FriendlyCount)
            .ThenBy(objective => objective.TravelMinutes)
            .FirstOrDefault();
        if (defense != null)
            return new(eAutonomousRvrObjective.DefendKeep, defense.Id, $"Defend {defense.Name}, which is under live enemy attack.");

        Objective vulnerableKeep = objectives
            .Where(objective => objective.Reachable && objective.Kind == eAutonomousRvrObjective.AssaultKeep &&
                                objective.OwningRealm != eRealm.None && objective.OwningRealm != warband.Realm &&
                                CanAttemptKeep(warband, objective))
            .OrderBy(objective => AssaultCost(objective))
            .ThenBy(objective => objective.TravelMinutes)
            .FirstOrDefault();
        if (vulnerableKeep != null)
            return new(eAutonomousRvrObjective.AssaultKeep, vulnerableKeep.Id, $"Attack {vulnerableKeep.Name}, break its real defenses, kill its guards and lord, and attempt a normal keep capture.");

        Objective enemy = objectives
            .Where(objective => objective.Reachable && objective.Kind == eAutonomousRvrObjective.HuntEnemy &&
                                objective.OwningRealm != warband.Realm &&
                                objective.EnemyCount > 0 && objective.EnemyCount <= Math.Max(2, warband.MemberCount + 2))
            .OrderBy(objective => Math.Abs(objective.EnemyCount - warband.MemberCount))
            .ThenBy(objective => objective.TravelMinutes)
            .FirstOrDefault();
        if (enemy != null)
            return new(eAutonomousRvrObjective.HuntEnemy, enemy.Id, $"Engage the live enemy force near {enemy.Name}.");

        return new(eAutonomousRvrObjective.Regroup, string.Empty, "No reachable, survivable live RvR objective is presently available.");
    }

    internal static bool CanAttemptKeep(Warband warband, Objective keep)
    {
        if (warband.AverageLevel < 40 || warband.MemberCount < 5 || warband.HealerCount < 1)
            return false;

        int effectiveDefenders = keep.EnemyCount + (keep.GuardStrength + 1) / 2 + Math.Max(0, keep.DoorsRemaining - 1);
        int effectiveAttackers = warband.MemberCount + Math.Min(3, warband.HealerCount);
        return effectiveAttackers >= effectiveDefenders;
    }

    private static int AssaultCost(Objective keep) =>
        keep.EnemyCount * 4 + keep.GuardStrength * 2 + keep.DoorsRemaining * 3 - keep.FriendlyCount * 2;
}

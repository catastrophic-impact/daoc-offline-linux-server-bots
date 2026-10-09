using DOL.GS.Styles;
using System;
using System.Numerics;

namespace DOL.GS;

/// <summary>
/// Small, deterministic policy for the Classic Savage's instant, health-cost
/// self buffs. It does not alter the native effects, values, durations, costs,
/// styles, abilities, or player controls.
/// </summary>
public static class SavageBotCombatPolicy
{
    public const int FailedSoloPullRouteRetryMilliseconds = 6_000;
    public const int FailedMeleePullRetryMilliseconds = 90_000;
    public const int MeleePullProgressDistance = 64;
    public const int MeleePullRetryAfterNoProgressMilliseconds = 8_000;
    public const int MeleePullGiveUpAfterNoProgressMilliseconds = 20_000;
    // The dungeon brain permits a committed approach for 30 seconds. A moving
    // Savage gets longer than that, but an untouched pull must still end.
    public const int MeleePullMaximumMilliseconds = 60_000;

    public enum MeleePullDecision { Continue, RetryApproach, GiveUp }

    // Every other class uses the same untouched-pull watch with a little more
    // time: casters and pet classes open from range and pets must walk in.
    // Without it a caster that could neither cast nor close range (resting,
    // a cast that never finished, out of power while nothing attacks it)
    // stood on its target until the fifteen-minute stuck watchdog.
    public const int PullRetryAfterNoProgressMilliseconds = 12_000;
    public const int PullGiveUpAfterNoProgressMilliseconds = 30_000;
    public const int PullMaximumMilliseconds = 75_000;

    public static bool MustMeleePull(eCharacterClass characterClass) =>
        characterClass == eCharacterClass.Savage;

    public static MeleePullDecision EvaluatePull(eCharacterClass characterClass, long nowTick, long startedTick,
        long lastProgressTick, bool retriedApproach)
    {
        if (MustMeleePull(characterClass))
            return EvaluateMeleePull(nowTick, startedTick, lastProgressTick, retriedApproach);
        if (nowTick < startedTick || nowTick < lastProgressTick ||
            nowTick - startedTick >= PullMaximumMilliseconds ||
            nowTick - lastProgressTick >= PullGiveUpAfterNoProgressMilliseconds)
            return MeleePullDecision.GiveUp;
        if (!retriedApproach && nowTick - lastProgressTick >= PullRetryAfterNoProgressMilliseconds)
            return MeleePullDecision.RetryApproach;
        return MeleePullDecision.Continue;
    }

    /// <summary>
    /// A spell handler that is still attached long after its cast should have
    /// ended blocks both movement (the follow tick stops a casting NPC) and new
    /// spell decisions (the bot waits for the active cast). Focus, pulse and
    /// concentration spells legitimately stay attached and are never stale.
    /// </summary>
    public static bool IsStaleCast(Spell spell, long attachedSinceTick, long nowTick) =>
        spell != null && !spell.IsFocus && !spell.IsPulsing && !spell.IsConcentration &&
        nowTick - attachedSinceTick >= Math.Max(8_000, spell.CastTime + 4_000L);   // CastTime is milliseconds

    public static bool IsMeleePullTargetValid(bool alive, bool active, bool sameRegion,
        bool sameCamp) => alive && active && sameRegion && sameCamp;

    public static bool HasMeleePullContact(long initialAttackTick, long attackTick,
        long initialAttackedTick, long attackedTick, int initialTargetHealth,
        int targetHealth) =>
        attackTick > initialAttackTick || attackedTick > initialAttackedTick ||
        targetHealth < initialTargetHealth;

    public static bool MadeMeleePullProgress(Vector3 previousPosition, Vector3 currentPosition,
        int previousDistance, int currentDistance) =>
        Vector3.DistanceSquared(previousPosition, currentPosition) >=
            MeleePullProgressDistance * MeleePullProgressDistance ||
        previousDistance - currentDistance >= MeleePullProgressDistance;

    public static MeleePullDecision EvaluateMeleePull(long nowTick, long startedTick,
        long lastProgressTick, bool retriedApproach)
    {
        if (nowTick < startedTick || nowTick < lastProgressTick ||
            nowTick - startedTick >= MeleePullMaximumMilliseconds ||
            nowTick - lastProgressTick >= MeleePullGiveUpAfterNoProgressMilliseconds)
            return MeleePullDecision.GiveUp;
        if (!retriedApproach &&
            nowTick - lastProgressTick >= MeleePullRetryAfterNoProgressMilliseconds)
            return MeleePullDecision.RetryApproach;
        return MeleePullDecision.Continue;
    }

    // A normal short patrol step must not force the same failed native path
    // probe on the next brain tick. A materially new approach still retries.
    private const int FailedSoloPullOriginMovement = 512;
    private const int FailedSoloPullTargetMovement = 64;

    public readonly record struct FailedSoloPullRoute(long FailedAtTick, ushort RegionId,
        int OriginZoneId, int TargetZoneId, Vector3 Origin, Vector3 Target);

    // Only cache a failed native corridor. A reachable target is always
    // rechecked before a new pull, and movement or a zone change retries a
    // failed path immediately instead of trusting an old negative answer.
    public static bool ShouldDelayFailedSoloPullRetry(FailedSoloPullRoute failure,
        long nowTick, ushort regionId, int originZoneId, int targetZoneId,
        Vector3 origin, Vector3 target) =>
        nowTick >= failure.FailedAtTick &&
        nowTick - failure.FailedAtTick < FailedSoloPullRouteRetryMilliseconds &&
        failure.RegionId == regionId &&
        failure.OriginZoneId == originZoneId &&
        failure.TargetZoneId == targetZoneId &&
        Vector3.DistanceSquared(failure.Origin, origin) <=
            FailedSoloPullOriginMovement * FailedSoloPullOriginMovement &&
        Vector3.DistanceSquared(failure.Target, target) <=
            FailedSoloPullTargetMovement * FailedSoloPullTargetMovement;

    // Ordinary outdoor melee camps already use the live-target search and
    // watchdog used by Warriors and Berserkers. Requiring a reversible dungeon
    // corridor for every Savage pull rejects otherwise usable outdoor spawns,
    // especially near the edge of a historical 5,000-unit camp. Keep the
    // stricter route check where the assigned camp actually needs it.
    public static bool NeedsVerifiedSoloPullRoute(eCharacterClass characterClass,
        bool dynamicGroup, bool isDungeon, bool isAuditedOutdoorCamp) =>
        characterClass == eCharacterClass.Savage && !dynamicGroup &&
        (isDungeon || isAuditedOutdoorCamp);

    public static bool IsInstantCombatAction(Spell spell) => spell != null && spell.CastTime == 0 &&
        spell.SpellType is eSpellType.SavageEnduranceHeal or
            eSpellType.SavageEvadeBuff or eSpellType.SavageParryBuff or
            eSpellType.SavageCombatSpeedBuff or eSpellType.SavageDPSBuff or
            eSpellType.SavageSlashResistanceBuff or eSpellType.SavageCrushResistanceBuff or
            eSpellType.SavageThrustResistanceBuff;

    public static bool MayAttackDuringActiveCast(eCharacterClass characterClass, Spell activeSpell) =>
        characterClass == eCharacterClass.Savage && IsInstantCombatAction(activeSpell);

    /// <summary>
    /// Savage self-buffs are native instant, health-cost combat actions.  The
    /// asynchronous casting queue briefly reports them as pending/active even
    /// though they must not cancel a weapon swing or melee approach.  Unknown
    /// requests and real cast-time spells retain the normal stop-attack rule.
    /// </summary>
    public static bool ContinueMeleeAfterSpell(eCharacterClass characterClass, bool spellAction,
        bool isCasting, Spell activeSpell, bool hasPendingCast, Spell pendingSpell)
    {
        if (!spellAction || characterClass != eCharacterClass.Savage)
            return false;
        if (isCasting && !MayAttackDuringActiveCast(characterClass, activeSpell))
            return false;
        if (hasPendingCast && !IsInstantCombatAction(pendingSpell))
            return false;
        return true;
    }

    public static int BuffPriority(eSpellType type) => type switch
    {
        eSpellType.SavageDPSBuff => 0,
        eSpellType.SavageCombatSpeedBuff => 1,
        eSpellType.SavageEvadeBuff => 2,
        eSpellType.SavageParryBuff => 3,
        eSpellType.SavageCrushResistanceBuff => 4,
        eSpellType.SavageSlashResistanceBuff => 5,
        eSpellType.SavageThrustResistanceBuff => 6,
        _ => 100,
    };

    // Native Savages can trade health for endurance. Use it only when the
    // endurance shortage is material and there is a wide health reserve; the
    // spell handler retains the authored level-scaled value and health cost.
    public static bool ShouldUseEnduranceHeal(int healthPercent, int endurancePercent) =>
        healthPercent >= 75 && endurancePercent <= 30;

    // Each Savage buff lasts 15 seconds and costs 5% health. Bots kept up to four
    // running, two even at 55% health: up to 40-80% health a minute, so level-50
    // Savages killed at half the rate of Berserkers and Blademasters and died the
    // most (Oct 3-5). Now: the damage and attack-speed staples from 70% health,
    // evasion as well from 90%, resist buffs never, nothing below 70%.
    public const int MinimumBuffHealthPercent = 70;
    public const int ExtraBuffHealthPercent = 90;

    public static bool ShouldUseBuff(eSpellType type, int healthPercent, int activeBuffs)
    {
        int limit = healthPercent >= ExtraBuffHealthPercent ? 3 : 2;
        if (healthPercent < MinimumBuffHealthPercent || activeBuffs >= limit)
            return false;

        return BuffPriority(type) < limit;
    }

    // Never pay health to finish a monster that is nearly dead.
    public const int MinimumTargetHealthPercent = 35;

    public static bool ShouldUseBuff(eSpellType type, int healthPercent, int activeBuffs, int level, int targetHealthPercent) =>
        targetHealthPercent >= MinimumTargetHealthPercent && ShouldUseBuff(type, healthPercent, activeBuffs, level);

    public const int LowLevelSavageBuffLevel = 10;

    // Below level 10 a Savage's health pool is too small to pay for several
    // buffs: allow only the strongest one (the DPS buff) at a time.
    public static bool ShouldUseBuff(eSpellType type, int healthPercent, int activeBuffs, int level) =>
        level < LowLevelSavageBuffLevel
            ? type == eSpellType.SavageDPSBuff && activeBuffs == 0 && healthPercent >= 70
            : ShouldUseBuff(type, healthPercent, activeBuffs);

    public static bool SameBuffFamily(Spell requested, Spell active) =>
        requested != null && active != null && requested.SpellType == active.SpellType;

    public static bool IsReliableAnytimeStyle(Style style) => style != null &&
        style.OpeningRequirementType == Style.eOpening.Offensive &&
        style.OpeningRequirementValue == 0 &&
        style.AttackResultRequirement == Style.eAttackResultRequirement.Any;

    // Savage lines contain low- or zero-growth utility styles above stronger
    // anytime attacks. "Highest level" alone repeatedly selected those weak
    // attacks. Native growth, endurance, procs and hit resolution remain intact.
    public static double StylePriority(Style style) => style?.GrowthRate ?? double.MinValue;

    public static bool NeedsWeaponTrainingRepair(int weaponLevel, int savageryLevel) =>
        weaponLevel < savageryLevel;
}

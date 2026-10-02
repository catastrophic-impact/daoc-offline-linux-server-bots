using System;
using System.Linq;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

/// <summary>
/// Policies added after the 2026-09-29 overnight audit: every class's pull is
/// watched, stale casts are cleared, repeatedly unroutable camps are benched,
/// low-level Savages pay for at most one health-cost buff, and solo bots need
/// level 25 and blue-or-easier targets for Darkness Falls.
/// </summary>
[TestFixture]
public sealed class UT_OvernightAuditFixes
{
    [SetUp]
    public void SetUp() => AutonomousCampRouteQuarantine.ResetForTests();

    [Test]
    public void SavagePullTimingIsUnchanged()
    {
        Assert.That(SavageBotCombatPolicy.EvaluatePull(eCharacterClass.Savage, 8_000, 0, 0, false),
            Is.EqualTo(SavageBotCombatPolicy.EvaluateMeleePull(8_000, 0, 0, false)));
        Assert.That(SavageBotCombatPolicy.EvaluatePull(eCharacterClass.Savage, 20_000, 0, 0, true),
            Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.GiveUp));
    }

    [TestCase(eCharacterClass.Wizard)]
    [TestCase(eCharacterClass.Necromancer)]
    [TestCase(eCharacterClass.Warrior)]
    public void OtherClassesRetryThenGiveUpOnAnUntouchedPull(eCharacterClass cls)
    {
        Assert.That(SavageBotCombatPolicy.EvaluatePull(cls, 11_000, 0, 0, false),
            Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.Continue), "casters get time to open from range");
        Assert.That(SavageBotCombatPolicy.EvaluatePull(cls, 12_000, 0, 0, false),
            Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.RetryApproach));
        Assert.That(SavageBotCombatPolicy.EvaluatePull(cls, 29_000, 0, 0, true),
            Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.Continue));
        Assert.That(SavageBotCombatPolicy.EvaluatePull(cls, 30_000, 0, 0, true),
            Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.GiveUp));
        // Steady approach progress keeps the pull alive, but never past the cap.
        Assert.That(SavageBotCombatPolicy.EvaluatePull(cls, 74_000, 0, 70_000, true),
            Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.Continue));
        Assert.That(SavageBotCombatPolicy.EvaluatePull(cls, 75_000, 0, 74_000, true),
            Is.EqualTo(SavageBotCombatPolicy.MeleePullDecision.GiveUp));
    }

    private static Spell SpellWith(double castSeconds, bool focus = false, int frequency = 0) => new(new DbSpell
    {
        SpellID = 1, Name = "test", Type = "DirectDamage", Target = "Enemy", CastTime = castSeconds,
        IsFocus = focus, Frequency = frequency, Damage = 10
    }, 1);

    [Test]
    public void OnlyCastsLongPastTheirCastTimeAreStale()
    {
        Spell bolt = SpellWith(3.0);
        Assert.That(SavageBotCombatPolicy.IsStaleCast(bolt, 0, 7_999), Is.False);
        Assert.That(SavageBotCombatPolicy.IsStaleCast(bolt, 0, 8_000), Is.True);
        Spell slow = SpellWith(6.0);
        Assert.That(SavageBotCombatPolicy.IsStaleCast(slow, 0, 9_999), Is.False, "cast time + 4 s");
        Assert.That(SavageBotCombatPolicy.IsStaleCast(slow, 0, 10_000), Is.True);
        Assert.That(SavageBotCombatPolicy.IsStaleCast(SpellWith(3.0, focus: true), 0, 600_000), Is.False,
            "focus spells legitimately stay attached");
        Assert.That(SavageBotCombatPolicy.IsStaleCast(null, 0, 600_000), Is.False);
    }

    [Test]
    public void OneBotRepeatingAFailureNeverBenchesACamp()
    {
        DateTime now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 10; i++)
            Assert.That(AutonomousCampRouteQuarantine.ReportFailure("camp-a", 7, now.AddMinutes(i)), Is.False);
        Assert.That(AutonomousCampRouteQuarantine.IsQuarantined("camp-a", now.AddMinutes(10)), Is.False);
    }

    [Test]
    public void ThreeDifferentBotsBenchACampForTwoHours()
    {
        DateTime now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.That(AutonomousCampRouteQuarantine.ReportFailure("camp-b", 1, now), Is.False);
        Assert.That(AutonomousCampRouteQuarantine.ReportFailure("camp-b", 2, now.AddMinutes(5)), Is.False);
        Assert.That(AutonomousCampRouteQuarantine.ReportFailure("camp-b", 3, now.AddMinutes(10)), Is.True);
        Assert.That(AutonomousCampRouteQuarantine.IsQuarantined("camp-b", now.AddMinutes(11)), Is.True);
        Assert.That(AutonomousCampRouteQuarantine.IsQuarantined("camp-b", now.AddMinutes(10).AddHours(2)), Is.False);
        Assert.That(AutonomousCampRouteQuarantine.IsQuarantined("other-camp", now.AddMinutes(11)), Is.False);
    }

    [Test]
    public void FailuresOutsideTheWindowDoNotAccumulate()
    {
        DateTime now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        AutonomousCampRouteQuarantine.ReportFailure("camp-c", 1, now);
        AutonomousCampRouteQuarantine.ReportFailure("camp-c", 2, now.AddMinutes(40));
        Assert.That(AutonomousCampRouteQuarantine.ReportFailure("camp-c", 3, now.AddMinutes(45)), Is.False,
            "the first failure is older than thirty minutes");
    }

    [TestCase(1)]
    [TestCase(9)]
    public void LowLevelSavagesUseOnlyOneHealthCostBuff(int level)
    {
        Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageDPSBuff, 100, 0, level), Is.True);
        Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageDPSBuff, 100, 1, level), Is.False);
        Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageCombatSpeedBuff, 100, 0, level), Is.False);
        Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageDPSBuff, 69, 0, level), Is.False);
    }

    [Test]
    public void SavagesFromLevelTenKeepTheExistingBuffRule()
    {
        foreach ((eSpellType type, int hp, int active) in new[]
                 {
                     (eSpellType.SavageDPSBuff, 70, 0), (eSpellType.SavageEvadeBuff, 70, 1),
                     (eSpellType.SavageEvadeBuff, 90, 2), (eSpellType.SavageDPSBuff, 50, 0)
                 })
            Assert.That(SavageBotCombatPolicy.ShouldUseBuff(type, hp, active, 10),
                Is.EqualTo(SavageBotCombatPolicy.ShouldUseBuff(type, hp, active)));
    }

    [Test]
    public void SoloDarknessFallsNeedsLevelTwentyFiveAndBlueOrEasier()
    {
        Assert.That(AutonomousDarknessFallsPolicy.SoloMinimumLevel, Is.EqualTo(25));
        Assert.That(ConColor.BLUE <= AutonomousDarknessFallsPolicy.SoloMaximumCon, Is.True);
        Assert.That(ConColor.GREEN <= AutonomousDarknessFallsPolicy.SoloMaximumCon, Is.True);
        Assert.That(ConColor.YELLOW <= AutonomousDarknessFallsPolicy.SoloMaximumCon, Is.False);
    }

    [Test]
    public void LevelZeroCreaturesAreBlueToALevelOneBot()
    {
        Assert.That(ConLevels.GetConColor(ConLevels.GetConLevel(1, 0)), Is.EqualTo(ConColor.BLUE));
        Assert.That(ConLevels.GetConColor(ConLevels.GetConLevel(1, 0)) >= ConColor.GREEN, Is.True,
            "starter camps pass the planner's green-or-better XP filter");
    }

    [Test]
    public void FullEightIsStillPreferredWhenEveryRoleIsAvailable()
    {
        Assert.That(AutonomousBotGroupCoordinator.PveFormationSize(eCharacterClass.Warrior,
            eCharacterClass.Thane, eCharacterClass.Healer, eCharacterClass.Shaman, eCharacterClass.Skald,
            eCharacterClass.Berserker, eCharacterClass.Savage, eCharacterClass.Hunter), Is.EqualTo(8));
    }

    [Test]
    public void OneHealerAndNoBufferStillFormsASixBotParty()
    {
        Assert.That(AutonomousBotGroupCoordinator.PveFormationSize(eCharacterClass.Warrior,
            eCharacterClass.Thane, eCharacterClass.Healer, eCharacterClass.Berserker, eCharacterClass.Savage,
            eCharacterClass.Hunter, eCharacterClass.Runemaster, eCharacterClass.Spiritmaster), Is.EqualTo(6));
    }

    [Test]
    public void SmallestPartyIsFiveWithTankHealerAndAttackers()
    {
        Assert.That(AutonomousBotGroupCoordinator.PveFormationSize(eCharacterClass.Warrior,
            eCharacterClass.Healer, eCharacterClass.Berserker, eCharacterClass.Savage, eCharacterClass.Hunter),
            Is.EqualTo(5));
        Assert.That(AutonomousBotGroupCoordinator.PveFormationSize(eCharacterClass.Warrior,
            eCharacterClass.Healer, eCharacterClass.Berserker, eCharacterClass.Savage), Is.Zero, "four is too few");
    }

    [Test]
    public void NoPartyFormsWithoutAHealerOrATank()
    {
        Assert.That(AutonomousBotGroupCoordinator.PveFormationSize(eCharacterClass.Warrior,
            eCharacterClass.Thane, eCharacterClass.Berserker, eCharacterClass.Savage, eCharacterClass.Hunter,
            eCharacterClass.Runemaster, eCharacterClass.Skald), Is.Zero);
        Assert.That(AutonomousBotGroupCoordinator.PveFormationSize(eCharacterClass.Healer,
            eCharacterClass.Shaman, eCharacterClass.Berserker, eCharacterClass.Savage, eCharacterClass.Hunter,
            eCharacterClass.Runemaster), Is.Zero);
    }

    [TestCase(5, 6)] [TestCase(6, 6)] [TestCase(7, 8)] [TestCase(8, 10)]
    public void SmallerPartiesGetAGentlerTopTargetLevel(int size, int highest)
    {
        int[] rolls = Enumerable.Range(0, 200)
            .Select(seed => AutonomousBotGroupCoordinator.RollPreferredLevelBonus(size, new Random(seed))).ToArray();
        Assert.That(rolls.Min(), Is.EqualTo(3));
        Assert.That(rolls.Max(), Is.EqualTo(highest));
    }
}

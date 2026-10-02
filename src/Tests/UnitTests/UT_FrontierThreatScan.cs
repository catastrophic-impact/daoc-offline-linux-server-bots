using System;
using System.Linq;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

public class UT_FrontierThreatScan
{
    [Test]
    public void DefenderChoosesCombatantsBeforeEvenAVisibleRam()
    {
        var scan = new AutonomousFrontierThreatPolicy();
        int engineChecks = 0;
        Assert.That(scan.VisiblePriority(new[] { "player", "companion", "pet" }, new[] { "ram" }, target =>
        { if (target == "ram") engineChecks++; return true; }), Is.EqualTo(new[] { "player", "companion", "pet" }));
        Assert.That(engineChecks, Is.Zero);
    }

    [Test]
    public void DefenderRotatesPastBlockedCombatantsBeforeConsideringSiege()
    {
        var scan = new AutonomousFrontierThreatPolicy();
        int[] combatants = Enumerable.Range(0, 20).ToArray();
        int checks = 0;
        bool Visible(int target) { checks++; return target == 18 || target == 100; }
        Assert.That(scan.VisiblePriority(combatants, new[] { 100 }, Visible), Is.Empty);
        Assert.That(checks, Is.EqualTo(8)); checks = 0;
        Assert.That(scan.VisiblePriority(combatants, new[] { 100 }, Visible), Is.Empty);
        Assert.That(checks, Is.EqualTo(8)); checks = 0;
        Assert.That(scan.VisiblePriority(combatants, new[] { 100 }, Visible), Is.EqualTo(new[] { 18 }));
        Assert.That(checks, Is.LessThanOrEqualTo(8));
    }

    [Test]
    public void DefenderCanEventuallyAttackAnEngineWhenAllCombatantsAreHidden()
    {
        var scan = new AutonomousFrontierThreatPolicy();
        int[] hidden = Enumerable.Range(0, 8).ToArray();
        Assert.That(scan.VisiblePriority(hidden, new[] { 100 }, target => target == 100), Is.Empty);
        Assert.That(scan.VisiblePriority(hidden, new[] { 100 }, target => target == 100), Is.EqualTo(new[] { 100 }));
        Assert.That(scan.VisiblePriority(hidden, new[] { 100 }, _ => true), Is.EqualTo(hidden), "Newly visible fighters replace the ram");
        Assert.That(scan.VisiblePriority(Array.Empty<int>(), new[] { 100 }, _ => true), Is.EqualTo(new[] { 100 }));
    }

    [Test]
    public void ScanCadenceIsPerActorAndDoesNotGrowWithPopulation()
    {
        var a = new AutonomousFrontierThreatPolicy();
        var b = new AutonomousFrontierThreatPolicy();
        Assert.That(a.Due(100, 0), Is.True);
        Assert.That(a.Due(1099, 0), Is.False);
        Assert.That(a.Due(1100, 0), Is.True);
        Assert.That(b.Due(100, 249), Is.True);
        Assert.That(b.Due(1348, 249), Is.False);
        Assert.That(b.Due(1349, 249), Is.True);
    }

    [Test]
    public void DenseEnemyCrowdUsesAtMostEightVisibilityChecks()
    {
        var scan = new AutonomousFrontierThreatPolicy();
        int checks = 0;
        var visible = scan.Visible(Enumerable.Range(0, 10000).ToArray(), _ => { checks++; return true; });
        Assert.That(checks, Is.EqualTo(8));
        Assert.That(visible, Is.EqualTo(Enumerable.Range(0, 8)));
    }

    [Test]
    public void BlockedNearestEnemiesCannotHideLaterVisibleEnemiesForever()
    {
        var scan = new AutonomousFrontierThreatPolicy();
        int[] enemies = Enumerable.Range(0, 20).ToArray();
        Assert.That(scan.Visible(enemies, i => i == 18), Is.Empty);
        Assert.That(scan.Visible(enemies, i => i == 18), Is.Empty);
        Assert.That(scan.Visible(enemies, i => i == 18), Is.EqualTo(new[] { 18 }));
        Assert.That(scan.Visible(enemies, _ => true), Is.EqualTo(Enumerable.Range(0, 8)));
    }

    [Test]
    public void DisappearingOrEmptyCrowdDoesNotKeepStaleTargetsOrInvalidCursor()
    {
        var scan = new AutonomousFrontierThreatPolicy();
        scan.Visible(Enumerable.Range(0, 20).ToArray(), _ => false);
        Assert.That(scan.Visible(new[] { 42 }, _ => true), Is.EqualTo(new[] { 42 }));
        Assert.That(scan.Visible(Array.Empty<int>(), _ => throw new Exception()), Is.Empty);
        Assert.That(scan.Visible(new[] { 43 }, _ => true), Is.EqualTo(new[] { 43 }));
    }

    [TestCase(eRealm.Albion, eRealm.Midgard, true)]
    [TestCase(eRealm.Midgard, eRealm.Hibernia, true)]
    [TestCase(eRealm.Hibernia, eRealm.Albion, true)]
    [TestCase(eRealm.Albion, eRealm.Albion, false)]
    [TestCase(eRealm.Albion, eRealm.None, false)]
    public void RealmRulesAreIndependentOfPveAssignmentOrTargetLevel(eRealm actor, eRealm enemy, bool expected)
    {
        Assert.That(AutonomousRvrTargetPolicy.IsEligible(actor, enemy, true, true, true, false, true), Is.EqualTo(expected));
    }

    [TestCase(false, true, false, true)]
    [TestCase(true, false, false, true)]
    [TestCase(true, true, true, true)]
    [TestCase(true, true, false, false)]
    public void DeathRegionSafeAreaAndNativeAttackProtectionAreNotBypassed(bool alive, bool sameRegion, bool safe, bool legal)
    {
        Assert.That(AutonomousRvrTargetPolicy.IsEligible(eRealm.Albion, eRealm.Midgard, alive, sameRegion, true, safe, legal), Is.False);
    }

    [Test]
    public void EarlyThreatEntryPointAndBrainLoadAndJit()
    {
        RuntimeHelpers.PrepareMethod(typeof(AutonomousWorldBotController).GetMethod(nameof(AutonomousWorldBotController.TryEngageFrontierThreat)).MethodHandle);
        RuntimeHelpers.PrepareMethod(typeof(BotBrain).GetMethod(nameof(BotBrain.Think)).MethodHandle);
        Assert.That(new AutonomousWorldBotController().TryEngageFrontierThreat(null), Is.False);
    }
}

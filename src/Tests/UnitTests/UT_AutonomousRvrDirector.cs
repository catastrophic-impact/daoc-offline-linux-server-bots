using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousRvrDirector
{
    [Test]
    public void ObjectivePhase_UsesAuthoritativeBreachLordAndRelicFallbackOrder()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRvrObjectiveState.Next(false, false, false, false, false), Is.EqualTo(eAutonomousRvrPhase.Traveling));
            Assert.That(AutonomousRvrObjectiveState.Next(true, true, true, true, true), Is.EqualTo(eAutonomousRvrPhase.BreachingDoor));
            Assert.That(AutonomousRvrObjectiveState.Next(true, false, true, true, true), Is.EqualTo(eAutonomousRvrPhase.AssaultingLord));
            Assert.That(AutonomousRvrObjectiveState.Next(true, false, false, true, true), Is.EqualTo(eAutonomousRvrPhase.HuntingEnemy));
            Assert.That(AutonomousRvrObjectiveState.Next(true, false, false, false, true), Is.EqualTo(eAutonomousRvrPhase.RelicRequiresPlayerCarrier));
        });
    }

    [Test]
    public void HumanPresenceIsNotPartOfRvrSelection()
    {
        var force = new AutonomousRvrDirector.Warband(eRealm.Albion, 8, 50, 2, 0, false);
        var choices = new List<AutonomousRvrDirector.Objective>
        {
            new("keep:1", "Dun Crauchon", eAutonomousRvrObjective.AssaultKeep, eRealm.Hibernia, 163, true, 1, 0, 3, 2, false, 8),
        };

        var result = AutonomousRvrDirector.Choose(force, choices);

        Assert.That(result.Kind, Is.EqualTo(eAutonomousRvrObjective.AssaultKeep));
        Assert.That(result.TargetId, Is.EqualTo("keep:1"));
    }

    [Test]
    public void RealmDefenseTakesPriorityOverKeepRaid()
    {
        var force = new AutonomousRvrDirector.Warband(eRealm.Midgard, 8, 50, 2, 0, false);
        var choices = new List<AutonomousRvrDirector.Objective>
        {
            new("defend:1", "Bledmeer Faste", eAutonomousRvrObjective.DefendKeep, eRealm.Midgard, 163, true, 7, 2, 0, 0, true, 4),
            new("keep:2", "Caer Benowyc", eAutonomousRvrObjective.AssaultKeep, eRealm.Albion, 163, true, 0, 0, 1, 1, false, 2),
        };

        Assert.That(AutonomousRvrDirector.Choose(force, choices).Kind, Is.EqualTo(eAutonomousRvrObjective.DefendKeep));
    }

    [Test]
    public void UndersizedForceDoesNotAttemptKeep()
    {
        var force = new AutonomousRvrDirector.Warband(eRealm.Hibernia, 4, 50, 1, 0, false);
        var choices = new List<AutonomousRvrDirector.Objective>
        {
            new("keep:3", "Nottmoor Faste", eAutonomousRvrObjective.AssaultKeep, eRealm.Midgard, 163, true, 0, 0, 0, 1, false, 1),
        };

        Assert.That(AutonomousRvrDirector.Choose(force, choices).Kind, Is.EqualTo(eAutonomousRvrObjective.Regroup));
    }

    [Test]
    public void EveryRealmStagesAtItsClassicSafeBorderKeep()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRvrStaging.TryGetBorderKeep(eRealm.Albion, out var albion), Is.True);
            Assert.That(albion.Name, Is.EqualTo("Castle Sauvage"));
            Assert.That(albion.RegionId, Is.EqualTo(1));
            Assert.That(AutonomousRvrStaging.TryGetBorderKeep(eRealm.Midgard, out var midgard), Is.True);
            Assert.That(midgard.Name, Is.EqualTo("Svasud Faste"));
            Assert.That(midgard.RegionId, Is.EqualTo(100));
            Assert.That(AutonomousRvrStaging.TryGetBorderKeep(eRealm.Hibernia, out var hibernia), Is.True);
            Assert.That(hibernia.Name, Is.EqualTo("Druim Ligen"));
            Assert.That(hibernia.RegionId, Is.EqualTo(200));
        });
    }

    [Test]
    public void RvrSupportsSoloThroughEightAndHasNoSinglePullActor()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRvrStaging.RollWarbandSize(8, 0), Is.EqualTo(1));
            Assert.That(AutonomousRvrStaging.RollWarbandSize(8, .999999), Is.EqualTo(8));
            Assert.That(AutonomousRvrStaging.UsesIndependentCombatActors(eAutonomousObjectiveKind.RvR), Is.True);
            Assert.That(AutonomousRvrStaging.UsesIndependentCombatActors(eAutonomousObjectiveKind.GroupPve), Is.False);
        });
    }

    [Test]
    public void WarbandTargetPressureSpreadsButStaysWithinTheClosestPartySizedSet()
    {
        int[] selections = Enumerable.Range(10, 8)
            .Select(id => AutonomousRvrStaging.TargetIndex(id, 20, 8)).ToArray();

        Assert.That(selections.Distinct().Count(), Is.EqualTo(8));
        Assert.That(selections, Has.All.InRange(0, 7));
        Assert.That(AutonomousRvrStaging.TargetIndex(99, 1, 8), Is.Zero);
    }

    [Test]
    public void HigherLevelLargerWarbandsHaveARealKeepAssaultChance()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRvrEventLayer.MajorAssaultWeight(8, 50), Is.EqualTo(1));
            Assert.That(AutonomousRvrEventLayer.ChooseIntent(
                new AutonomousRvrEventLayer.Force("high", eRealm.Albion, 8, 50, 2),
                false, true, true, .10), Is.EqualTo(AutonomousRvrEventLayer.Intent.AssaultRelicKeep));
            Assert.That(AutonomousRvrEventLayer.ChooseIntent(
                new AutonomousRvrEventLayer.Force("low", eRealm.Albion, 3, 30, 1),
                false, true, true, 0), Is.EqualTo(AutonomousRvrEventLayer.Intent.Roam));
            Assert.That(AutonomousRvrEventLayer.ShouldJoinActiveEvent(
                new AutonomousRvrEventLayer.Force("reserve", eRealm.Albion, 8, 50, 2),
                AutonomousRvrEventLayer.OrdinaryAssaultCap - 2, AutonomousRvrEventLayer.OrdinaryAssaultCap, false, false, 0), Is.False);
        });
    }
}

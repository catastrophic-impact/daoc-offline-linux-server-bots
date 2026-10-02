using System.Collections.Generic;
using DOL.Database;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, NonParallelizable]
public sealed class UT_AutonomousRvrEventLayer
{
    [SetUp] public void ResetEvents() => RvrEventTestState.Clear();
    [TearDown] public void ClearEvents() => RvrEventTestState.Clear();
    [Test]
    public void SmallForcesFavorHuntOrRoamWhileLargeHighLevelForcesCanAssault()
    {
        var small = new AutonomousRvrEventLayer.Force("small", eRealm.Albion, 3, 30, 1);
        var large = new AutonomousRvrEventLayer.Force("large", eRealm.Albion, 8, 50, 2);

        Assert.That(AutonomousRvrEventLayer.ChooseIntent(small, true, true, true, 0), Is.EqualTo(AutonomousRvrEventLayer.Intent.HuntEnemy));
        Assert.That(AutonomousRvrEventLayer.ChooseIntent(large, true, true, true, 0), Is.EqualTo(AutonomousRvrEventLayer.Intent.AssaultRelicKeep));
        Assert.That(AutonomousRvrEventLayer.MajorAssaultWeight(8, 50), Is.GreaterThan(AutonomousRvrEventLayer.MajorAssaultWeight(3, 30)));
    }

    [Test]
    public void SoloLowLevelAndUnsuppliedForcesCannotOpenKeepAssaults()
    {
        var solo = new AutonomousRvrEventLayer.Force("solo", eRealm.Albion, 1, 50, 0, true);
        var low = new AutonomousRvrEventLayer.Force("low", eRealm.Albion, 8, 34, 2, true);
        var unsupplied = new AutonomousRvrEventLayer.Force("poor", eRealm.Albion, 8, 50, 2, false);

        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRvrEventLayer.ChooseIntent(solo, false, true, true, 0), Is.EqualTo(AutonomousRvrEventLayer.Intent.Roam));
            Assert.That(AutonomousRvrEventLayer.ChooseIntent(low, false, true, true, 0), Is.EqualTo(AutonomousRvrEventLayer.Intent.Roam));
            Assert.That(AutonomousRvrEventLayer.ChooseIntent(unsupplied, false, true, true, 0), Is.EqualTo(AutonomousRvrEventLayer.Intent.Roam));
        });
    }

    [Test]
    public void OpposingFrontierBotsAreEligibleOnlyWhenNormalServerCombatAllowsIt()
    {
        Assert.That(AutonomousRvrTargetPolicy.IsEligible(eRealm.Albion, eRealm.Midgard,
            true, true, true, false, true), Is.True);
        Assert.That(AutonomousRvrTargetPolicy.IsEligible(eRealm.Albion, eRealm.Albion,
            true, true, true, false, true), Is.False);
        Assert.That(AutonomousRvrTargetPolicy.IsEligible(eRealm.Albion, eRealm.Midgard,
            true, true, true, true, true), Is.False);
        Assert.That(AutonomousRvrTargetPolicy.IsEligible(eRealm.Albion, eRealm.Midgard,
            true, true, true, false, false), Is.False);
    }

    [Test]
    public void KeepAssaultIsSharedButParticipationIsCapped()
    {
        var keep = new AutonomousRvrEventLayer.LiveObjective("keep-1", "Dun Crauchon", AutonomousRvrEventLayer.Intent.AssaultKeep,
            eRealm.Hibernia, 163, 100, 100, 0, false, 0, 0, 2, 2);
        var first = new AutonomousRvrEventLayer.Force("a", eRealm.Albion, 8, 50, 2);
        var second = new AutonomousRvrEventLayer.Force("b", eRealm.Albion, 8, 50, 2);

        AutonomousRvrEventLayer.Plan opened = AutonomousRvrEventLayer.ChooseOrJoin(first, new[] { keep }, 1, 0);
        AutonomousRvrEventLayer.Plan joined = AutonomousRvrEventLayer.ChooseOrJoin(second, new[] { keep }, 2, 0);

        Assert.That(opened.IsSharedEvent, Is.True);
        Assert.That(joined.TargetId, Is.EqualTo("keep-1"));
    }

    [TestCase(49, 49)] [TestCase(50, 49)]
    public void UnderFiftyOrMixedLevelForceCannotJoinKeepOrRelicEvents(int average, int minimum)
    {
        var keep = new AutonomousRvrEventLayer.LiveObjective("keep-50", "Test keep", AutonomousRvrEventLayer.Intent.AssaultKeep,
            eRealm.Hibernia, 163, 100, 100, 0, false, 0, 0, 2, 2);
        var carrier = new AutonomousRvrEventLayer.LiveObjective("carrier-50", "Relic carrier", AutonomousRvrEventLayer.Intent.HuntEnemy,
            eRealm.Hibernia, 163, 100, 100, 0, false, 1, 0, 0, 0, true);
        var roam = new AutonomousRvrEventLayer.LiveObjective("roam-50", "Patrol", AutonomousRvrEventLayer.Intent.Roam,
            eRealm.Hibernia, 163, 200, 200, 0, false, 0, 0, 0, 0);
        var force = new AutonomousRvrEventLayer.Force("mixed-level", eRealm.Albion, 8, average, 2, MinimumMemberLevel: minimum);
        var plan = AutonomousRvrEventLayer.ChooseOrJoin(force, new[] { keep, carrier, roam }, 100, 0);
        Assert.That(plan.IsSharedEvent, Is.False);
        Assert.That(plan.TargetId, Is.EqualTo("roam-50"));
    }

    [Test]
    public void ActiveEventAttractsForcesButKeepsAHighRollRoamingReserve()
    {
        var keep = new AutonomousRvrEventLayer.LiveObjective("keep-reserve", "Dun Bolg", AutonomousRvrEventLayer.Intent.AssaultKeep,
            eRealm.Hibernia, 163, 100, 100, 0, false, 0, 0, 2, 2);
        var roam = new AutonomousRvrEventLayer.LiveObjective("roam-reserve", "Bolg frontier", AutonomousRvrEventLayer.Intent.Roam,
            eRealm.Hibernia, 163, 200, 200, 0, false, 0, 0, 0, 0);
        var opener = new AutonomousRvrEventLayer.Force("reserve-open", eRealm.Albion, 8, 50, 2);
        var joiner = new AutonomousRvrEventLayer.Force("reserve-join", eRealm.Albion, 8, 50, 2);
        var decliner = new AutonomousRvrEventLayer.Force("reserve-decline", eRealm.Albion, 2, 20, 0);

        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(opener, new[] { keep, roam }, 100, 0).IsSharedEvent, Is.True);
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(joiner, new[] { keep, roam }, 101, 0).TargetId, Is.EqualTo("keep-reserve"));
        AutonomousRvrEventLayer.Plan declined = AutonomousRvrEventLayer.ChooseOrJoin(decliner, new[] { keep, roam }, 102, 1);

        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRvrEventLayer.ShouldJoinActiveEvent(joiner, 8, AutonomousRvrEventLayer.OrdinaryAssaultCap, false, false, 0), Is.True);
            Assert.That(AutonomousRvrEventLayer.ShouldJoinActiveEvent(decliner, 16, AutonomousRvrEventLayer.OrdinaryAssaultCap, false, false, 1), Is.False);
            Assert.That(declined.IsSharedEvent, Is.False);
            Assert.That(declined.TargetId, Is.EqualTo("roam-reserve"));
        });
    }

    [Test]
    public void CarrierEventAllowsAllThreeRealmsButCapsEachRealm()
    {
        var carrier = new AutonomousRvrEventLayer.LiveObjective("carrier-1", "Strength relic carrier", AutonomousRvrEventLayer.Intent.HuntEnemy,
            eRealm.Hibernia, 163, 100, 100, 0, false, 1, 0, 0, 0, true);
        var escort = new AutonomousRvrEventLayer.Force("hib", eRealm.Hibernia, 6, 50, 1);
        var alb = new AutonomousRvrEventLayer.Force("alb", eRealm.Albion, 6, 50, 1);
        var mid = new AutonomousRvrEventLayer.Force("mid", eRealm.Midgard, 6, 50, 1);

        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(escort, new[] { carrier }, 10, 0).Intent, Is.EqualTo(AutonomousRvrEventLayer.Intent.DefendEvent));
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(alb, new[] { carrier }, 11, 0).Intent, Is.EqualTo(AutonomousRvrEventLayer.Intent.HuntEnemy));
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(mid, new[] { carrier }, 12, 0).Intent, Is.EqualTo(AutonomousRvrEventLayer.Intent.HuntEnemy));
    }

    [Test]
    public void AuthoritativeZonePointEdgesRequireDistinctPersistedEndpoints()
    {
        Assert.That(AutonomousWorldBotController.IsAuthoritativeZonePointEdge(new DbZonePoint { SourceRegion = 163, TargetRegion = 161 }), Is.True);
        Assert.That(AutonomousWorldBotController.IsAuthoritativeZonePointEdge(new DbZonePoint { SourceRegion = 163, TargetRegion = 163 }), Is.False);
        Assert.That(AutonomousWorldBotController.IsAuthoritativeZonePointEdge(new DbZonePoint { SourceRegion = 0, TargetRegion = 161 }), Is.False);
    }
}

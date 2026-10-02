using System;
using System.Linq;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, NonParallelizable]
public class UT_PhysicalSiegeRally
{
    [SetUp] public void ResetEvents() => RvrEventTestState.Clear();
    [TearDown] public void ClearEvents() => RvrEventTestState.Clear();
    private static (AutonomousRvrEventLayer.LiveObjective Target, AutonomousRvrEventLayer.Force[] Forces) Open(bool relic, long now, int groups = 2)
    {
        string id = Guid.NewGuid().ToString();
        var target = new AutonomousRvrEventLayer.LiveObjective(id, "Physical rally",
            relic ? AutonomousRvrEventLayer.Intent.AssaultRelicKeep : AutonomousRvrEventLayer.Intent.AssaultKeep,
            eRealm.Hibernia, 200, 300000, 400000, 100, relic, 0, 0, 10, 2);
        var forces = Enumerable.Range(0, groups).Select(i => new AutonomousRvrEventLayer.Force(
            id + ":" + i, eRealm.Albion, 8, 50, 2)).ToArray();
        foreach (var force in forces)
            Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(force, [target], now, 0)?.TargetId, Is.EqualTo(id));
        return (target, forces);
    }
    private static void Attend(AutonomousRvrEventLayer.LiveObjective target, AutonomousRvrEventLayer.Force[] forces, int count, long now)
    {
        for (int i = 0; i < count; i++)
            AutonomousRvrEventLayer.ReportAttendance(target.Id, forces[i / 8].GroupId, eRealm.Albion, 1000 + i, true, now);
    }

    [TestCase(false)] [TestCase(true)]
    public void EveryRealmConvergesWithoutRequiringPhysicalStaging(bool relic)
    {
        long now = GameLoop.GameLoopTime;
        int attackers = relic ? 170 : 108;
        var (target, forces) = Open(relic, now, (attackers + 7) / 8);
        Attend(target, forces, attackers, now + 1);
        Assert.That(AutonomousRvrEventLayer.IsRallying(forces[0].GroupId, now + 1), Is.False);
        long ready = now + RealmEventPolicy.EarliestAssaultMilliseconds;
        Attend(target, forces, attackers, ready);
        Assert.That(AutonomousRvrEventLayer.IsBattleForce(forces[0].GroupId, ready), Is.True, "Opposing attendance cannot hold attackers at a camp.");
        AddDefenders(target, relic ? 96 : 64, ready + 1);
        Assert.That(AutonomousRvrEventLayer.IsBattleForce(forces[0].GroupId, ready + 1), Is.True);
        Assert.That(AutonomousRvrEventLayer.GetRallyOrder(forces[0].GroupId, eRealm.Albion, ready + 1), Is.Null);
        var defender = forces[0] with { GroupId = "defender", Realm = eRealm.Hibernia };
        var third = forces[0] with { GroupId = "third", Realm = eRealm.Midgard };
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(defender, [target], ready + 2, 0)?.TargetId, Is.EqualTo(target.Id));
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(third, [target], ready + 2, 0)?.TargetId, Is.EqualTo(target.Id));
        target = target with { UnderAttack = true };
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(defender, [target], ready + 3, 0)?.TargetId, Is.EqualTo(target.Id));
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(third, [target], ready + 3, 0)?.TargetId, Is.EqualTo(target.Id));
        Assert.That(AutonomousRvrEventLayer.KeepPlan(defender.GroupId, defender.Realm, ready + 3)?.Intent,
            Is.EqualTo(AutonomousRvrEventLayer.Intent.DefendEvent));
        Assert.That(AutonomousRvrEventLayer.KeepPlan(third.GroupId, third.Realm, ready + 3)?.Intent,
            Is.EqualTo(relic ? AutonomousRvrEventLayer.Intent.AssaultRelicKeep : AutonomousRvrEventLayer.Intent.AssaultKeep),
            "Third faction independently attacks/captures; it never defends the current owner");
        AutonomousRvrEventLayer.ReportAttendance(target.Id, forces[0].GroupId, eRealm.Albion, 1000, false, ready + 4);
        Assert.That(AutonomousRvrEventLayer.IsBattleForce(forces[0].GroupId, now + AutonomousRvrEventLayer.BattleLifetimeMilliseconds - 1), Is.True);
        Assert.That(AutonomousRvrEventLayer.TryConsumeRelease(forces[0].GroupId, ready + 1 + AutonomousRvrEventLayer.BattleLifetimeMilliseconds, out string reason), Is.True);
        Assert.That(reason, Does.Contain("defended"));
    }

    [TestCase(false)] [TestCase(true)]
    public void ContinuousAssaultExpiresAfterFourHoursEvenIfNobodyArrives(bool relic)
    {
        long now = GameLoop.GameLoopTime;
        var (target, forces) = Open(relic, now);
        long duration = AutonomousRvrEventLayer.Snapshot().Single().RemainingMilliseconds;
        Assert.That(duration, Is.EqualTo(AutonomousRvrEventLayer.BattleLifetimeMilliseconds));
        long deadline = now + duration;
        Attend(target, forces, 8, deadline - 1);
        Assert.That(AutonomousRvrEventLayer.IsBattleForce(forces[0].GroupId, deadline - 1), Is.True);
        foreach (var force in forces)
        {
            Assert.That(AutonomousRvrEventLayer.TryConsumeRelease(force.GroupId, deadline, out string reason), Is.True);
            Assert.That(reason, Does.Contain("battle timer expired"));
            Assert.That(AutonomousRvrEventLayer.IsBattleForce(force.GroupId, deadline), Is.False);
        }
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(forces[1], [target], deadline + 1, 1), Is.Null);
        AutonomousRvrEventLayer.EndTarget(target.Id, deadline + 2);
        foreach (var force in forces) Assert.That(AutonomousRvrEventLayer.IsForceCommitted(force.GroupId, deadline + 3), Is.False);
    }

    [Test]
    public void MissingLocalCandidateDoesNotCancelACommittedOrOtherRealmEvent()
    {
        long now = GameLoop.GameLoopTime;
        var (target, forces) = Open(false, now);
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(forces[0], [], now + 1, 1)?.TargetId, Is.EqualTo(target.Id));
        var independent = target with { Id = target.Id + "-other", OwningRealm = eRealm.Albion };
        var otherRealm = forces[0] with { GroupId = "independent", Realm = eRealm.Midgard };
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(otherRealm, [independent], now + 2, 0), Is.Null);
        Assert.That(AutonomousRvrEventLayer.IsForceCommitted(forces[0].GroupId, now + 2), Is.True);
    }

    [Test]
    public void RelicEscortInheritsReactingRealmsAndRemainingBattleClock()
    {
        long now = GameLoop.GameLoopTime;
        var (target, forces) = Open(true, now, 22);
        long ready = now + RealmEventPolicy.EarliestAssaultMilliseconds;
        Attend(target, forces, 170, ready);
        AddDefenders(target, 96, ready);
        var reactors = new[] { eRealm.Midgard, eRealm.Hibernia }.Select(r => forces[0] with { GroupId = target.Id + r, Realm = r }).ToArray();
        foreach (var force in reactors)
            Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(force, [target with { UnderAttack = true }], ready + 1, 0)?.TargetId, Is.EqualTo(target.Id));
        string carrier = target.Id + "-carrier";
        AutonomousRvrEventLayer.TransferToRelicCarrier(target.Id, carrier, ready + 60_000);
        Assert.That(AutonomousRvrEventLayer.CarrierRemainingMilliseconds(carrier, ready + 60_000), Is.EqualTo(AutonomousRvrEventLayer.BattleLifetimeMilliseconds - (ready - now) - 60_000));
        foreach (var force in forces.Concat(reactors)) Assert.That(AutonomousRvrEventLayer.IsForceCommitted(force.GroupId, ready + 60_001), Is.True);
        foreach (var force in forces.Concat(reactors))
            Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(force, [], ready + 60_001, 0)?.TargetId, Is.EqualTo(carrier),
                "A partial route snapshot cannot disband the moving relic escort/interceptors.");
        AutonomousRvrEventLayer.EndTarget(carrier, ready + 60_002);
        foreach (var force in forces.Concat(reactors)) Assert.That(AutonomousRvrEventLayer.TryConsumeRelease(force.GroupId, ready + 60_003, out _), Is.True);
    }

    [TestCase(false)] [TestCase(true)]
    public void PendingEventFillsBeforeNewOneAndLateReinforcementsRespectCaps(bool relic)
    {
        long now = GameLoop.GameLoopTime;
        int cap = relic ? AutonomousRvrEventLayer.RelicAssaultCap : AutonomousRvrEventLayer.OrdinaryAssaultCap;
        var (target, forces) = Open(relic, now, cap / 8);
        var other = target with { Id = target.Id + "-other" };
        var roam = target with { Id = target.Id + "-roam", Kind = AutonomousRvrEventLayer.Intent.Roam };
        var extra = forces[0] with { GroupId = "extra" };
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(extra, [target, other, roam], now + 1, 0)?.TargetId, Is.EqualTo(roam.Id));
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(extra with { GroupId = "reserve", RoamingReserve = true }, [target, other, roam], now + 2, 0)?.TargetId, Is.EqualTo(roam.Id));
        AutonomousRvrEventLayer.RemoveForce(forces[^1].GroupId);
        long ready = now + RealmEventPolicy.EarliestAssaultMilliseconds;
        Attend(target, forces, cap - 8, ready);
        AddDefenders(target, relic ? 96 : 64, ready);
        Assert.That(AutonomousRvrEventLayer.IsBattleForce(forces[0].GroupId, ready), Is.True);
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(extra, [target], ready + 1, 0)?.TargetId, Is.EqualTo(target.Id));
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(extra with { GroupId = "over-cap" }, [target], ready + 2, 0), Is.Null);
    }

    private static void AddDefenders(AutonomousRvrEventLayer.LiveObjective target, int count, long now)
    {
        for (int group = 0; group < (count + 7) / 8; group++)
        {
            var force = new AutonomousRvrEventLayer.Force(target.Id + "-def-" + group, target.OwningRealm, 8, 50, 2);
            Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(force, [target], now, 0)?.TargetId, Is.EqualTo(target.Id));
            for (int member = 0; member < 8 && group * 8 + member < count; member++)
                AutonomousRvrEventLayer.ReportAttendance(target.Id, force.GroupId, force.Realm, 2000 + group * 8 + member, true, now);
        }
    }
    private sealed class FixedRoll(double value) : Random { public override double NextDouble() => value; }
    [Test]
    public void RelicsRemainRarerAndLevelFiftyTaskSplitIsUnchanged()
    {
        var force = new AutonomousRvrEventLayer.Force("weight", eRealm.Albion, 8, 50, 2);
        Assert.That(Enumerable.Range(0, 100).Count(i => AutonomousRvrEventLayer.ChooseIntent(force, false, true, true, i / 100d) == AutonomousRvrEventLayer.Intent.AssaultRelicKeep), Is.EqualTo(15));
        var rolls = Enumerable.Range(0, 100).Select(i => AutonomousObjectiveAssignments.RollLevelFiftyObjective(new FixedRoll(i / 100d))).ToArray();
        Assert.That(rolls.Count(k => k == eAutonomousObjectiveKind.RvR), Is.EqualTo(40));
        Assert.That(rolls.Count(k => k == eAutonomousObjectiveKind.GroupPve), Is.EqualTo(40));
        Assert.That(rolls.Count(k => k == eAutonomousObjectiveKind.SoloPve), Is.EqualTo(20));
        var record = new OfflineWorldBotRecord { Level = 50, ObjectiveRvrEligibleUtc = AutonomousObjectiveAssignments.PveCompletionRequired };
        Assert.That(AutonomousObjectiveAssignments.IsRvrEligible(record, DateTime.UtcNow), Is.False);
    }
}

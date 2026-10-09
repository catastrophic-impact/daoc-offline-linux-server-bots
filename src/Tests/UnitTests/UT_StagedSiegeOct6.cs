using System;
using System.Linq;
using DOL.GS;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>Keep sieges gather at the siege camp and assault together (rvr_siege_staged_assault).</summary>
[TestFixture, NonParallelizable]
public class UT_StagedSiegeOct6
{
    [SetUp] public void SetUp() { RvrEventTestState.Clear(); AutonomousRvrEventLayer.StagedAssault = true; }
    [TearDown] public void TearDown() => RvrEventTestState.Clear();

    [Test]
    public void AnAutomaticSiegeRalliesFirstInsteadOfStartingTheBattle()
    {
        string id = Guid.NewGuid().ToString();
        long opened = GameLoop.GameLoopTime;
        var objective = new AutonomousRvrEventLayer.LiveObjective(id, "Staged keep",
            AutonomousRvrEventLayer.Intent.AssaultKeep, eRealm.Hibernia, 200, 100, 100, 0, false, 0, 0, 5, 2);
        var attacker = new AutonomousRvrEventLayer.Force(id + "-attack", eRealm.Albion, 8, 50, 1);

        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(attacker, [objective], opened, 0)?.TargetId, Is.EqualTo(id));
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRvrEventLayer.IsBattleForce(attacker.GroupId, opened), Is.False, "no battle before the army gathers");
            Assert.That(AutonomousRvrEventLayer.IsRallying(attacker.GroupId, opened), Is.True);
            Assert.That(AutonomousRvrEventLayer.GetRallyOrder(attacker.GroupId, eRealm.Albion, opened)?.Slots.Length, Is.EqualTo(8));
            Assert.That(AutonomousRvrEventLayer.Snapshot().Single(b => b.TargetId == id).Kind, Is.EqualTo("Keep siege rally"));
        });
    }

    [Test]
    public void AttackersMusterAtHomeFirstAndMarchAtTheMusterDeadline()
    {
        string id = Guid.NewGuid().ToString();
        long opened = GameLoop.GameLoopTime;
        var objective = new AutonomousRvrEventLayer.LiveObjective(id, "Muster keep",
            AutonomousRvrEventLayer.Intent.AssaultKeep, eRealm.Hibernia, 200, 100, 100, 0, false, 0, 0, 5, 2);
        var attacker = new AutonomousRvrEventLayer.Force(id + "-attack", eRealm.Albion, 8, 50, 1);
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(attacker, [objective], opened, 0)?.TargetId, Is.EqualTo(id));

        Assert.That(AutonomousRvrEventLayer.GetRallyOrder(attacker.GroupId, eRealm.Albion, opened)?.HomeMuster, Is.True,
            "attackers gather at their border keep first");
        long firstDeadline = opened + AutonomousRvrEventLayer.HomeMusterMilliseconds + 1;
        Assert.That(AutonomousRvrEventLayer.GetRallyOrder(attacker.GroupId, eRealm.Albion, firstDeadline)?.HomeMuster, Is.True,
            "a muster too thin to start a battle waits once more for the army on its way");
        long marched = firstDeadline + AutonomousRvrEventLayer.MusterExtensionMilliseconds + 1;
        var order = AutonomousRvrEventLayer.GetRallyOrder(attacker.GroupId, eRealm.Albion, marched);
        Assert.Multiple(() =>
        {
            Assert.That(order?.HomeMuster, Is.False, "the army marches at the extended muster deadline");
            Assert.That(order?.RemainingMilliseconds, Is.GreaterThanOrEqualTo(RealmEventPolicy.RecruitmentMilliseconds(0) - 1),
                "the forward rally keeps its full hour after the march");
        });
    }

    [TestCase(false, 107, ExpectedResult = false, TestName = "107 attackers at the camp are not yet an assault force")]
    [TestCase(false, 108, ExpectedResult = true, TestName = "108 attackers at the camp start the assault")]
    [TestCase(true, 32, ExpectedResult = true, TestName = "At the one-hour deadline 32 attackers still go in")]
    public bool TheAssaultStartsWithAViableForce(bool deadline, int attackers) =>
        RealmEventPolicy.SiegeReady(false, deadline, attackers, 0);
}

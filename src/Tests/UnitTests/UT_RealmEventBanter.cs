using DOL.GS;
using NUnit.Framework;
using System.Collections.Generic;
using System.Reflection;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_RealmEventBanter
    {
        private static Dictionary<(string, eRealm), (string Text, long Expires)> Pending =>
            (Dictionary<(string, eRealm), (string Text, long Expires)>)typeof(RealmEventNotices)
                .GetField("Pending", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        [SetUp] public void Before() { DOL.GS.Tests.RvrEventTestState.Clear(); Pending.Clear(); }
        [TearDown] public void After() { DOL.GS.Tests.RvrEventTestState.Clear(); Pending.Clear(); }

        [Test]
        public void ActualKeepCaptureReplacesPendingCountdownForEveryRealm()
        {
            var keep = new AutonomousRvrEventLayer.LiveObjective("banter-keep", "Test keep", AutonomousRvrEventLayer.Intent.AssaultKeep,
                eRealm.Midgard, 100, 100, 100, 0, false, 0, 0, 2, 2);
            Assert.That(AutonomousRvrEventLayer.ForceStart(keep, eRealm.Albion, 0, out _), Is.True);
            foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
                RealmEventNotices.Queue(keep.Id, realm, "Old preparation countdown");
            AutonomousRvrEventLayer.EndTarget(keep.Id, 100, eRealm.Hibernia);
            Assert.That(Pending[(keep.Id, eRealm.Hibernia)].Text, Does.Contain("is ours"));
            Assert.That(Pending[(keep.Id, eRealm.Albion)].Text, Does.Contain("fallen to Hibernia"));
            Assert.That(Pending[(keep.Id, eRealm.Midgard)].Text, Does.Not.Contain("countdown"));
        }

        [Test]
        public void NoticeQueueIsBoundedAndLatestStatusWins()
        {
            for (int i = 0; i < 100; i++) RealmEventNotices.Queue("bounded-" + i, eRealm.Albion, "Preparing");
            Assert.That(Pending.Count, Is.EqualTo(32));
            RealmEventNotices.Queue("bounded-0", eRealm.Albion, "Victory");
            Assert.That(Pending.Count, Is.EqualTo(32));
            Assert.That(Pending[("bounded-0", eRealm.Albion)].Text, Is.EqualTo("Victory"));
        }
        [TestCase(false, false, 1200000, true)]
        [TestCase(false, false, 1200001, false)]
        [TestCase(false, false, 0, false)]
        [TestCase(false, true, 1000000, false)]
        [TestCase(true, false, 1000000, false)]
        public void ReminderIsOnceDuringPreparationOnly(bool started, bool sent, long remaining, bool expected)
        {
            Assert.That(RealmEventBanter.ReminderDue(started, sent, remaining), Is.EqualTo(expected));
        }
        [Test]
        public void CountdownDoesNotPromiseAutomaticStart()
        {
            Assert.That(RealmEventBanter.RaidReminder("Galladoria", false), Does.Contain("once enough"));
            Assert.That(RealmEventBanter.RaidReminder("Golestandt", true), Does.Contain("earliest assault"));
        }
        [Test]
        public void VictoryAndTimeoutAreNotConfused()
        {
            Assert.That(RealmEventBanter.RaidOutcome("Golestandt", "Boss defeated"), Does.Contain("Golestandt"));
            Assert.That(RealmEventBanter.RaidOutcome("Galladoria", "Failed rally"), Does.Contain("muster is called off"));
            Assert.That(RealmEventBanter.RaidOutcome("Galladoria", "Ended (unconfirmed)"), Does.Contain("without a confirmed victory"));
            Assert.That(RealmEventBanter.SiegeOutcome("Caer Benowyc",true,true,false,eRealm.None,eRealm.Albion), Does.Contain("We held"));
            Assert.That(RealmEventBanter.SiegeOutcome("Caer Benowyc",false,true,false,eRealm.None,eRealm.Midgard), Does.Contain("Withdraw"));
        }
        [Test]
        public void ThirdRealmCaptureCreditsActualWinner()
        {
            Assert.That(RealmEventBanter.SiegeOutcome("Keep",false,false,false,eRealm.Hibernia,eRealm.Hibernia), Does.Contain("is ours"));
            Assert.That(RealmEventBanter.SiegeOutcome("Keep",false,false,false,eRealm.Hibernia,eRealm.Midgard), Does.Contain("fallen to Hibernia"));
        }
    }
}

using System.Linq;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    [TestFixture, NonParallelizable]
    public class UT_PlayerKeepDefense
    {
        [SetUp] public void SetUp() { RvrEventTestState.Clear(); }
        [TearDown] public void TearDown() { RvrEventTestState.Clear(); }
        private static AutonomousRvrEventLayer.LiveObjective Target(string id = "defense") =>
            new(id, "Test keep", AutonomousRvrEventLayer.Intent.AssaultKeep, eRealm.Midgard, 100, 100000, 100000, 100, false, 0, 0, 0, 0);

        [Test]
        public void AlarmOpensImmediateTimedBattleAndRepeatedHitsDoNotResetItsTimer()
        {
            Assert.That(AutonomousRvrEventLayer.DefenseResponseMilliseconds, Is.EqualTo(4 * 60 * 60_000L));
            long now = GameLoop.GameLoopTime + 100;
            Assert.That(AutonomousRvrEventLayer.BeginDefenseResponse(Target(), eRealm.Albion, "offline", now), Is.True);
            var first = AutonomousRvrEventLayer.Snapshot().Single();
            Assert.That(first.BattleStarted, Is.True);
            Assert.That(first.Kind, Does.Contain("defense"));
            Assert.That(first.RemainingMilliseconds, Is.EqualTo(now + AutonomousRvrEventLayer.DefenseResponseMilliseconds - GameLoop.GameLoopTime));
            Assert.That(AutonomousRvrEventLayer.BeginDefenseResponse(Target(), eRealm.Albion, "offline", now + 30_000), Is.False);
            Assert.That(AutonomousRvrEventLayer.Snapshot().Single().RemainingMilliseconds, Is.EqualTo(first.RemainingMilliseconds));
        }

        [Test]
        public void PlayerResponseDoesNotBlockIndependentAutonomousSiegeOrResetExistingSiegeClock()
        {
            long now = GameLoop.GameLoopTime + 100;
            AutonomousRvrEventLayer.BeginDefenseResponse(Target(), eRealm.Albion, "offline", now);
            Assert.That(AutonomousRvrEventLayer.ForceStart(Target("other"), eRealm.Hibernia, now, out _), Is.True);
            var before = AutonomousRvrEventLayer.Snapshot().Single(e => e.TargetId == "other");
            AutonomousRvrEventLayer.BeginDefenseResponse(Target("other"), eRealm.Albion, "offline", now + 30_000);
            var after = AutonomousRvrEventLayer.Snapshot().Single(e => e.TargetId == "other");
            Assert.That(after.RemainingMilliseconds, Is.EqualTo(before.RemainingMilliseconds));
            Assert.That(after.Attacker, Is.EqualTo(eRealm.Hibernia));
        }

        [Test]
        public void QuietDefenseSurvivesThreeMinutesButEndsAtFourHours()
        {
            long now = GameLoop.GameLoopTime + 100;
            AutonomousRvrEventLayer.BeginDefenseResponse(Target(), eRealm.Albion, "offline", now);
            AutonomousRvrEventLayer.PulseDefense(now + 5 * 60_000);
            Assert.That(AutonomousRvrEventLayer.Snapshot(), Has.Length.EqualTo(1));
            AutonomousRvrEventLayer.PulseDefense(now + AutonomousRvrEventLayer.DefenseResponseMilliseconds);
            Assert.That(AutonomousRvrEventLayer.Snapshot(), Is.Empty);
        }

        [Test]
        public void CrossRealmAttackPreservesBothIndependentDefenseWindows()
        {
            long now = GameLoop.GameLoopTime + 100;
            AutonomousRvrEventLayer.BeginDefenseResponse(Target("albion") with { OwningRealm = eRealm.Albion }, eRealm.Midgard, "offline", now);
            var old = AutonomousRvrEventLayer.Snapshot().Single();
            AutonomousRvrEventLayer.BeginDefenseResponse(Target("hibernia") with { OwningRealm = eRealm.Hibernia }, eRealm.Midgard, "offline", now + 60_000);
            var events = AutonomousRvrEventLayer.Snapshot();
            Assert.That(events.Length, Is.EqualTo(2));
            Assert.That(events.Single(e => e.TargetId == "albion").RemainingMilliseconds, Is.EqualTo(old.RemainingMilliseconds));
            Assert.That(AutonomousRvrEventLayer.CanRedirectDefense(eRealm.Albion, eRealm.Hibernia, "offline", "offline", now, now + 60_000), Is.False);
            Assert.That(AutonomousRvrEventLayer.CanRedirectDefense(eRealm.Albion, eRealm.Albion, "offline", "offline", now, now + 60_000), Is.True);
            Assert.That(AutonomousRvrEventLayer.CanRedirectDefense(eRealm.Albion, eRealm.Albion, "offline", "offline", now + 60_000, now), Is.False);
            Assert.That(AutonomousRvrEventLayer.CanRedirectDefense(eRealm.Albion, eRealm.Albion, "other", "offline", now, now + 60_000), Is.False);
        }

        [Test]
        public void FriendlyAndProtectedKeepsDoNotRaiseHostileResponse()
        {
            Assert.That(AutonomousRvrEventLayer.BeginDefenseResponse(Target(), eRealm.Midgard, "offline", 100), Is.False);
            Assert.That(AutonomousRvrEventLayer.BeginDefenseResponse(Target() with { IsPortalKeep = true }, eRealm.Albion, "offline", 100), Is.False);
            Assert.That(AutonomousRvrEventLayer.Snapshot(), Is.Empty);
        }

        [Test]
        public void ResponseHasLargeDefenseAndBoundedHelpersWithStableReserve()
        {
            Assert.That(AutonomousRvrEventLayer.ResponseCap(true), Is.EqualTo(240));
            Assert.That(AutonomousRvrEventLayer.ResponseCap(false), Is.EqualTo(96));
            Assert.That(Enumerable.Range(1, 100).Count(id => AutonomousRvrEventLayer.ResponseReserve(id)), Is.EqualTo(30));
            Assert.That(AutonomousRvrEventLayer.PlayerInstigator(null), Is.Null);
        }

        [TestCase(true, true, false, true)]
        [TestCase(true, true, true, false)]
        [TestCase(false, true, false, false)]
        [TestCase(false, false, false, true)]
        public void ForcedExpeditionMayCoexistButNeverDuplicateEncounter(bool forced, bool sameRealm, bool sameEncounter, bool allowed)
        {
            Assert.That(RealmRaidRecruitmentPolicy.CanOpenEvent(forced, sameRealm, sameEncounter), Is.EqualTo(allowed));
        }
    }
}

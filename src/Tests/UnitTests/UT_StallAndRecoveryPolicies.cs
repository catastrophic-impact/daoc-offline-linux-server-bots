using System.Linq;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_StallAndRecoveryPolicies
    {
        [SetUp] public void Reset() => AiTurnStallMonitor.TakeMinute();

        [Test]
        public void StallMonitorKeepsOnlyTheFiveSlowestTurnsAndCountsTheRest()
        {
            for (int i = 1; i <= 40; i++)
                AiTurnStallMonitor.Record("bot" + i, "BotBrain", 25 + i * 10);
            var (slow, big, top) = AiTurnStallMonitor.TakeMinute();
            Assert.That(slow, Is.EqualTo(40));
            Assert.That(big, Is.EqualTo(40 - 22), "turns of 250 ms or more");
            Assert.That(top.Select(t => t.Milliseconds), Is.EqualTo(new long[] { 425, 415, 405, 395, 385 }));
            Assert.That(AiTurnStallMonitor.TakeMinute().Slow, Is.Zero, "each minute starts empty");
        }

        [Test]
        public void OnlyBigTurnsStillGetTheirOwnWarningLine()
        {
            Assert.That(AiTurnStallMonitor.ShouldLogIndividually(99), Is.False);
            Assert.That(AiTurnStallMonitor.ShouldLogIndividually(100), Is.True);
        }

        [Test]
        public void StallMinutesAreNamedByTheirLikelyCause()
        {
            var top = new[] { new AiTurnStallMonitor.Turn("Conainren", "SluaghbinderBotBrain", 4800) };
            Assert.That(AiTurnStallMonitor.IsStallMinute(400, 200), Is.False, "quiet minutes write nothing");
            Assert.That(AiTurnStallMonitor.IsStallMinute(5100, 4900), Is.True);
            Assert.That(AiTurnStallMonitor.LikelyCause(5100, 4900, top), Does.StartWith("garbage-collection"));
            Assert.That(AiTurnStallMonitor.LikelyCause(5100, 0, top), Does.Contain("Conainren"));
            Assert.That(AiTurnStallMonitor.LikelyCause(5100, 0, []), Does.StartWith("mixed"));
        }

        [Test]
        public void StuckBotPetIsRecoveredOnlyAfterTenSecondsWithoutProgress()
        {
            Assert.That(BotPetStuckRecovery.IsStuck(0, 50_000), Is.False);
            Assert.That(BotPetStuckRecovery.IsStuck(40_001, 50_000), Is.False);
            Assert.That(BotPetStuckRecovery.IsStuck(40_000, 50_000), Is.True);
        }

        [Test]
        public void RallyMemberEscapesAfterThreeFailedRoutes()
        {
            Assert.That(RealmRaidHubEscape.ShouldEscape(2), Is.False);
            Assert.That(RealmRaidHubEscape.ShouldEscape(3), Is.True);
        }
    }
}

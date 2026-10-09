using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Healer/buffer reach and the group leader no longer waiting on a stuck member.</summary>
    [TestFixture]
    public class UT_SupportReachAndGroupStall
    {
        [Test]
        public void ASupportBotGivesUpOnATargetItCannotGetCloserTo()
        {
            var reach = new BotSupportReach();
            object member = new();
            Assert.Multiple(() =>
            {
                Assert.That(reach.KeepApproaching(member, 1500, 0), Is.True);
                Assert.That(reach.KeepApproaching(member, 1450, 6_000), Is.True, "not yet 12 s");
                Assert.That(reach.KeepApproaching(member, 1450, 12_000), Is.False, "no 100-unit progress in 12 s");
                Assert.That(reach.IsSkipped(member, 20_000), Is.True);
                Assert.That(reach.IsSkipped(member, 42_001), Is.False, "skipped for 30 s only");
            });
        }

        [Test]
        public void ProgressKeepsTheApproachGoing()
        {
            var reach = new BotSupportReach();
            object member = new();
            reach.KeepApproaching(member, 1500, 0);
            Assert.That(reach.KeepApproaching(member, 1300, 11_000), Is.True);
            Assert.That(reach.KeepApproaching(member, 1290, 20_000), Is.True, "the clock restarted at the last progress");
        }

        [TestCase(0f, 1L, 44_000L, ExpectedResult = false, TestName = "Not yet 45 s")]
        [TestCase(0f, 0L, 45_000L, ExpectedResult = false, TestName = "No first observation")]
        [TestCase(20f, 1L, 45_001L, ExpectedResult = true, TestName = "Barely moved in 45 s")]
        [TestCase(400f, 1L, 45_001L, ExpectedResult = false, TestName = "Moving, just behind a moving leader")]
        public bool AGroupMemberStuckFor45SecondsNoLongerHoldsTheGroup(float moved, long since, long now) =>
            AutonomousGroupMemberStall.IsStalled(moved, since, now);
    }
}

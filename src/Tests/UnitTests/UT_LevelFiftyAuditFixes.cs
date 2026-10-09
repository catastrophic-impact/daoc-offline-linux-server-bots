using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Fixes from the 2026-10-02 level-50 audit (6,000 bots, 6 hours).</summary>
    [TestFixture]
    public class UT_LevelFiftyAuditFixes
    {
        // 1. A member stranded on a navmesh island regroups at the expedition front.
        [TestCase(1, true, false, false)]
        [TestCase(2, true, false, false)]
        [TestCase(3, true, false, true)]
        [TestCase(5, true, false, true)]
        [TestCase(3, false, false, false)]   // dead members use normal corpse recovery
        [TestCase(3, true, true, false)]     // never teleport out of a fight
        public void StrandedExpeditionMemberRegroupsAfterThreeFailures(int failures, bool alive, bool inCombat, bool expected) =>
            Assert.That(AutonomousWorldBotController.ShouldRegroupStrandedMember(failures, alive, inCombat), Is.EqualTo(expected));

        // 3. Edging forward inside the same pocket does not clear the repeated-failure count.
        [Test]
        public void ForwardProgressInsideThePocketKeepsTheFailureCount()
        {
            var pocket = new Vector3(425740, 318142, 4730);
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousRouteRecoveryPolicy.HasLeftFailurePocket(200, 200, pocket, new Vector3(426000, 318400, 4730)), Is.False);
                Assert.That(AutonomousRouteRecoveryPolicy.HasLeftFailurePocket(200, 200, pocket, new Vector3(427000, 318142, 4730)), Is.True);
                Assert.That(AutonomousRouteRecoveryPolicy.HasLeftFailurePocket(200, 201, pocket, pocket), Is.True);
                Assert.That(AutonomousRouteRecoveryPolicy.HasLeftFailurePocket(0, 200, default, pocket), Is.True, "no pocket recorded yet");
            });
        }

        // 4. Flying monsters far above or below the bot are not pull targets; low flyers still are.
        [TestCase(GameNPC.eFlags.FLYING, 13184, 8895, true)]    // griffon glider over Gripklosa
        [TestCase(GameNPC.eFlags.FLYING, 9300, 8895, true)]
        [TestCase(GameNPC.eFlags.FLYING, 9200, 8895, false)]    // within 400: reachable flyer
        [TestCase(GameNPC.eFlags.FLYING, 5600, 5550, false)]    // white light near the ground
        [TestCase((GameNPC.eFlags)0, 13184, 8895, false)]       // a grounded monster on a ledge is the route's job
        public void HighFlyersAreSkipped(GameNPC.eFlags flags, int monsterZ, int botZ, bool unreachable) =>
            Assert.That(AutonomousPveTargetPolicy.IsUnreachableFlyer(flags, monsterZ, botZ), Is.EqualTo(unreachable));

        // 5. A nearly full automatic raid starts late instead of failing its rally.
        [TestCase(false, 75, 180, true, true)]
        [TestCase(false, 80, 196, true, true)]    // Caer Sidi run 1
        [TestCase(false, 74, 199, true, false)]   // before the late window: 200 still required
        [TestCase(false, 80, 179, true, false)]
        [TestCase(false, 80, 190, false, false)]  // a dragon must still be landed
        [TestCase(true, 80, 190, true, false)]    // player-forced raids keep the 200 rule
        [TestCase(false, 15, 200, true, true)]    // normal start unchanged
        public void NearlyFullAutomaticRaidStartsLate(bool forced, int minutes, int present, bool landed, bool expected) =>
            Assert.That(RealmRaidRecruitmentPolicy.Ready(forced, minutes * 60_000L, present, landed), Is.EqualTo(expected));
    }
}

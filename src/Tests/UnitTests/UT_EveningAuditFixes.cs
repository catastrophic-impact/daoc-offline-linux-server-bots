using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Fixes from the 2026-10-02 evening audit (19:02-22:57 run).</summary>
    [TestFixture]
    public class UT_EveningAuditFixes
    {
        // 1. Keep travel: long legs are chunked and a still bot is a stall.
        [Test]
        public void LongKeepLegsAreChunkedAndStillBotsStall()
        {
            Assert.Multiple(() =>
            {
                // Hibernia portal keep -> Caer Benowyc was one ~50,000-unit leg.
                Assert.That(KeepTravelStallPolicy.NeedsShorterLeg(new(605743, 293676, 4839), new(653430, 345890, 6280)), Is.True);
                Assert.That(KeepTravelStallPolicy.NeedsShorterLeg(new(0, 0, 0), new(5000, 0, 0)), Is.False);
                Assert.That(KeepTravelStallPolicy.IsStalled(60_000, false, false), Is.True);
                Assert.That(KeepTravelStallPolicy.IsStalled(59_000, false, false), Is.False);
                Assert.That(KeepTravelStallPolicy.IsStalled(600_000, true, false), Is.False, "fighting or casting is not a stall");
                Assert.That(KeepTravelStallPolicy.IsStalled(600_000, false, true), Is.False, "riding a stable horse is not a stall");
                Assert.That(KeepTravelStallPolicy.SameSpot(new(625357, 314061, 5034), new(625600, 314200, 5034)), Is.True);
                Assert.That(KeepTravelStallPolicy.SameSpot(new(625357, 314061, 5034), new(626000, 314061, 5034)), Is.False);
            });
        }

        [Test]
        public void PointAlongWalksThePathCorners()
        {
            Vector3[] corners = [new(0, 0, 0), new(3000, 0, 0), new(3000, 3000, 0)];
            Assert.Multiple(() =>
            {
                Assert.That(KeepTravelStallPolicy.PointAlong(corners, 4000), Is.EqualTo(new Vector3(3000, 1000, 0)));
                Assert.That(KeepTravelStallPolicy.PointAlong(corners, 10_000), Is.EqualTo(new Vector3(3000, 3000, 0)), "shorter path: its end");
                Assert.That(KeepTravelStallPolicy.PointAlong([], 100), Is.Null);
            });
        }

        // 1. Keep courtyards: a bot bouncing between two spots ~460 apart is one pocket.
        [Test]
        public void CourtyardPingPongIsOnePocket()
        {
            Vector3 anchor = new(676804, 628066, 5187);
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousRouteRecoveryPolicy.IsSameRepeatedFailurePocket(100, 100, anchor, new(676338, 628132, 5187), 120_000), Is.True);
                Assert.That(AutonomousRouteRecoveryPolicy.IsSameRepeatedFailurePocket(100, 100, anchor, new(677075, 627694, 5187), 120_000), Is.True);
                Assert.That(AutonomousRouteRecoveryPolicy.IsSameRepeatedFailurePocket(100, 100, anchor, new(678200, 628066, 5187), 120_000), Is.False);
            });
        }

        // 2. Dragons: a grounded dragon in a fight counts as landed anywhere; flights end.
        [TestCase(false, true, false, true)]
        [TestCase(false, false, true, true)]    // dragged out of the lair circle mid-fight
        [TestCase(false, false, false, false)]  // wandered off and idle
        [TestCase(true, true, true, false)]     // airborne is never landed
        public void DragonLandedRule(bool flying, bool nearLair, bool inCombat, bool landed) =>
            Assert.That(RealmRaidStaging.DragonCountsAsLanded(flying, nearLair, inCombat), Is.EqualTo(landed));

        [Test]
        public void DragonFlightAlwaysEndsInALanding()
        {
            var watch = new DragonFlightWatch();
            watch.Begin(0);
            Assert.Multiple(() =>
            {
                Assert.That(watch.ShouldCutShort(60_000, 0), Is.False, "still on the first leg");
                Assert.That(watch.ShouldCutShort(80_000, 1), Is.False, "reached a waypoint");
                Assert.That(watch.ShouldCutShort(80_000 + DragonFlightWatch.WaypointStallMilliseconds, 1), Is.True, "stalled between waypoints");
            });
            var steady = new DragonFlightWatch();
            steady.Begin(0);
            long t = 0;
            int index = 0;
            while (t < DragonFlightWatch.MaximumRouteMilliseconds - 60_000)
            {
                t += 60_000;
                Assert.That(steady.ShouldCutShort(t, ++index), Is.False);
            }
            Assert.That(steady.ShouldCutShort(DragonFlightWatch.MaximumRouteMilliseconds, ++index), Is.True, "flight far longer than a route");
            var landing = new DragonFlightWatch();
            Assert.Multiple(() =>
            {
                Assert.That(landing.ShouldForceLanding(1_000), Is.False);
                Assert.That(landing.ShouldForceLanding(1_000 + DragonFlightWatch.LandingApproachMilliseconds), Is.True);
            });
        }

        // 3. Epic dungeon routes never hold forever on an undamageable target.
        [TestCase(false, 4 * 60_000, false)]
        [TestCase(false, 5 * 60_000, true)]
        [TestCase(true, 60 * 60_000, false)]   // final bosses are never set aside
        public void UndamagedRouteTargetIsSetAside(bool finalBoss, long sinceProgress, bool skip) =>
            Assert.That(RealmRaidDungeonRoute.ShouldSkipTarget(finalBoss, sinceProgress), Is.EqualTo(skip));

        // 4. Resurrection outranks chant and song upkeep only when it can happen.
        [TestCase(true, true, true, true, true)]
        [TestCase(true, true, false, true, false)]   // no power: keep chanting
        [TestCase(true, true, true, false, false)]   // nobody waiting in range
        [TestCase(false, true, true, true, false)]
        public void ResurrectionPausesUpkeep(bool spell, bool grouped, bool power, bool waiting, bool pause) =>
            Assert.That(BotGroupSupport.ResurrectionOutranksUpkeep(spell, grouped, power, waiting), Is.EqualTo(pause));

        // 5. Summoner's Hall is not an autonomous goal; its neighbours still are.
        [Test]
        public void SummonersHallIsNotABotGoal()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(248, "grey noble"), Is.False);
                Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(246, "cavernous yeti"), Is.True);
                Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(277, "decayed dire wolf"), Is.True);
            });
        }
    }
}

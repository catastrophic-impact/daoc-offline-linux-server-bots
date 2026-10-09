using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Gamebots deal with aggressive monsters on their route before walking into them.</summary>
    [TestFixture]
    public class UT_RouteThreatAwarenessOct5
    {
        [TestCase(ConColor.BLUE, false, 1, false, true, 0, true, ExpectedResult = RouteThreatAction.Pull, TestName = "Solo: an even single monster is pulled")]
        [TestCase(ConColor.YELLOW, false, 1, false, false, 0, true, ExpectedResult = RouteThreatAction.Pull, TestName = "Solo: a yellow single is pulled")]
        [TestCase(ConColor.ORANGE, false, 1, false, true, 0, true, ExpectedResult = RouteThreatAction.Detour, TestName = "Solo: an orange is avoided when possible")]
        [TestCase(ConColor.BLUE, false, 3, false, true, 0, true, ExpectedResult = RouteThreatAction.Detour, TestName = "Solo: a pack is avoided when possible")]
        [TestCase(ConColor.ORANGE, false, 1, false, false, 0, true, ExpectedResult = RouteThreatAction.Pull, TestName = "Solo: no way around, a single orange is fought")]
        [TestCase(ConColor.ORANGE, false, 1, false, true, 2, true, ExpectedResult = RouteThreatAction.Pull, TestName = "Solo: after two detours, a single orange is fought")]
        [TestCase(ConColor.RED, false, 1, false, false, 0, true, ExpectedResult = RouteThreatAction.Retarget, TestName = "Solo: a red with no way around ends the camp")]
        [TestCase(ConColor.RED, false, 1, false, false, 0, false, ExpectedResult = RouteThreatAction.Ignore, TestName = "Solo: no second give-up within 10 minutes")]
        [TestCase(ConColor.BLUE, false, 1, true, true, 0, true, ExpectedResult = RouteThreatAction.Detour, TestName = "An epic monster is never pulled on the way")]
        [TestCase(ConColor.BLUE, false, 1, true, false, 0, true, ExpectedResult = RouteThreatAction.Retarget, TestName = "An epic monster with no way around ends the camp")]
        [TestCase(ConColor.ORANGE, true, 3, false, true, 0, true, ExpectedResult = RouteThreatAction.Pull, TestName = "Group: an orange pack of three is pulled")]
        [TestCase(ConColor.RED, true, 2, false, true, 0, true, ExpectedResult = RouteThreatAction.Detour, TestName = "Group: a red is avoided when possible")]
        [TestCase(ConColor.RED, true, 2, false, false, 0, true, ExpectedResult = RouteThreatAction.Pull, TestName = "Group: no way around, a red pair is fought")]
        [TestCase(ConColor.PURPLE, true, 1, false, false, 0, true, ExpectedResult = RouteThreatAction.Retarget, TestName = "Group: a purple with no way around ends the camp")]
        [TestCase(ConColor.GREY, false, 6, false, false, 0, true, ExpectedResult = RouteThreatAction.Ignore, TestName = "Solo: a grey pack is no threat")]
        [TestCase(ConColor.GREEN, false, 3, false, true, 0, true, ExpectedResult = RouteThreatAction.Pull, TestName = "Solo: a green pack of three is pulled")]
        [TestCase(ConColor.GREEN, false, 5, false, false, 0, true, ExpectedResult = RouteThreatAction.Pull, TestName = "Solo: no way around, a green pack of five is fought")]
        [TestCase(ConColor.BLUE, false, 3, false, false, 0, true, ExpectedResult = RouteThreatAction.Pull, TestName = "Solo: no way around, a blue pack of three is fought")]
        [TestCase(ConColor.BLUE, false, 4, false, false, 0, true, ExpectedResult = RouteThreatAction.Retarget, TestName = "Solo: a blue pack of four with no way around ends the camp")]
        [TestCase(ConColor.GREY, false, 1, true, false, 0, true, ExpectedResult = RouteThreatAction.Retarget, TestName = "A grey epic monster still counts")]
        public RouteThreatAction TheFirstThreatOnTheRouteIsHandled(ConColor con, bool grouped, int pack, bool boss,
            bool detour, int detoursSoFar, bool mayRetarget) =>
            AutonomousRouteThreatPolicy.Decide(con, grouped, pack, boss, detour, detoursSoFar, mayRetarget);

        [Test]
        public void TheNearestRoutePointAndDirectionAreFound()
        {
            var route = new[] { new Vector3(0, 0, 0), new Vector3(1000, 0, 0), new Vector3(1000, 1000, 0) };
            Assert.That(AutonomousRouteThreatPolicy.NearestOnRoute(route, new Vector3(400, 300, 0), 1500,
                out Vector3 point, out Vector2 direction), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(point.X, Is.EqualTo(400).Within(0.01));
                Assert.That(point.Y, Is.EqualTo(0).Within(0.01));
                Assert.That(direction.X, Is.EqualTo(1).Within(0.001));
            });
        }

        [Test]
        public void ADetourWhoseStraightLegCutsThroughTheAggroIsRejected()
        {
            // Bot at the origin heading east; a monster with 450 aggro sits 500 ahead, 100 to the side.
            var start = new Vector3(0, 0, 0);
            var threat = new Vector3(500, 100, 0);
            var rejoin = new Vector3(1600, 0, 0);
            Vector3 beside = AutonomousRouteThreatPolicy.DetourPoint(threat, Vector2.UnitX, 450, 220, 0, 1, 0f);
            Vector3 before = AutonomousRouteThreatPolicy.DetourPoint(threat, Vector2.UnitX, 450, 220, 0, 1, -1f);
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousRouteThreatPolicy.LegsClear(start, beside, rejoin, threat, 560), Is.False,
                    "Walking straight to the point beside the monster passes inside its aggro.");
                Assert.That(AutonomousRouteThreatPolicy.SegmentDistance2D(start, rejoin, threat), Is.EqualTo(100).Within(0.01));
            });
            // Swinging wide earlier keeps the first leg clear.
            Assert.That(AutonomousRouteThreatPolicy.SegmentDistance2D(start, before, threat), Is.GreaterThan(450));
        }

        [Test]
        public void DetourPointsStayOutsideTheAggroRange()
        {
            var threat = new Vector3(500, 100, 0);
            var (left, right) = AutonomousRouteThreatPolicy.DetourPoints(threat, Vector2.UnitX, 450, 220, 0);
            float clearance = 450 + 220 + AutonomousRouteThreatPolicy.DetourClearance;
            Assert.Multiple(() =>
            {
                Assert.That(Vector3.Distance(left, threat), Is.EqualTo(clearance).Within(0.01));
                Assert.That(Vector3.Distance(right, threat), Is.EqualTo(clearance).Within(0.01));
                Assert.That(left.X, Is.EqualTo(500).Within(0.01), "Beside the monster, perpendicular to the route.");
                Assert.That(left.Y, Is.Not.EqualTo(right.Y));
            });
        }
    }
}

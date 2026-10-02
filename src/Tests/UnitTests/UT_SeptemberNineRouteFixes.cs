using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_SeptemberNineRouteFixes
    {
        [Test]
        public void EightMembersCrossWithoutDeclaringPartialPartyReady()
        {
            Vector3 source = new(1000, 2000, 300), inside = new(30000, 30000, 16000);
            var members = new AutonomousDungeonPolicy.GroupTransitMember[8];
            for (int i = 0; i < members.Length; i++)
                members[i] = new(200, source + new Vector3(i * 30, 0, 0), false);
            for (int entered = 0; entered < members.Length; entered++)
            {
                Assert.That(AutonomousDungeonPolicy.GroupReadyAtInteriorStaging(221, inside, members), Is.False);
                Assert.That(AutonomousDungeonPolicy.GroupReadyForDungeonEntrance(200, 221, source, members), Is.True);
                members[entered] = new(221, inside + new Vector3(entered * 20, 0, 0), false);
            }
            Assert.That(AutonomousDungeonPolicy.GroupReadyAtInteriorStaging(221, inside, members), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ApproachAndMoverFitInsideAttendance(bool tight)
        {
            int approach = AutonomousRendezvousAttendance.ApproachRadius(tight);
            Vector3 worstStop = new(approach + AutonomousRendezvousAttendance.EndpointTolerance, 0, 0);
            Assert.That(AutonomousRendezvousAttendance.IsAtSlot(worstStop, Vector3.Zero, tight), Is.True);
            Assert.That(AutonomousWorldBotController.IsRouteDestinationReached(new(30, 0, 0), Vector3.Zero, true), Is.False);
            Assert.That(AutonomousWorldBotController.IsRouteDestinationReached(new(30, 0, 0), Vector3.Zero), Is.True,
                "General travel keeps its existing completion tolerance");
            Assert.That(AutonomousWorldBotController.IsRouteDestinationReached(new(0, 0, 50), Vector3.Zero, true), Is.False);
        }

        [TestCase(2, true)]
        [TestCase(24, true)]
        [TestCase(32, true)]
        [TestCase(33, false)]
        [TestCase(250, false)]
        public void NativeEntranceFloorOffsetDoesNotRemoveRealPortal(int dz, bool expected)
        {
            Assert.That(AutonomousDungeonGoalCatalog.MatchesEntrance(new(30000, 31000, 16000),
                new(30000, 31000, 16000 + dz)), Is.EqualTo(expected));
            Assert.That(AutonomousDungeonGoalCatalog.MatchesEntrance(new(30000, 31000, 16000),
                new(30100, 31000, 16000)), Is.False, "Do not admit a different entrance through a wall");
        }

        [TestCase("orchard nipper", true)]
        [TestCase("lugradan whelp", true)]
        [TestCase("luricaduane", true)]
        [TestCase("hill toad", true)]
        [TestCase("decaying spirit", false)]
        public void LiveCampRebindingIsScopedToAuditedNames(string name, bool expected)
        {
            Assert.That(AutonomousAuditedCampPolicy.UsesLiveAnchor(200, name), Is.EqualTo(expected));
            Assert.That(AutonomousAuditedCampPolicy.UsesLiveAnchor(1, name), Is.False);
        }

        [TestCase(51, "large dragonfly")]
        [TestCase(151, "boobrie hatchling")]
        [TestCase(200, "feccan")]
        [TestCase(100, "huldu outcast")]
        [TestCase(100, "green serpent")]
        public void RepeatedOutdoorRouteFailuresRequirePerMobApproachProof(int region, string name)
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousAuditedCampPolicy.UsesLiveAnchor((ushort)region, name), Is.True);
                Assert.That(AutonomousAuditedCampPolicy.RequiresVerifiedTargetRoute((ushort)region, name), Is.True);
            });
        }

        [Test]
        public void OrdinaryOutdoorCampsKeepTheCheapTargetLookup()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousAuditedCampPolicy.RequiresVerifiedTargetRoute(1, "brown drake"), Is.False);
                Assert.That(AutonomousAuditedCampPolicy.RequiresVerifiedTargetRoute(200, "orchard nipper"), Is.False,
                    "The older live-anchor repair did not have per-mob route failure evidence");
            });
        }
    }
}

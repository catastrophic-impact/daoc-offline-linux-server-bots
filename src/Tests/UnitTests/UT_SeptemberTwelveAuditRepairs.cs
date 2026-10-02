using DOL.GS;
using System.Numerics;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_SeptemberTwelveAuditRepairs
    {
        [Test]
        public void OldFrontierRouteFailureCannotRejectANewDestination()
        {
            Vector3 old = new(475835,343661,4080);
            Vector3 next = new(474608,295612,3848);
            Assert.That(AutonomousRouteRecoveryPolicy.AppliesToCurrentDestination(true,old,next),Is.False);
            Assert.That(AutonomousRouteRecoveryPolicy.AppliesToCurrentDestination(false,old,next),Is.True,
                "An intermediate seam failure on an unchanged route remains real");
            Assert.That(AutonomousRouteRecoveryPolicy.AppliesToCurrentDestination(true,next,next),Is.True);
        }
        [TestCase(eRealm.Albion, 1)]
        [TestCase(eRealm.Midgard, 100)]
        [TestCase(eRealm.Hibernia, 200)]
        public void ReturningPvePassengersMayUseTheirOwnHomeTicket(eRealm realm, int home)
        {
            var passage = AutonomousFrontierTransport.Destination(realm, (ushort)home);
            Assert.That(AutonomousFrontierTransport.CanBoardForObjective(realm, eAutonomousObjectiveKind.SoloPve, passage), Is.True);
            Assert.That(AutonomousFrontierTransport.CanBoardForObjective(realm, eAutonomousObjectiveKind.GroupPve, passage), Is.True);
        }

        [Test]
        public void PveCannotUseOutboundOrWrongRealmHomeTickets()
        {
            Assert.That(AutonomousFrontierTransport.CanBoardForObjective(eRealm.Albion, eAutonomousObjectiveKind.SoloPve,
                AutonomousFrontierTransport.Destination(eRealm.Albion, 100)), Is.False);
            Assert.That(AutonomousFrontierTransport.CanBoardForObjective(eRealm.Albion, eAutonomousObjectiveKind.SoloPve,
                AutonomousFrontierTransport.Destination(eRealm.Midgard, 100)), Is.False);
            Assert.That(AutonomousFrontierTransport.CanBoardForObjective(eRealm.Albion, eAutonomousObjectiveKind.RvR,
                AutonomousFrontierTransport.Destination(eRealm.Albion, 100)), Is.True);
        }

        [Test]
        public void OnlyUnsafeSpindelhallaHuskGoalsAreExcluded()
        {
            Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(125, "Husk"), Is.False);
            Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(125, "svartalf thrall"), Is.True);
            Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(128, "husk"), Is.True);
            Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(129, "haunt"), Is.False);
        }

        [Test]
        public void MeetupBoardingKeepsNearbyHorsesButRejectsLongDetours()
        {
            Assert.That(AutonomousStableRoutePlanner.CanApproachStableDuringMeetup(24000, 200), Is.True);
            Assert.That(AutonomousStableRoutePlanner.CanApproachStableDuringMeetup(150000, 200), Is.False);
            Assert.That(AutonomousStableRoutePlanner.CanApproachStableDuringMeetup(10, 0), Is.False);
            Assert.That(AutonomousStableRoutePlanner.CanApproachStableDuringMeetup(double.NaN, 200), Is.False);
        }

        [Test]
        public void ResurrectionGraceIsForAnAlreadyStartedCastAndIsBounded()
        {
            Assert.That(BotGroupSupport.CanFinishResurrectionBeforeRelease(59000, 60000, 61000, 10000), Is.True);
            Assert.That(BotGroupSupport.CanFinishResurrectionBeforeRelease(61000, 60000, 62000, 10000), Is.False);
            Assert.That(BotGroupSupport.CanFinishResurrectionBeforeRelease(59000, 60000, 71000, 10000), Is.False);
            Assert.That(BotGroupSupport.CanFinishResurrectionBeforeRelease(0, 60000, 61000, 10000), Is.False);
        }
    }
}

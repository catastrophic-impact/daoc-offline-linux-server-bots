using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_SeptemberThirteenAuditRepairs
    {
        [Test]
        public void SharedProgressProtectsNearbySupportButNotStrandedMembers()
        {
            Assert.That(AutonomousBotGroupCoordinator.HasRecentSharedWatchdogProgress(10000, 1000, true, 400), Is.True);
            Assert.That(AutonomousBotGroupCoordinator.HasRecentSharedWatchdogProgress(10000, 1000, false, 0), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.HasRecentSharedWatchdogProgress(10000, 1000, true, 5000), Is.False);
        }

        [Test]
        public void SharedProgressProtectionExpiresAndRejectsInvalidClocks()
        {
            Assert.That(AutonomousBotGroupCoordinator.HasRecentSharedWatchdogProgress(901000, 1000, true, 10), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.HasRecentSharedWatchdogProgress(1000, 0, true, 10), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.HasRecentSharedWatchdogProgress(1000, 2000, true, 10), Is.False);
        }

        [Test]
        public void LocalMeetupDoesNotBoardAnotherHorse()
        {
            Assert.That(AutonomousStableRoutePlanner.FinishMeetupOnFoot(true, 1999 * 1999), Is.True);
            Assert.That(AutonomousStableRoutePlanner.FinishMeetupOnFoot(true, 2001 * 2001), Is.False);
            Assert.That(AutonomousStableRoutePlanner.FinishMeetupOnFoot(false, 1), Is.False);
            Assert.That(AutonomousStableRoutePlanner.FinishMeetupOnFoot(true, float.NaN), Is.False);
        }

        [Test]
        public void BoardingDeadlineCannotBeExtendedByShufflingAndDoesNotAffectOtherTravel()
        {
            Assert.That(AutonomousStableRoutePlanner.MeetupBoardingExpired(true, 1000, 120999), Is.False);
            Assert.That(AutonomousStableRoutePlanner.MeetupBoardingExpired(true, 1000, 121000), Is.True);
            Assert.That(AutonomousStableRoutePlanner.MeetupBoardingExpired(false, 1000, 999999), Is.False);
            Assert.That(AutonomousStableRoutePlanner.MeetupBoardingExpired(true, 1000, 999), Is.False);
        }
    }
}

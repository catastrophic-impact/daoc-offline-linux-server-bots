using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>2026-10-16: an automatic Tuscaran rally spent 84 of its 90 staging minutes gathering 200 at Hagall.</summary>
    [TestFixture]
    public class UT_RaidRallyTiming
    {
        private const long Minute = 60_000;

        [Test]
        public void SlowHubLeavesAfterHalfAnHourWithEnough()
        {
            Assert.That(RealmRaidRecruitmentPolicy.DepartHub(false, 200, 0), Is.True);
            Assert.That(RealmRaidRecruitmentPolicy.DepartHub(false, 150, 29 * Minute), Is.False);
            Assert.That(RealmRaidRecruitmentPolicy.DepartHub(false, 150, 30 * Minute), Is.True);
            Assert.That(RealmRaidRecruitmentPolicy.DepartHub(false, 119, 60 * Minute), Is.False);
        }

        [Test]
        public void StagingAlwaysGetsTimeAfterLeavingTheHub()
        {
            long created = 1_000;
            Assert.That(RealmRaidRecruitmentPolicy.StagingDeadline(created, 0), Is.EqualTo(created + 90 * Minute));
            Assert.That(RealmRaidRecruitmentPolicy.StagingDeadline(created, created + 10 * Minute), Is.EqualTo(created + 90 * Minute));
            long lateDeparture = created + 84 * Minute;
            Assert.That(RealmRaidRecruitmentPolicy.StagingDeadline(created, lateDeparture), Is.EqualTo(lateDeparture + 45 * Minute));
            Assert.That(RealmRaidRecruitmentPolicy.StagingExpired(false, created + 91 * Minute, created, lateDeparture, 93, true), Is.False);
            Assert.That(RealmRaidRecruitmentPolicy.StagingExpired(false, lateDeparture + 45 * Minute, created, lateDeparture, 93, true), Is.True);
            Assert.That(RealmRaidRecruitmentPolicy.StagingExpired(true, lateDeparture + 300 * Minute, created, lateDeparture, 0, true), Is.False);
        }
    }
}

using System.Linq;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_CompanionRaidSiege
    {
        [Test]
        public void RaidSlotsFormSquadsOfEight()
        {
            Assert.That(CompanionRaidSiege.SquadOf(1), Is.EqualTo(0));
            Assert.That(CompanionRaidSiege.SquadOf(8), Is.EqualTo(0));
            Assert.That(CompanionRaidSiege.SquadOf(9), Is.EqualTo(1));
            Assert.That(CompanionRaidSiege.SquadOf(39), Is.EqualTo(4));
            Assert.That(CompanionRaidSiege.SquadOf(79), Is.EqualTo(9));
        }

        [Test]
        public void Raid40AssaultHasBodyguardRamArtilleryWallClearersAndAssault()
        {
            var jobs = Enumerable.Range(0, 5).Select(i => CompanionRaidSiege.JobFor(CompanionRaidSiege.Mode.Assault, i, 5)).ToArray();
            Assert.That(jobs, Is.EqualTo(new[] { CompanionRaidSiege.Job.Bodyguard, CompanionRaidSiege.Job.Ram,
                CompanionRaidSiege.Job.Catapult, CompanionRaidSiege.Job.WallClear, CompanionRaidSiege.Job.Assault }));
        }

        [Test]
        public void Raid80BringsTwoRamsATrebuchetAndABallista()
        {
            var jobs = Enumerable.Range(0, 10).Select(i => CompanionRaidSiege.JobFor(CompanionRaidSiege.Mode.Assault, i, 10)).ToArray();
            Assert.That(jobs.Count(j => j == CompanionRaidSiege.Job.Ram), Is.EqualTo(2));
            Assert.That(jobs, Does.Contain(CompanionRaidSiege.Job.Trebuchet));
            Assert.That(jobs, Does.Contain(CompanionRaidSiege.Job.Ballista));
            Assert.That(jobs[0], Is.EqualTo(CompanionRaidSiege.Job.Bodyguard));
            Assert.That(jobs.Count(j => j == CompanionRaidSiege.Job.Assault), Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void DefenseHoldsDoorsWithCourtyardEngines()
        {
            var jobs = Enumerable.Range(0, 5).Select(i => CompanionRaidSiege.JobFor(CompanionRaidSiege.Mode.Defense, i, 5)).ToArray();
            Assert.That(jobs[0], Is.EqualTo(CompanionRaidSiege.Job.Bodyguard));
            Assert.That(jobs, Does.Contain(CompanionRaidSiege.Job.Ballista));
            Assert.That(jobs, Does.Contain(CompanionRaidSiege.Job.Catapult));
            Assert.That(jobs, Does.Not.Contain(CompanionRaidSiege.Job.Ram));
            Assert.That(CompanionRaidSiege.EngineFor(CompanionRaidSiege.Job.Defend), Is.Null);
            Assert.That(CompanionRaidSiege.EngineFor(CompanionRaidSiege.Job.Ram), Is.EqualTo(BotSiegeKind.Ram));
        }
    }
}

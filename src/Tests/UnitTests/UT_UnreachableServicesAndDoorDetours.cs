using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_UnreachableServicesAndDoorDetours
    {
        [SetUp]
        public void Reset() => AutonomousServiceReachability.Clear();

        [Test]
        public void AMerchantOneBotCannotReachIsSkippedByThatBotOnly()
        {
            Assert.That(AutonomousServiceReachability.ReportFailure("kel", 1, 0), Is.False);
            Assert.That(AutonomousServiceReachability.IsSkipped("kel", 1, 1_000), Is.True);
            Assert.That(AutonomousServiceReachability.IsSkipped("kel", 2, 1_000), Is.False);
            Assert.That(AutonomousServiceReachability.IsSkipped("kel", 1, AutonomousServiceReachability.SkipMilliseconds), Is.False);
        }

        [Test]
        public void ASecondBotFailingClosesTheMerchantForEveryBot()
        {
            AutonomousServiceReachability.ReportFailure("kuz", 1, 0);
            // The same bot failing again does not count as a second opinion.
            Assert.That(AutonomousServiceReachability.ReportFailure("kuz", 1, 5_000), Is.False);
            Assert.That(AutonomousServiceReachability.IsSkipped("kuz", 3, 6_000), Is.False);
            Assert.That(AutonomousServiceReachability.ReportFailure("kuz", 2, 10_000), Is.True);
            Assert.That(AutonomousServiceReachability.IsSkipped("kuz", 3, 11_000), Is.True);
            Assert.That(AutonomousServiceReachability.IsSkipped("kuz", 3, 10_000 + AutonomousServiceReachability.SkipMilliseconds), Is.False);
        }

        [Test]
        public void AFarServiceIsGivenUpOnlyWithoutProgressForAMinute()
        {
            var failures = new AutonomousServiceReachability.ApproachFailures();
            // The Camelot broker from across the city: the bot keeps getting closer, so it keeps walking.
            Assert.That(failures.Observe("adalyn", 6_000, 0), Is.False);
            Assert.That(failures.Observe("adalyn", 5_000, 30_000), Is.False);
            Assert.That(failures.Observe("adalyn", 4_000, 61_000), Is.False);
            Assert.That(failures.Observe("adalyn", 3_000, 125_000), Is.False);
            // Kel on the Grimspound hill: no closer for a minute.
            Assert.That(failures.Observe("kel", 16_000, 0), Is.False);
            Assert.That(failures.Observe("kel", 15_800, 30_000), Is.False);
            Assert.That(failures.Observe("kel", 16_100, 45_000), Is.False);
            Assert.That(failures.Observe("kel", 15_900, 61_000), Is.True);
        }

        [Test]
        public void ARealmWithFewerThanAWaveInABattlegroundDoesNotAssault()
        {
            // Thidranki on 2026-10-17: one or two bots per realm.
            Assert.That(AutonomousBattlegroundWave.IsTooFew(2), Is.True);
            Assert.That(AutonomousBattlegroundWave.IsTooFew(AutonomousBattlegroundWave.MinimumWave - 1), Is.True);
            Assert.That(AutonomousBattlegroundWave.IsTooFew(AutonomousBattlegroundWave.MinimumWave), Is.False);
        }

        [Test]
        public void ACamelotPocketWithItsOwnMarginLeavesTheStreetAboveAlone()
        {
            var data = AutonomousZonePockets.Parse("{\"version\":1,\"cell\":512,\"zones\":{\"26\":[[67,60,7905,7935,20]],\"0\":[[1,1,100,200]]}}");
            Assert.That(AutonomousZonePockets.Contains(data, 26, 34412, 30739, 7920), Is.True);
            // The real street 90 units above the sunken patch.
            Assert.That(AutonomousZonePockets.Contains(data, 26, 34412, 30739, 8010), Is.False);
            // Entries without a margin keep the old 96.
            Assert.That(AutonomousZonePockets.Contains(data, 0, 700, 700, 290), Is.True);
        }

        [Test]
        public void ATeleporterWalkIsPricedByItsRoute()
        {
            float length = AutonomousTownTeleporters.RouteLength(new Vector3(0, 0, 0),
                new[] { new Vector3(0, 10_000, 0), new Vector3(12_000, 10_000, 0) }, new Vector3(12_000, 0, 0));
            Assert.That(length, Is.EqualTo(32_000).Within(1));
        }

        [Test]
        public void AFailedDoorDetourWaitsUntilItsTimeIsUpOrTheTargetMoves()
        {
            Vector3 target = new(1000, 1000, 0);
            long until = Pathfinder.DoorDetourRetryMilliseconds;
            Assert.That(Pathfinder.ShouldRetryDoorDetour(1_000, until, target, target), Is.False);
            Assert.That(Pathfinder.ShouldRetryDoorDetour(1_000, until, target, target + new Vector3(40, 0, 0)), Is.False);
            Assert.That(Pathfinder.ShouldRetryDoorDetour(1_000, until, target, target + new Vector3(100, 0, 0)), Is.True);
            Assert.That(Pathfinder.ShouldRetryDoorDetour(until, until, target, target), Is.True);
        }
    }
}

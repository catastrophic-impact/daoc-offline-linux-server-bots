using System;
using System.Numerics;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    [TestFixture]
    public sealed class UT_AutonomousTownIdle
    {
        [TestCase(0, true)]
        [TestCase(.149999, true)]
        [TestCase(.15, false)]
        [TestCase(.99999, false)]
        public void BoundaryRollIsExactlyFifteenPercent(double roll, bool expected)
        {
            Assert.That(AutonomousTownDowntime.RollAtTaskBoundary(roll), Is.EqualTo(expected));
        }

        [TestCase("TID", true, true, true)]
        [TestCase("TD", true, false, true)]
        [TestCase("ID", false, true, true)]
        [TestCase("D", false, false, true)]
        [TestCase("TI", true, true, false)]
        [TestCase("", false, false, false)]
        public void OptionalIdleDoesNotOverrideTrainingOrInventoryFlags(string flags, bool train, bool inventory, bool idle)
        {
            var record = new OfflineWorldBotRecord { ObjectiveAssignmentId = "between-pve-services-" + flags + "-D-T-I" };
            Assert.That(AutonomousObjectiveAssignments.HasBetweenTaskFlag(record, 'T'), Is.EqualTo(train));
            Assert.That(AutonomousObjectiveAssignments.HasBetweenTaskFlag(record, 'I'), Is.EqualTo(inventory));
            Assert.That(AutonomousObjectiveAssignments.HasBetweenTaskFlag(record, 'D'), Is.EqualTo(idle));
            Assert.That(AutonomousObjectiveAssignments.RollBetweenTaskPlan(true, true, .899, .949),
                Is.EqualTo(new AutonomousObjectiveAssignments.BetweenTaskPlan(true, true)));
            Assert.That(AutonomousObjectiveAssignments.RollBetweenTaskPlan(true, true, .90, .95),
                Is.EqualTo(new AutonomousObjectiveAssignments.BetweenTaskPlan(false, false)));
        }

        [TestCase(14.99)]
        [TestCase(0)]
        [TestCase(-1)]
        public void SkipIfCannotHonorTheMinimumWithoutExtendingMaintenance(double remaining)
        {
            Assert.That(AutonomousTownDowntime.RollWithinBudget(TimeSpan.FromMinutes(remaining)), Is.Null);
        }

        [TestCase(15)]
        [TestCase(20)]
        [TestCase(30)]
        [TestCase(60)]
        public void DurationStartsAtArrivalAndFitsTheOriginalBudget(int remaining)
        {
            Assert.That(AutonomousTownDowntime.MinimumDuration, Is.EqualTo(TimeSpan.FromMinutes(15)));
            Assert.That(AutonomousTownDowntime.MaximumDuration, Is.EqualTo(TimeSpan.FromMinutes(30)));
            for (int seed = 0; seed < 200; seed++)
                Assert.That(AutonomousTownDowntime.RollWithinBudget(TimeSpan.FromMinutes(remaining), new Random(seed)).Value,
                    Is.InRange(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(Math.Min(30, remaining))));
            Assert.That(AutonomousObjectiveAssignments.MaximumBetweenTaskDuration, Is.EqualTo(TimeSpan.FromMinutes(30)));
        }

        [TestCase(1, false, 20, 30, false)]
        [TestCase(25, false, 20, 30, true)]
        [TestCase(50, false, 1, 10, false)]
        [TestCase(1, true, 0, 0, true)]
        [TestCase(50, true, 0, 0, true)]
        [TestCase(10, false, 0, 0, false)]
        public void TownsUseLocalLevelCoverageAndCapitalsSupportAllLevels(int level, bool capital, int min, int max, bool allowed)
        {
            Assert.That(AutonomousWorldBotController.IsIdleTownLevelAppropriate(level, capital, min, max), Is.EqualTo(allowed));
        }

        [TestCase(PathfindingStatus.PathFound, 3, 0, true)]
        [TestCase(PathfindingStatus.PartialPathFound, 3, 0, false)]
        [TestCase(PathfindingStatus.BufferTooSmall, 3, 0, false)]
        [TestCase(PathfindingStatus.NavmeshUnavailable, 0, 0, false)]
        [TestCase(PathfindingStatus.NoPathFound, 0, 0, false)]
        [TestCase(PathfindingStatus.PathFound, 3, 100, false)]
        public void ReachabilityRequiresACompleteCorridorEndingAtTheDestination(PathfindingStatus status, int nodes, int distance, bool complete)
        {
            Assert.That(AutonomousWorldBotController.IsCompleteTownCorridor(new(status, nodes), new(distance, 0, 0), Vector3.Zero), Is.EqualTo(complete));
        }

        [Test]
        public void IntentionalIdleExemptionNeverCoversTravelOrAnExpiredTask()
        {
            DateTime now = DateTime.UtcNow;
            var record = new OfflineWorldBotRecord { ObjectiveAssignmentId = "between-pve-services-TID-1-1",
                Activity = AutonomousWorldBotController.TownIdlePhase, ObjectivePhase = AutonomousWorldBotController.TownIdlePhase,
                ObjectiveExpiresUtc = now.AddMinutes(20).ToString("O") };
            Assert.That(AutonomousObjectiveAssignments.IsIntentionalTownIdle(record, now), Is.True);
            Assert.That(AutonomousObjectiveAssignments.IsIntentionalTownIdle(record, now.AddMinutes(20)), Is.False);
            record.Activity = "Traveling to town";
            Assert.That(AutonomousObjectiveAssignments.IsIntentionalTownIdle(record, now), Is.False);
            record.Activity = AutonomousWorldBotController.TownIdlePhase;
            record.ObjectiveAssignmentId = "solo-pve-1";
            Assert.That(AutonomousObjectiveAssignments.IsIntentionalTownIdle(record, now), Is.False);
        }
    }
}

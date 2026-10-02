using System;
using System.Text.Json;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    [TestFixture]
    public sealed class UT_AutonomousGoalDiagnostics
    {
        private static AutonomousGoalAttempt Attempt() => new()
        {
            CampId = "hib:large-frog:1", Target = "large frog", Region = 200,
            X = 10000, Y = 10000, Z = 5000,
            StartedUtc = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            ExpiresUtc = new DateTime(2026, 8, 30, 1, 0, 0, DateTimeKind.Utc)
        };

        [Test]
        public void TargetMatchIncludesRegionCampAndVerticalLayer()
        {
            var attempt = Attempt();
            Assert.Multiple(() =>
            {
                Assert.That(attempt.MatchesTarget("Large Frog", 200, 10100, 10100, 5100), Is.True);
                Assert.That(attempt.MatchesTarget("large frog", 181, 10100, 10100, 5100), Is.False);
                Assert.That(attempt.MatchesTarget("large frog", 200, 20000, 10000, 5000), Is.False);
                Assert.That(attempt.MatchesTarget("large frog", 200, 10000, 10000, 7000), Is.False);
                Assert.That(attempt.MatchesTarget("bull frog", 200, 10000, 10000, 5000), Is.False);
            });
        }

        [Test]
        public void UnrelatedXpCannotMakeAnUnreachableGoalLookProductive()
        {
            var attempt = Attempt();
            attempt.Reward(false, 100);
            Assert.Multiple(() =>
            {
                Assert.That(attempt.Kills, Is.EqualTo(1));
                Assert.That(attempt.Experience, Is.EqualTo(100));
                Assert.That(attempt.TargetKills, Is.Zero);
                Assert.That(AutonomousGoalAttempt.Classify(GoalAttemptEnd.NoProgress45m, attempt.TargetKills), Is.EqualTo("failed"));
            });
        }

        [TestCase(GoalAttemptEnd.Logout)]
        [TestCase(GoalAttemptEnd.GroupChanged)]
        [TestCase(GoalAttemptEnd.ServiceDetour)]
        [TestCase(GoalAttemptEnd.Reassigned)]
        public void InterruptedAttemptsDoNotCountAsFailures(GoalAttemptEnd reason)
        {
            Assert.That(AutonomousGoalAttempt.Classify(reason, 0), Is.EqualTo("interrupted"));
        }

        [Test]
        public void FinishIsExactlyOnceAndIgnoresLateEvents()
        {
            var attempt = Attempt();
            attempt.Arrive(attempt.StartedUtc.AddMinutes(2));
            attempt.Reward(true, 55);
            attempt.Died();
            attempt.RouteFailed("NoPathFound");
            object result = attempt.Finish(GoalAttemptEnd.Defeated, "test", attempt.StartedUtc.AddMinutes(10), 200, 1, 2, 3);
            attempt.Reward(true, 100);
            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Not.Null);
                Assert.That(attempt.TargetKills, Is.EqualTo(1));
                Assert.That(attempt.TargetExperience, Is.EqualTo(55));
                Assert.That(attempt.RecoveryAttempts, Is.EqualTo(1));
                Assert.That(attempt.Deaths, Is.EqualTo(1));
                Assert.That(attempt.Finish(GoalAttemptEnd.Logout, "duplicate", DateTime.UtcNow, 0, 0, 0, 0), Is.Null);
            });
        }

        [Test]
        public void ExpiryWithNoAssignedKillsIsNotHiddenByReassignment()
        {
            var attempt = Attempt();
            object result = attempt.Finish(GoalAttemptEnd.Reassigned, "expiry", attempt.ExpiresUtc.AddSeconds(1), 200, 1, 2, 3);
            using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.Multiple(() =>
            {
                Assert.That(json.RootElement.GetProperty("reason").GetString(), Is.EqualTo("Expired"));
                Assert.That(json.RootElement.GetProperty("outcome").GetString(), Is.EqualTo("failed"));
            });
        }

        [Test]
        public void SuccessfulRecoveryIsNotReportedAsTerminalRouteFailure()
        {
            var attempt = Attempt();
            attempt.RouteFailed("NoPathFound");
            attempt.Reward(true, 50);
            object result = attempt.Finish(GoalAttemptEnd.Reassigned, "productive", attempt.StartedUtc.AddMinutes(5), 200, 10, 20, 30);
            using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.Multiple(() =>
            {
                Assert.That(json.RootElement.GetProperty("recoveryAttempts").GetInt32(), Is.EqualTo(1));
                Assert.That(json.RootElement.GetProperty("terminalRouteFailures").GetInt32(), Is.Zero);
                Assert.That(json.RootElement.GetProperty("endX").GetInt32(), Is.EqualTo(10));
                Assert.That(json.RootElement.GetProperty("endY").GetInt32(), Is.EqualTo(20));
                Assert.That(json.RootElement.GetProperty("endZ").GetInt32(), Is.EqualTo(30));
            });
        }

        [Test]
        public void DiagnosticHooksIgnoreMissingActors()
        {
            Assert.DoesNotThrow(() => AutonomousGoalDiagnostics.Begin(null, "x", "y", "z", 200, 1, 2, 3, "", 1, 1, ""));
            Assert.DoesNotThrow(() => AutonomousGoalDiagnostics.Reward(null, null, 0));
            Assert.DoesNotThrow(() => AutonomousGoalDiagnostics.End(null, GoalAttemptEnd.Logout, "test"));
        }
    }
}

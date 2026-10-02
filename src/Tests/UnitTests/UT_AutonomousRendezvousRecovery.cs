using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    [TestFixture]
    public sealed class UT_AutonomousRendezvousRecovery
    {
        [Test]
        public void GenericBindRadiusIsNotAdvertisedAsATown()
        {
            var bind = new Area.BindArea("bind point", new DOL.Database.DbBindPoint
            {
                X = 100,
                Y = 100,
                Z = 0,
                Radius = 500,
            });
            var namedTown = new Area.Circle("Humberton Village", 100, 100, 0, 1000);

            Assert.That(AutonomousBotGroupCoordinator.IsNamedRendezvousArea(bind), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.IsNamedRendezvousArea(namedTown), Is.True);
        }

        [TestCase(eAutonomousObjectiveKind.GroupPve, 45, 120)]
        [TestCase(eAutonomousObjectiveKind.RvR, 45, 120)]
        public void SharedClockUsesCorrectGroupDuration(eAutonomousObjectiveKind kind, int minimum, int maximum)
        {
            for (int seed = 0; seed < 500; seed++)
            {
                var clock = new AutonomousGroupTaskClock(kind, new Random(seed));
                Assert.That(clock.DurationMilliseconds, Is.InRange(minimum * 60_000L, maximum * 60_000L));
            }
        }

        [TestCase(eAutonomousObjectiveKind.GroupPve)]
        [TestCase(eAutonomousObjectiveKind.RvR)]
        public void SharedClockStartsOnceAndCannotBeExtendedByRegrouping(eAutonomousObjectiveKind kind)
        {
            var clock = new AutonomousGroupTaskClock(kind, new Random(3));
            DateTime utc = new(2026, 8, 30, 5, 0, 0, DateTimeKind.Utc);
            Assert.That(clock.HasExpired(long.MaxValue), Is.False);
            Assert.That(clock.Start(100, utc), Is.True);
            long deadline = clock.DeadlineTick.Value;
            Assert.That(clock.Start(100 + 20 * 60_000, utc.AddMinutes(20)), Is.False);
            Assert.That(clock.DeadlineTick, Is.EqualTo(deadline));
            Assert.That(clock.ExpiresUtc, Is.EqualTo(utc.AddMilliseconds(clock.DurationMilliseconds)));
            Assert.That(clock.HasExpired(deadline - 1), Is.False);
            Assert.That(clock.HasExpired(deadline), Is.True);
        }

        [TestCase(eAutonomousObjectiveKind.GroupPve)]
        [TestCase(eAutonomousObjectiveKind.RvR)]
        public void OnlyInitialMeetupPausesTaskAndRecoveryCannotExtendIt(eAutonomousObjectiveKind kind)
        {
            var clock = new AutonomousGroupTaskClock(kind, new Random(3));
            var attendance = new AutonomousRendezvousAttendance();
            DateTime utc = new(2026, 8, 30, 5, 0, 0, DateTimeKind.Utc);
            attendance.Add(1, 0, utc);
            Assert.That(clock.RemainingMilliseconds(900_000), Is.EqualTo(clock.DurationMilliseconds));
            Assert.That(attendance.Observe(1, 900_000, false), Is.True);
            clock.Start(900_000, utc.AddMinutes(15));
            long deadline = clock.DeadlineTick.Value;
            // Ten minutes outbound plus fifteen minutes recovering all count.
            long remaining = clock.DurationMilliseconds - 1_500_000;
            Assert.That(clock.RemainingMilliseconds(2_400_000), Is.EqualTo(remaining));
            Assert.That(clock.Start(2_400_000, utc.AddMinutes(40)), Is.False);
            Assert.That(clock.DeadlineTick, Is.EqualTo(deadline));
            Assert.That(clock.ExpiresUtc, Is.EqualTo(utc.AddMinutes(15).AddMilliseconds(clock.DurationMilliseconds)));
            Assert.That(clock.HasExpired(clock.DeadlineTick.Value), Is.True);
        }

        [Test]
        public void CountdownMetadataIsStableUntilArrivalAndClearsAfterIt()
        {
            var attendance = new AutonomousRendezvousAttendance();
            DateTime utc = new(2026, 8, 30, 5, 0, 0, DateTimeKind.Utc);
            attendance.Add(42, 100, utc);
            Assert.That(attendance.DeadlineUtc(42), Is.EqualTo(utc.AddMinutes(15)));
            attendance.Observe(42, 5 * 60_000, false);
            Assert.That(attendance.DeadlineUtc(42), Is.EqualTo(utc.AddMinutes(15)));
            attendance.Observe(42, 6 * 60_000, true);
            Assert.That(attendance.DeadlineUtc(42), Is.Null);
            Assert.That(attendance.Observe(42, 20 * 60_000, true), Is.False);
        }

        [Test]
        public void BetweenTasksRollRequiresRealNeedsAndStronglyFavorsServices()
        {
            Assert.That(AutonomousObjectiveAssignments.RollBetweenTaskPlan(false, false, 0, 0),
                Is.EqualTo(new AutonomousObjectiveAssignments.BetweenTaskPlan(false, false)));
            Assert.That(AutonomousObjectiveAssignments.RollBetweenTaskPlan(true, true, .89, .94),
                Is.EqualTo(new AutonomousObjectiveAssignments.BetweenTaskPlan(true, true)));
            Assert.That(AutonomousObjectiveAssignments.RollBetweenTaskPlan(true, true, .90, .95),
                Is.EqualTo(new AutonomousObjectiveAssignments.BetweenTaskPlan(false, false)));
            Assert.That(AutonomousObjectiveAssignments.RollBetweenTaskPlan(false, true, .01, .5),
                Is.EqualTo(new AutonomousObjectiveAssignments.BetweenTaskPlan(false, true)));
            var record = new OfflineWorldBotRecord { ObjectiveKind = "SoloPve",
                ObjectiveAssignmentId = "between-pve-services-TI-42-1", ObjectiveExpiresUtc = DateTime.UtcNow.AddMinutes(45).ToString("O") };
            Assert.That(AutonomousObjectiveAssignments.IsBetweenPveTasks(record), Is.True);
            Assert.That(AutonomousObjectiveAssignments.HasActivePveAssignment(record, DateTime.UtcNow), Is.False);
        }

        [Test]
        public void RvrRequiresCompletedPveAtEveryEligibleLevel()
        {
            var record = new OfflineWorldBotRecord { Level = 49,
                ObjectiveRvrEligibleUtc = AutonomousObjectiveAssignments.PveCompletionRequired };
            Assert.That(AutonomousObjectiveAssignments.IsRvrEligible(record, DateTime.UtcNow.AddDays(10)), Is.False);
            record.Level = 50;
            Assert.That(AutonomousObjectiveAssignments.IsRvrEligible(record, DateTime.UtcNow), Is.False);
            Assert.That(AutonomousObjectiveAssignments.MinimumRvrTenure, Is.EqualTo(TimeSpan.FromMinutes(45)));
            Assert.That(AutonomousObjectiveAssignments.MaximumRvrTenure, Is.EqualTo(TimeSpan.FromMinutes(120)));
            Assert.That(AutonomousObjectiveAssignments.MaximumBetweenTaskDuration, Is.EqualTo(TimeSpan.FromMinutes(30)));
        }

        [Test]
        public void LegacyContractsNormalizeOnceWithoutRestartingTheirTimers()
        {
            DateTime utc = new(2026, 8, 30, 5, 0, 0, DateTimeKind.Utc);
            var record = new OfflineWorldBotRecord { ObjectiveAssignmentId = "legacy", ObjectiveKind = "SoloPve",
                ObjectivePveMode = "Kills", ObjectivePveKillTarget = 12, ObjectiveAssignedUtc = utc.ToString("O"),
                ObjectiveExpiresUtc = utc.AddMinutes(130).ToString("O") };
            Assert.That(AutonomousObjectiveAssignments.NormalizeLegacyTimedAssignment(record, utc.AddMinutes(10)), Is.True);
            Assert.That(record.ObjectivePveMode, Is.EqualTo("Time"));
            Assert.That(record.ObjectivePveKillTarget, Is.Zero);
            Assert.That(DateTime.Parse(record.ObjectiveExpiresUtc).ToUniversalTime(), Is.EqualTo(utc.AddMinutes(120)));
            Assert.That(AutonomousObjectiveAssignments.NormalizeLegacyTimedAssignment(record, utc.AddMinutes(30)), Is.False);
            record.ObjectiveKind = "RvR";
            record.ObjectiveExpiresUtc = utc.AddMinutes(180).ToString("O");
            Assert.That(AutonomousObjectiveAssignments.NormalizeLegacyTimedAssignment(record, utc.AddMinutes(40)), Is.True);
            Assert.That(DateTime.Parse(record.ObjectiveExpiresUtc).ToUniversalTime(), Is.EqualTo(utc.AddMinutes(120)));
        }

        [Test]
        public void NoShowExpiresAtFifteenNotTenMinutes()
        {
            var attendance = new AutonomousRendezvousAttendance();
            attendance.Add(1, 500);
            Assert.That(attendance.Observe(1, 500 + 10 * 60_000, false), Is.False);
            Assert.That(attendance.Observe(1, 500 + 15 * 60_000 - 1, false), Is.False);
            Assert.That(attendance.Observe(1, 500 + 15 * 60_000, false), Is.True);
            Assert.That(attendance.WaitedMilliseconds(1, 500 + 15 * 60_000), Is.EqualTo(900_000));
        }

        [Test]
        public void RepeatedRouteFailureShortensOnlyItsMembersMeetupDeadline()
        {
            var attendance = new AutonomousRendezvousAttendance();
            DateTime utc = new(2026, 9, 27, 5, 0, 0, DateTimeKind.Utc);
            attendance.Add(1, 0, utc);
            attendance.Add(2, 0, utc);
            attendance.LimitDeadline(1, 60_000, AutonomousBotGroupCoordinator.UnreachableRouteGraceMilliseconds,
                utc.AddMinutes(1));
            Assert.That(attendance.DeadlineUtc(1), Is.EqualTo(utc.AddMinutes(3)));
            Assert.That(attendance.DeadlineUtc(2), Is.EqualTo(utc.AddMinutes(15)));
            // Repeated reports cannot keep extending a proven failure.
            attendance.LimitDeadline(1, 120_000, AutonomousBotGroupCoordinator.UnreachableRouteGraceMilliseconds,
                utc.AddMinutes(2));
            Assert.That(attendance.DeadlineUtc(1), Is.EqualTo(utc.AddMinutes(3)));
            Assert.That(attendance.Observe(1, 3 * 60_000 - 1, false), Is.False);
            Assert.That(attendance.Observe(1, 3 * 60_000, false), Is.True);
            Assert.That(attendance.Observe(2, 3 * 60_000, false), Is.False);
        }

        [Test]
        public void RouteReselectionNeedsARealChangeNotTheSameTownSquare()
        {
            Vector3 original = new(1000, 1000, 100);
            Assert.That(AutonomousBotGroupCoordinator.IsMaterialRendezvousChange(1, original,
                1, new Vector3(1050, 990, 100)), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.IsMaterialRendezvousChange(1, original,
                1, new Vector3(1200, 1000, 100)), Is.True);
            Assert.That(AutonomousBotGroupCoordinator.IsMaterialRendezvousChange(1, original,
                10, original), Is.True);
        }

        [Test]
        public void RosterReductionReassignsSlotsWithoutRestartingEveryMembersMeetupClock()
        {
            var attendance = new AutonomousRendezvousAttendance();
            DateTime utc = new(2026, 9, 27, 5, 0, 0, DateTimeKind.Utc);
            attendance.Add(1, 0, utc);
            attendance.Add(2, 0, utc);
            attendance.RebaseAfterRosterReduction(new long[] { 1 }, 14 * 60_000L,
                utc.AddMinutes(14));
            Assert.That(attendance.DeadlineUtc(1), Is.EqualTo(utc.AddMinutes(16)));
            Assert.That(attendance.DeadlineUtc(2), Is.Null);
            Assert.That(attendance.Observe(1, 16 * 60_000L, false), Is.True);
        }

        [Test]
        public void OutdoorFormationAllowsNaturalSpacingButInteriorStagingStaysTight()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousRendezvousAttendance.IsAtSlot(Vector3.Zero, new Vector3(79, 0, 0), false), Is.True);
                Assert.That(AutonomousRendezvousAttendance.IsAtSlot(Vector3.Zero, new Vector3(81, 0, 0), false), Is.False);
                Assert.That(AutonomousRendezvousAttendance.IsAtSlot(Vector3.Zero, new Vector3(39, 0, 0), true), Is.False);
            });
        }

        [Test]
        public void FormationRebuildRebasesExpiredDeadlinesWithoutForgettingCurrentArrivals()
        {
            var attendance = new AutonomousRendezvousAttendance();
            attendance.Add(1, 0);
            attendance.Add(2, 0);
            Assert.That(attendance.Observe(1, AutonomousRendezvousAttendance.TimeoutMilliseconds, false), Is.True);

            long rebuiltAt = AutonomousRendezvousAttendance.TimeoutMilliseconds;
            attendance.Rebase(new long[] { 2, 3 }, rebuiltAt,
                new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc));
            attendance.Observe(2, rebuiltAt, true);

            Assert.Multiple(() =>
            {
                Assert.That(attendance.Observe(2, rebuiltAt + AutonomousRendezvousAttendance.TimeoutMilliseconds, true), Is.False);
                Assert.That(attendance.Observe(3, rebuiltAt + AutonomousRendezvousAttendance.TimeoutMilliseconds - 1, false), Is.False);
                Assert.That(attendance.Observe(3, rebuiltAt + AutonomousRendezvousAttendance.TimeoutMilliseconds, false), Is.True);
                Assert.That(attendance.DeadlineUtc(2), Is.Null);
                Assert.That(attendance.DeadlineUtc(3), Is.EqualTo(new DateTime(2026, 9, 3, 12, 15, 0, DateTimeKind.Utc)));
            });
        }

        [Test]
        public void RepeatedObservationsDoNotExtendDeadline()
        {
            var attendance = new AutonomousRendezvousAttendance();
            attendance.Add(1, 0);
            attendance.Add(1, 14 * 60_000);
            Assert.That(attendance.Observe(1, 15 * 60_000, false), Is.True);
        }

        [Test]
        public void DepartedMembersBecomeNoShowsAtTheirOriginalDeadlineAndNewMembersGetTheirOwnClock()
        {
            var attendance = new AutonomousRendezvousAttendance();
            attendance.Observe(1, 0, true);
            attendance.Add(2, 10 * 60_000);
            Assert.That(attendance.Observe(1, 30 * 60_000, false), Is.True);
            Assert.That(attendance.Observe(2, 15 * 60_000, false), Is.False);
            Assert.That(attendance.Observe(2, 25 * 60_000, false), Is.True);
        }

        [Test]
        public void NewMeetingResetsAttendance()
        {
            var attendance = new AutonomousRendezvousAttendance();
            attendance.Observe(1, 0, true);
            attendance.Reset();
            attendance.Add(1, 20 * 60_000);
            Assert.That(attendance.Observe(1, 35 * 60_000, false), Is.True);
        }

        [Test]
        public void ActualIsaoalineSeamUsesTerrainElevationInsteadOfDepartureElevation()
        {
            var inside = new Vector3(575777, 548802, 2520);
            var outside = new Vector3(575825, 548926, 2520);
            bool valid = AutonomousZoneBoundaryRouting.TryResolveHeights(inside, outside,
                p => new Vector3(p.X, p.Y, 3168.36f), p => new Vector3(p.X, p.Y, 3097.66f), out var step);
            Assert.That(valid, Is.True);
            Assert.That(step.Inside.Z, Is.EqualTo(3168.36f));
            Assert.That(step.Outside.Z, Is.EqualTo(3097.66f));
        }

        [Test]
        public void SeamCannotBridgeAMissingMeshCliffOrDistantSnap()
        {
            Vector3 a = new(0, 0, 1000), b = new(128, 0, 1000);
            Assert.That(AutonomousZoneBoundaryRouting.TryResolveHeights(a, b, p => null, p => p, out _), Is.False);
            Assert.That(AutonomousZoneBoundaryRouting.TryResolveHeights(a, b, p => p, p => p + new Vector3(0, 0, 500), out _), Is.False);
            Assert.That(AutonomousZoneBoundaryRouting.TryResolveHeights(a, b, p => p + new Vector3(500, 0, 0), p => p, out _), Is.False);
            Assert.That(AutonomousZoneBoundaryRouting.TryResolveHeights(a, b, p => new Vector3(float.NaN), p => p, out _), Is.False);
        }

        [Test]
        public void UsefulPartialCorridorContinuesButIslandOrRealFailureDoesNotLoop()
        {
            Assert.That(AutonomousRouteRecoveryPolicy.CanContinuePartial(PathfindingStatus.PartialPathFound, Vector3.Zero, new(0, 500, 0)), Is.True);
            Assert.That(AutonomousRouteRecoveryPolicy.CanContinuePartial(PathfindingStatus.PartialPathFound, Vector3.Zero, new(0, 2, 0)), Is.False);
            Assert.That(AutonomousRouteRecoveryPolicy.CanContinuePartial(PathfindingStatus.NoPathFound, Vector3.Zero, new(0, 500, 0)), Is.False);
        }

        [Test]
        public void FailedBoardingApproachCanUseAnotherMasterThenChainToTheBlockedMaster()
        {
            var legs = new List<AutonomousStableRoutePlanner.LegMetric>
            {
                new(0, 1000, 0, 9000, 0, 10, 0),
                new(1, 0, 0, 1000, 0, 20, 0)
            };
            var route = AutonomousStableRoutePlanner.ChooseFirstLeg(0, 0, 10000, 0, 100, 0, legs, new HashSet<int> { 0 });
            Assert.That(route, Is.Not.Null);
            Assert.That(route.Value.FirstLegIndex, Is.EqualTo(1));
            Assert.That(route.Value.HopCount, Is.EqualTo(2));
            Assert.That(route.Value.PlannedPrice, Is.Zero);
        }
    }
}

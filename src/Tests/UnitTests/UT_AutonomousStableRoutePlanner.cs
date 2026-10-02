using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousStableRoutePlanner
{
    [Test]
    public void ChoosesStableInsteadOfLongDirectWalk()
    {
        List<AutonomousStableRoutePlanner.LegMetric> legs =
        [
            new(0, 100, 0, 9000, 0, 10, 0)
        ];

        AutonomousStableRoutePlanner.RouteDecision? route =
            AutonomousStableRoutePlanner.ChooseFirstLeg(0, 0, 10000, 0, 100, 0, legs);

        Assert.That(route, Is.Not.Null);
        Assert.That(route!.Value.FirstLegIndex, Is.EqualTo(0));
        Assert.That(route.Value.EstimatedSeconds, Is.LessThan(route.Value.DirectWalkSeconds));
    }

    [Test]
    public void ChainsMultipleTicketLegsWhenThatIsFastest()
    {
        List<AutonomousStableRoutePlanner.LegMetric> legs =
        [
            new(0, 0, 0, 4000, 0, 5, 0),
            new(1, 4000, 0, 9000, 0, 5, 0)
        ];

        AutonomousStableRoutePlanner.RouteDecision? route =
            AutonomousStableRoutePlanner.ChooseFirstLeg(0, 0, 10000, 0, 100, 0, legs);

        Assert.That(route, Is.Not.Null);
        Assert.That(route!.Value.FirstLegIndex, Is.EqualTo(0));
        Assert.That(route.Value.HopCount, Is.EqualTo(2));
    }

    [Test]
    public void IgnoresPaidTicketThatBotCannotAfford()
    {
        List<AutonomousStableRoutePlanner.LegMetric> legs =
        [
            new(0, 0, 0, 9500, 0, 1, 50),
            new(1, 0, 0, 7000, 0, 5, 0)
        ];

        AutonomousStableRoutePlanner.RouteDecision? route =
            AutonomousStableRoutePlanner.ChooseFirstLeg(0, 0, 10000, 0, 100, 10, legs);

        Assert.That(route, Is.Not.Null);
        Assert.That(route!.Value.FirstLegIndex, Is.EqualTo(1));
        Assert.That(route.Value.PlannedPrice, Is.Zero);
    }

    [Test]
    public void NeverPlansAChainWhoseCombinedTicketsExceedFunds()
    {
        List<AutonomousStableRoutePlanner.LegMetric> legs =
        [
            new(0, 0, 0, 4000, 0, 5, 3),
            new(1, 4000, 0, 9000, 0, 5, 3)
        ];

        AutonomousStableRoutePlanner.RouteDecision? route =
            AutonomousStableRoutePlanner.ChooseFirstLeg(0, 0, 10000, 0, 100, 5, legs);

        Assert.That(route, Is.Not.Null);
        Assert.That(route!.Value.HopCount, Is.EqualTo(1));
        Assert.That(route.Value.PlannedPrice, Is.LessThanOrEqualTo(5));
    }

    [Test]
    public void AcceptsAnyMeaningfullyFasterTicketWithoutFifteenPercentThreshold()
    {
        List<AutonomousStableRoutePlanner.LegMetric> legs =
        [
            new(0, 0, 0, 9000, 0, 89, 0)
        ];

        AutonomousStableRoutePlanner.RouteDecision? route =
            AutonomousStableRoutePlanner.ChooseFirstLeg(0, 0, 10000, 0, 100, 0, legs);

        Assert.That(route, Is.Not.Null);
        Assert.That(route!.Value.EstimatedSeconds, Is.EqualTo(99).Within(0.001));
        Assert.That(route.Value.DirectWalkSeconds, Is.EqualTo(100).Within(0.001));
    }

    [Test]
    public void StableRideCannotDismountOnTransientMovementFlagLoss()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousStableRouteLifecycle.ShouldComplete(false, false, false, false), Is.False);
            Assert.That(AutonomousStableRouteLifecycle.ShouldComplete(true, false, true, false), Is.False);
            Assert.That(AutonomousStableRouteLifecycle.ShouldComplete(false, true, false, true), Is.False);
            Assert.That(AutonomousStableRouteLifecycle.ShouldComplete(false, false, false, true), Is.True);
        });
    }

    [Test]
    public void DisconnectedSameZoneStableCannotBecomeTheFirstBoardingLeg()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousStableRoutePlanner.CanUseAsFirstBoardingLeg(true, false), Is.False);
            Assert.That(AutonomousStableRoutePlanner.CanUseAsFirstBoardingLeg(true, true), Is.True);
            Assert.That(AutonomousStableRoutePlanner.CanUseAsFirstBoardingLeg(false, false), Is.True);
        });
    }

    [Test]
    public void AnUnreachableFirstBoardingLegCanBeExcludedWithoutLosingTheGoal()
    {
        List<AutonomousStableRoutePlanner.LegMetric> legs =
        [
            new(0, 100, 0, 9_000, 0, 5, 0),
            new(1, 600, 0, 8_000, 0, 10, 0),
        ];
        var excluded = new HashSet<int> { 0 };
        var decision = AutonomousStableRoutePlanner.ChooseFirstLeg(
            0, 0, 10_000, 0, 100, 0, legs, excluded);
        Assert.That(decision?.FirstLegIndex, Is.EqualTo(1));
    }

    [Test]
    public void FailedFirstBoardingProbeIsReusedOnlyAtTheSameSpotAndExpires()
    {
        var cache = new AutonomousStableBoardingFailureCache();
        DateTime now = new(2026, 9, 23, 7, 0, 0, DateTimeKind.Utc);
        var probe = new AutonomousStableBoardingFailureCache.Probe(17, 81, 100, 101,
            new Vector3(400, 500, 600), new Vector3(100, 200, 300));
        Assert.That(AutonomousStableBoardingFailureCache.MaximumNewProbesPerSearch, Is.EqualTo(2));
        Assert.That(cache.WasRecentlyUnreachable(probe, now), Is.False);
        cache.RememberUnreachable(probe, now);
        Assert.Multiple(() =>
        {
            Assert.That(cache.WasRecentlyUnreachable(probe, now.AddMinutes(1)), Is.True);
            Assert.That(cache.WasRecentlyUnreachable(probe with
            {
                Boarding = new Vector3(401, 500, 600)
            }, now.AddMinutes(1)), Is.False);
            Assert.That(cache.WasRecentlyUnreachable(probe with
            {
                Source = new Vector3(109, 200, 300)
            }, now.AddMinutes(1)), Is.False);
        });
        cache.RememberUnreachable(probe, now);
        Assert.That(cache.WasRecentlyUnreachable(probe, now.AddMinutes(5)), Is.False);
    }

    [Test]
    public void BoardingApproachFitsHorseStartAndMasterInteractionAfterArrivalTolerance()
    {
        Vector3 origin = new(342490, 593328, 5456);
        Vector3 master = new(342306, 593284, 5456);
        bool chosen = AutonomousStableRoutePlanner.TryChooseBoardingApproach(origin, master,
            192, point => point, out Vector3 approach);
        Assert.That(chosen, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(Vector3.Distance(approach, origin), Is.LessThanOrEqualTo(45));
            Assert.That(Vector3.Distance(approach, master), Is.LessThanOrEqualTo(147));
            Assert.That(AutonomousStableRoutePlanner.TryChooseBoardingApproach(origin,
                new(342000, 593284, 5456), 192, point => point, out _), Is.False);
            Assert.That(AutonomousStableRoutePlanner.TryChooseBoardingApproach(origin, master,
                192, point => point + new Vector3(100, 0, 0), out _), Is.False);
        });
    }

    [Test]
    public void AlreadyInRangeStableKeepsItsOriginalBoardingPoint()
    {
        Vector3 origin = new(100, 0, 0);
        Assert.That(AutonomousStableRoutePlanner.TryChooseBoardingApproach(origin,
            Vector3.Zero, 192, point => point, out Vector3 approach), Is.True);
        Assert.That(approach, Is.EqualTo(origin));
        Assert.That(AutonomousStableRoutePlanner.TryChooseBoardingApproach(
            Vector3.Zero, Vector3.Zero, 50, point => point, out _), Is.True);
    }

    [Test]
    public void FailedMeetupBoardingFallsBackToFootForOnlyThatGroup()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousStableRoutePlanner.MayPlanMeetupHorse(true, "party-A", "party-A"), Is.False);
            Assert.That(AutonomousStableRoutePlanner.MayPlanMeetupHorse(true, "party-B", "party-A"), Is.True);
            Assert.That(AutonomousStableRoutePlanner.MayPlanMeetupHorse(false, "party-A", "party-A"), Is.True);
        });
    }

    [Test]
    public void LowMularnTicketEndpointIsTheOnlyImmediateLandingCorrection()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousStableRoutePlanner.ShouldCorrectAuditedMularnLanding(true, true, 100,
                new(803743, 722129, 4684)), Is.True);
            Assert.That(AutonomousStableRoutePlanner.ShouldCorrectAuditedMularnLanding(false, true, 100,
                new(803743, 722129, 4684)), Is.False);
            Assert.That(AutonomousStableRoutePlanner.ShouldCorrectAuditedMularnLanding(true, false, 100,
                new(803743, 722129, 4684)), Is.False);
            Assert.That(AutonomousStableRoutePlanner.IsAuditedMularnLanding(100,
                new(803743, 722129, 4684)), Is.True);
            Assert.That(AutonomousStableRoutePlanner.IsAuditedMularnLanding(100,
                new(803744, 722167, 4685)), Is.True);
            Assert.That(AutonomousStableRoutePlanner.IsAuditedMularnLanding(100,
                new(801990, 722423, 4680)), Is.False);
            Assert.That(AutonomousStableRoutePlanner.IsAuditedMularnLanding(100,
                new(803743, 722129, 4852)), Is.False);
            Assert.That(AutonomousStableRoutePlanner.IsAuditedMularnLanding(1,
                new(803743, 722129, 4684)), Is.False);
        });
    }
}

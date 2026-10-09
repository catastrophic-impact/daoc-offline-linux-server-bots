using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_AutonomousBotDecisionEngine
{
    [Test]
    public void Choose_NeverSelectsGreyEnemyRealmOrUnreachableCamp()
    {
        var state = State();
        var camps = new[]
        {
            Camp("grey", eRealm.Albion, ConColor.GREY, true),
            Camp("enemy", eRealm.Midgard, ConColor.YELLOW, true),
            Camp("blocked", eRealm.Albion, ConColor.YELLOW, false),
            Camp("valid", eRealm.Albion, ConColor.BLUE, true),
        };

        var decision = AutonomousBotDecisionEngine.Choose(state, camps, Array.Empty<AutonomousBotDecisionEngine.Service>(), new Random(7));
        Assert.That(decision.TargetId, Is.EqualTo("valid"));
    }

    [Test]
    public void Choose_AfterRepeatedDeathsMovesToGreenButNotGrey()
    {
        var state = State() with { DeathsAtCurrentCamp = 3, CurrentCampId = "hard" };
        var camps = new[]
        {
            Camp("grey", eRealm.Albion, ConColor.GREY, true),
            Camp("hard", eRealm.Albion, ConColor.ORANGE, true),
            Camp("safe", eRealm.Albion, ConColor.GREEN, true),
        };

        var decision = AutonomousBotDecisionEngine.Choose(state, camps, Array.Empty<AutonomousBotDecisionEngine.Service>(), new Random(11));
        Assert.That(decision.TargetId, Is.EqualTo("safe"));
    }

    [Test]
    public void DeathFallbackChoosesSafestAvailableNonGreyAlternative()
    {
        var camps = new[]
        {
            Camp("grey", eRealm.Albion, ConColor.GREY, true),
            Camp("failed", eRealm.Albion, ConColor.YELLOW, true) with { MonsterName = "bull frog" },
            Camp("other-yellow", eRealm.Albion, ConColor.YELLOW, true) with { MonsterName = "wolf pup" },
            Camp("orange", eRealm.Albion, ConColor.ORANGE, true) with { MonsterName = "bandit" },
        };

        AutonomousBotDecisionEngine.Camp selected = AutonomousBotDecisionEngine.SelectSafestAvailableAfterDeath(
            camps, "failed", "bull frog", new Random(3));

        Assert.Multiple(() =>
        {
            Assert.That(selected.Id, Is.EqualTo("other-yellow"));
            Assert.That(selected.TypicalCon, Is.EqualTo(ConColor.YELLOW));
        });
    }

    [Test]
    public void DeathFallbackReturnsNullInsteadOfEverChoosingGrey()
    {
        AutonomousBotDecisionEngine.Camp selected = AutonomousBotDecisionEngine.SelectSafestAvailableAfterDeath(
            new[] { Camp("grey", eRealm.Albion, ConColor.GREY, true) }, string.Empty, string.Empty, new Random(1));
        Assert.That(selected, Is.Null);
    }

    [Test]
    public void OutdoorSelectionIsWeightedByLiveSpawnsWithoutNearestBias()
    {
        var state = State();
        var camps = new[]
        {
            new AutonomousBotDecisionEngine.Camp("near-crowded", "Near", "large frog", eRealm.Albion, 1,
                ConColor.BLUE, ConColor.BLUE, true, false, false, 99, 0, 0.01, 20),
            new AutonomousBotDecisionEngine.Camp("far-same-frog", "Far", "large frog", eRealm.Albion, 1,
                ConColor.BLUE, ConColor.BLUE, true, false, false, 1, 0, 999, 20),
            new AutonomousBotDecisionEngine.Camp("dungeon", "Dungeon", "cave rat", eRealm.Albion, 1,
                ConColor.BLUE, ConColor.BLUE, true, true, false, 3, 0, 500, 20),
            new AutonomousBotDecisionEngine.Camp("frontier-pve", "Frontier", "wolf", eRealm.Albion, 163,
                ConColor.BLUE, ConColor.BLUE, true, false, true, 2, 0, 750, 20),
        };

        AutonomousBotDecisionEngine.Camp near = AutonomousBotDecisionEngine.SelectWithinEnvironment(
            camps, AutonomousBotDecisionEngine.PveEnvironment.Outdoor, new IndexRandom(0));
        // Weights: near-crowded 6 (99 spawns, capped), far 1, frontier 2. Distance never counts.
        AutonomousBotDecisionEngine.Camp alsoNear = AutonomousBotDecisionEngine.SelectWithinEnvironment(
            camps, AutonomousBotDecisionEngine.PveEnvironment.Outdoor, new IndexRandom(5));
        AutonomousBotDecisionEngine.Camp far = AutonomousBotDecisionEngine.SelectWithinEnvironment(
            camps, AutonomousBotDecisionEngine.PveEnvironment.Outdoor, new IndexRandom(6));
        AutonomousBotDecisionEngine.Camp frontier = AutonomousBotDecisionEngine.SelectWithinEnvironment(
            camps, AutonomousBotDecisionEngine.PveEnvironment.Outdoor, new IndexRandom(7));
        AutonomousBotDecisionEngine.Camp dungeon = AutonomousBotDecisionEngine.SelectWithinEnvironment(
            camps, AutonomousBotDecisionEngine.PveEnvironment.Dungeon, new IndexRandom(0));

        Assert.Multiple(() =>
        {
            Assert.That(near.Id, Is.EqualTo("near-crowded"));
            Assert.That(alsoNear.Id, Is.EqualTo("near-crowded"));
            Assert.That(far.Id, Is.EqualTo("far-same-frog"));
            Assert.That(dungeon.Id, Is.EqualTo("dungeon"));
            Assert.That(frontier.Id, Is.EqualTo("frontier-pve"));
        });
    }

    [Test]
    public void Formation_IsTightOutsideAndContractsInside()
    {
        var outside = AutonomousFormation.For("Aldric", false);
        var inside = AutonomousFormation.For("Aldric", true);
        Assert.Multiple(() =>
        {
            Assert.That(outside.Distance, Is.InRange(63, 156));
            Assert.That(inside.Distance, Is.InRange(40, 54));
            Assert.That(outside.AngleDegrees, Is.EqualTo(inside.AngleDegrees));
        });
    }

    [Test]
    public void Formation_IdleWanderNeverExceedsThirtyPercentBeyondCircle()
    {
        var formation = AutonomousFormation.For("Aldric", false);

        for (int cycle = 0; cycle < 100; cycle++)
        {
            var wander = AutonomousFormation.ForIdleWander("Aldric", false, cycle);
            Assert.That(wander.Distance, Is.InRange(formation.Distance, (int)Math.Ceiling(formation.Distance * 1.30)));
            Assert.That(wander.AngleDegrees, Is.InRange(0, 359));
        }
    }

    [Test]
    public void RecoveryIsARealResourceNeedButNeverRequiredDuringTravel()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRestPolicy.NeedsRecovery(100, 100, 100, true), Is.False);
            Assert.That(AutonomousRestPolicy.NeedsRecovery(99, 100, 100, true), Is.False);
            Assert.That(AutonomousRestPolicy.NeedsRecovery(69, 100, 100, true), Is.True);
            Assert.That(AutonomousRestPolicy.NeedsRecovery(100, 44, 100, true), Is.True);
            Assert.That(AutonomousRestPolicy.NeedsRecovery(100, 100, 34, true), Is.True);
            Assert.That(AutonomousRestPolicy.NeedsRecovery(100, 40, 100, false), Is.False);
            Assert.That(AutonomousRestPolicy.CanSit(false, false, false, 50, 0, 0, true), Is.False);
            Assert.That(AutonomousRestPolicy.CanSit(true, true, false, 50, 0, 0, true), Is.False);
            Assert.That(AutonomousRestPolicy.CanSit(true, false, false, 50, 0, 0, true), Is.True);
            Assert.That(AutonomousRestPolicy.IsFullyRecovered(100, 100, 100, true), Is.True);
            Assert.That(AutonomousRestPolicy.IsFullyRecovered(100, 99, 100, true), Is.False);
        });
    }

    [Test]
    public void GroupLifecycle_CapsSizeAndEventuallyDisbands()
    {
        Assert.That(AutonomousGroupLifecycle.ShouldInvite(5, true, true, true, new Random(1)), Is.False);
        Assert.That(AutonomousGroupLifecycle.ShouldDisband(new(4, 20, 13, 8, 0, false, false), new Random(1)), Is.True);
        Assert.That(AutonomousGroupLifecycle.ShouldDisband(new(4, 120, 0, 0, 0, false, true), new Random(1)), Is.False);
        Assert.That(AutonomousGroupLifecycle.AreLevelsCompatible(new[] { 20, 22, 25 }), Is.True);
        Assert.That(AutonomousGroupLifecycle.AreLevelsCompatible(new[] { 20, 26 }), Is.False);
    }

    [Test]
    public void AutonomousGroupPolicy_PreservesSoloMajorityAndUsesFixedDifficulty()
    {
        Assert.That(AutonomousBotGroupCoordinator.MaximumAutonomousGrouped(180), Is.EqualTo(72));
        Assert.That(AutonomousBotGroupCoordinator.MaximumAutonomousGrouped(9), Is.EqualTo(3));
        Assert.That(AutonomousBotGroupCoordinator.MinimumSoloRvrReserve(10), Is.EqualTo(4));
        Assert.That(AutonomousBotGroupCoordinator.MaximumGroupedForObjective(eAutonomousObjectiveKind.RvR, 10), Is.EqualTo(6));
        Assert.That(AutonomousBotGroupCoordinator.AvailableGroupSlotsForObjective(eAutonomousObjectiveKind.RvR, 10, 4), Is.EqualTo(2));
        Assert.That(AutonomousBotGroupCoordinator.AvailableGroupSlotsForObjective(eAutonomousObjectiveKind.RvR, 10, 6), Is.Zero);
        Assert.That(AutonomousBotGroupCoordinator.ShouldContinueFormationSearch(eAutonomousObjectiveKind.RvR, 0), Is.True);
        Assert.That(AutonomousBotGroupCoordinator.ShouldContinueFormationSearch(eAutonomousObjectiveKind.GroupPve, 0), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.MaximumGroupedForObjective(eAutonomousObjectiveKind.GroupPve, 10), Is.EqualTo(10));

        long lifetime = AutonomousBotGroupCoordinator.RollGrindingLifetimeMilliseconds(new Random(7));
        Assert.That(lifetime, Is.InRange(45 * 60_000L, 120 * 60_000L));
        int[] bonuses = Enumerable.Range(0, 200)
            .Select(seed => AutonomousBotGroupCoordinator.RollPreferredLevelBonus(8, new Random(seed))).ToArray();
        Assert.That(bonuses, Has.All.InRange(3, 10));
        Assert.That(bonuses.Distinct().Count(), Is.GreaterThan(1));
        Assert.That(AutonomousBotGroupCoordinator.LevelsCompatible(49, 50), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.LevelsCompatible(50, 50), Is.True);
    }

    [Test]
    public void AutonomousGroupPolicy_StagesLeaderAndGivesTankPullPriority()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousBotGroupCoordinator.PreferredPullerIndex(
                new[] { BotPartyRole.Damage, BotPartyRole.Support, BotPartyRole.Tank }, 0), Is.EqualTo(2));
            Assert.That(AutonomousBotGroupCoordinator.PreferredPullerIndex(
                new[] { BotPartyRole.Tank, BotPartyRole.Damage }, 0), Is.Zero);
            Assert.That(AutonomousBotGroupCoordinator.PreferredPullerIndex(
                new[] { BotPartyRole.Damage, BotPartyRole.Support }, 0), Is.Zero);
            Assert.That(AutonomousBotGroupCoordinator.IsAssemblyPhase("Leader staging"), Is.True);
            Assert.That(AutonomousBotGroupCoordinator.IsAssemblyPhase("Meeting up"), Is.True);
            Assert.That(AutonomousBotGroupCoordinator.IsAssemblyPhase("Traveling"), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.LeaderStagingTimeoutMilliseconds, Is.EqualTo(20 * 60_000));
            Assert.That(BotPartyRoles.CanFill(eCharacterClass.Cleric, BotPveGroupRole.Healer), Is.True);
            Assert.That(BotPartyRoles.CanFill(eCharacterClass.Cleric, BotPveGroupRole.Buffer), Is.True);
            Assert.That(BotPartyRoles.CanFill(eCharacterClass.Armsman, BotPveGroupRole.Tank), Is.True);
            Assert.That(BotPartyRoles.CanFill(eCharacterClass.Bard, BotPveGroupRole.Healer), Is.False);
            Assert.That(BotPartyRoles.CanFill(eCharacterClass.Bard, BotPveGroupRole.Buffer), Is.True);
            Assert.That(AutonomousObjectiveAssignments.IsProtectedGroupMatchmakingState(
                eAutonomousObjectiveKind.GroupPve, false), Is.True);
            Assert.That(AutonomousObjectiveAssignments.IsProtectedGroupMatchmakingState(
                eAutonomousObjectiveKind.GroupPve, true), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.IsProtectedWatchdogPhase(
                "Leader staging", false, false, false), Is.True);
            Assert.That(AutonomousBotGroupCoordinator.IsProtectedWatchdogPhase(
                "Meeting up", false, false, false), Is.True);
            Assert.That(AutonomousBotGroupCoordinator.IsProtectedWatchdogPhase(
                "Grinding", false, false, false), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.HasLeaderStagingTimedOut(
                "Leader staging", 1_200_000, 1_199_999), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.HasLeaderStagingTimedOut(
                "Leader staging", 1_200_000, 1_200_000), Is.True);
            Assert.That(AutonomousBotGroupCoordinator.HasLeaderStagingTimedOut(
                "Meeting up", 1_200_000, 1_200_000), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.PhaseAfterCasualty("Choosing group target", false),
                Is.EqualTo("Choosing group target"));
        });
    }

    [Test]
    public void GroupPullReadinessDoesNotRequireAnIdleMemberToHaveExactlyFullResources()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousBotGroupCoordinator.ReadyForGroupPull(99, 99, 99, true), Is.True);
            Assert.That(AutonomousBotGroupCoordinator.ReadyForGroupPull(89, 99, 99, true), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.ReadyForGroupPull(99, 79, 99, true), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.ReadyForGroupPull(99, 99, 79, true), Is.False);
            Assert.That(AutonomousBotGroupCoordinator.ReadyForGroupPull(99, 0, 99, false), Is.True);
            Assert.That(AutonomousBotGroupCoordinator.ShouldRecoverAfterGroupCombat(false,
                99, 99, 99, true), Is.False, "near-full defense must not start an endless exact-full hold");
            Assert.That(AutonomousBotGroupCoordinator.ShouldRecoverAfterGroupCombat(false,
                80, 99, 99, true), Is.True);
            Assert.That(AutonomousBotGroupCoordinator.ShouldRecoverAfterGroupCombat(true,
                99, 99, 99, true), Is.True, "a begun post-fight rest still runs to full");
            Assert.That(AutonomousBotGroupCoordinator.ShouldRecoverAfterGroupCombat(true,
                100, 100, 100, true), Is.False);
        });
    }

    [Test]
    public void FormedPveGroupHoldsItsCampThroughNormalRespawns()
    {
        var camp = new AutonomousBotGroupCoordinator.SharedCamp(
            "camp", "huge boar", "Salisbury Plains", 1, 1, 2, 3, false, false, 10);
        var directive = new AutonomousBotGroupCoordinator.Directive(
            "group", "Grinding", "Grind huge boar", "", eAutonomousObjectiveKind.GroupPve,
            null, default, camp, 8, 10, 0, 5, true);

        Assert.That(AutonomousBotGroupCoordinator.ShouldHoldCampThroughRespawn(directive), Is.True);
        Assert.That(AutonomousBotGroupCoordinator.ShouldHoldCampThroughRespawn(
            directive with { ObjectiveKind = eAutonomousObjectiveKind.RvR }), Is.False);
        Assert.That(AutonomousBotGroupCoordinator.ShouldHoldCampThroughRespawn(
            directive with { IsDynamic = false }), Is.False);
    }

    [Test]
    public void PvePullerUsesHighestLevelTankExceptForLevelFiftyGroups()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousBotGroupCoordinator.PreferredPveTankSlot(
                new[] { 8, 11 }, false), Is.EqualTo(1));
            Assert.That(AutonomousBotGroupCoordinator.PreferredPveTankSlot(
                new[] { 14, 12 }, false), Is.Zero);
            Assert.That(AutonomousBotGroupCoordinator.PreferredPveTankSlot(
                new[] { 10, 10 }, false, new Random(1)), Is.InRange(0, 1),
                "Equal-level leveling tanks are selected randomly");
            Assert.That(AutonomousBotGroupCoordinator.PreferredPveTankSlot(
                new[] { 50, 50 }, true, new Random(1)), Is.InRange(0, 1));
        });
    }

    [Test]
    public void ObjectiveAllocation_UsesRequestedLevelBandTargets()
    {
        AutonomousObjectiveAssignments.Allocation level20To49 = AutonomousObjectiveAssignments.TargetForPopulation(10, false);
        AutonomousObjectiveAssignments.Allocation level50 = AutonomousObjectiveAssignments.TargetForPopulation(10, true);

        Assert.Multiple(() =>
        {
            Assert.That(level20To49, Is.EqualTo(new AutonomousObjectiveAssignments.Allocation(4, 4, 2)));
            Assert.That(level50, Is.EqualTo(new AutonomousObjectiveAssignments.Allocation(2, 4, 4)));
            Assert.That(AutonomousObjectiveAssignments.TargetForLowLevelPopulation(10), Is.EqualTo(new AutonomousObjectiveAssignments.Allocation(6, 4, 0)));
            Assert.That(AutonomousObjectiveAssignments.TargetForLowLevelPopulation(3), Is.EqualTo(new AutonomousObjectiveAssignments.Allocation(2, 1, 0)));
            Assert.That(AutonomousObjectiveAssignments.TargetForPopulation(2, false), Is.EqualTo(new AutonomousObjectiveAssignments.Allocation(1, 1, 0)));
            Assert.That(AutonomousObjectiveAssignments.TargetForPopulation(2, true), Is.EqualTo(new AutonomousObjectiveAssignments.Allocation(0, 1, 1)));
            Assert.That(AutonomousObjectiveAssignments.Parse("RvR"), Is.EqualTo(eAutonomousObjectiveKind.RvR));
            Assert.That(AutonomousObjectiveAssignments.Parse("unknown"), Is.EqualTo(eAutonomousObjectiveKind.SoloPve));
        });
    }

    [Test]
    public void RvrTenure_StaysBoundedAndRequiresPersistedPveIntermission()
    {
        Assert.That(Enumerable.Range(0, 200)
            .Select(seed => AutonomousObjectiveAssignments.RollRvrTenure(new Random(seed))),
            Has.All.InRange(AutonomousObjectiveAssignments.MinimumRvrTenure, AutonomousObjectiveAssignments.MaximumRvrTenure));

        DateTime now = DateTime.UtcNow;
        var record = new OfflineWorldBotRecord
        {
            ObjectiveKind = "RvR",
            ObjectiveExpiresUtc = now.AddMinutes(1).ToString("O"),
            ObjectiveRvrEligibleUtc = now.AddMinutes(30).ToString("O"),
        };
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousObjectiveAssignments.HasActiveRvrTenure(record, now), Is.True);
            Assert.That(AutonomousObjectiveAssignments.HasActiveRvrTenure(record, now.AddMinutes(2)), Is.False);
            Assert.That(AutonomousObjectiveAssignments.IsRvrEligible(record, now), Is.False);
            Assert.That(AutonomousObjectiveAssignments.IsRvrEligible(record, now.AddMinutes(31)), Is.True);
        });
    }

    [Test]
    public void RvrTenure_PveIntermissionAllocationNeverSelectsRvr()
    {
        AutonomousObjectiveAssignments.Allocation target = AutonomousObjectiveAssignments.TargetForPopulation(10, true);
        Assert.That(Enumerable.Range(0, 100)
            .Select(seed => AutonomousObjectiveAssignments.ChoosePveObjective(target, 0, 0, new Random(seed))),
            Has.None.EqualTo(eAutonomousObjectiveKind.RvR));
    }

    [Test]
    public void FreshStartPlacementNeverRepositionsSavedOrAlreadyPlacedCharacters()
    {
        var fresh = new OfflineWorldBotRecord { Level = 1, Experience = 0, RealmPoints = 0, RegionId = 0, LastSavedUtc = string.Empty };
        var saved = new OfflineWorldBotRecord { Level = 1, Experience = 0, RealmPoints = 0, RegionId = 51, LastSavedUtc = DateTime.UtcNow.ToString("O") };
        var legacyMissingPosition = new OfflineWorldBotRecord { Level = 1, Experience = 0, RealmPoints = 0, RegionId = 0, LastSavedUtc = DateTime.UtcNow.ToString("O") };

        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPopulationController.IsFreshUnplacedLevelOneRecord(fresh), Is.True);
            Assert.That(AutonomousPopulationController.IsFreshUnplacedLevelOneRecord(saved), Is.False);
            Assert.That(AutonomousPopulationController.IsFreshUnplacedLevelOneRecord(legacyMissingPosition), Is.False);
        });
    }

    [Test]
    public void TownDowntime_IsLowWeightAndNeverAppliesToPartyHelpers()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousTownDowntime.StartChancePerEligibilityCheck, Is.EqualTo(0.15));
            Assert.That(AutonomousTownDowntime.ShouldStart(true, false, false, true, false, false, false, 0.01), Is.True);
            Assert.That(AutonomousTownDowntime.ShouldStart(true, true, false, true, false, false, false, 0.01), Is.False);
            Assert.That(AutonomousTownDowntime.ShouldStart(true, false, true, true, false, false, false, 0.01), Is.False);
            Assert.That(AutonomousTownDowntime.ShouldStart(true, false, false, true, false, false, true, 0.01), Is.False);
            Assert.That(AutonomousTownDowntime.ShouldStart(true, false, false, true, false, false, false, 0.50), Is.False);
        });
    }

    [Test]
    public void TownDowntime_DurationStaysWithinSeveralMinutes()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            TimeSpan duration = AutonomousTownDowntime.RollDuration(new Random(seed));
            Assert.That(duration, Is.InRange(AutonomousTownDowntime.MinimumDuration, AutonomousTownDowntime.MaximumDuration));
        }
    }

    [Test]
    public void Choose_TownDowntimeNeverDisplacesUrgentVendorWork()
    {
        var state = State() with { InventoryUsedPercent = 95, InSafeTown = true };
        var services = new[]
        {
            new AutonomousBotDecisionEngine.Service(eWorldServiceKind.Vendor, "Merchant", eRealm.Albion, 1, true, 0),
        };

        var decision = AutonomousBotDecisionEngine.Choose(state, Array.Empty<AutonomousBotDecisionEngine.Camp>(), services, new FixedRandom(0.0));
        Assert.That(decision.Activity, Is.EqualTo(eAutonomousActivity.Vendor));
    }

    [Test]
    public void Choose_OrdinarySafeTownDecisionRemainsBiasedTowardGrinding()
    {
        var state = State() with { InSafeTown = true };
        var camps = new[] { Camp("valid", eRealm.Albion, ConColor.BLUE, true) };

        var grind = AutonomousBotDecisionEngine.Choose(state, camps, Array.Empty<AutonomousBotDecisionEngine.Service>(), new FixedRandom(0.50));
        var breakTime = AutonomousBotDecisionEngine.Choose(state, camps, Array.Empty<AutonomousBotDecisionEngine.Service>(), new FixedRandom(0.01));
        Assert.Multiple(() =>
        {
            Assert.That(grind.Activity, Is.EqualTo(eAutonomousActivity.Grind));
            Assert.That(breakTime.Activity, Is.EqualTo(eAutonomousActivity.TownDowntime));
        });
    }

    [Test]
    public void BalancedPveSelectionUsesGameWideSoloAndGroupDungeonPreferences()
    {
        var outdoor = Camp("outdoor", eRealm.Hibernia, ConColor.BLUE, true);
        var muire = outdoor with { Id = "muire", RegionId = 221, IsDungeon = true };

        Assert.Multiple(() =>
        {
            Assert.That(AutonomousBotDecisionEngine.SoloDungeonPreferencePermille, Is.EqualTo(100));
            Assert.That(AutonomousBotDecisionEngine.GroupDungeonPreferencePermille, Is.EqualTo(300));
            Assert.That(AutonomousBotDecisionEngine.Level50GroupDungeonPreferencePermille, Is.EqualTo(400));
            Assert.That(AutonomousBotDecisionEngine.SelectBalancedPveCamp([outdoor, muire], 1, 10, new FixedRandom(0.05)),
                Is.EqualTo(muire));
            Assert.That(AutonomousBotDecisionEngine.SelectBalancedPveCamp([muire, outdoor], 1, 10, new FixedRandom(0.50)),
                Is.EqualTo(outdoor));
            Assert.That(AutonomousBotDecisionEngine.SelectBalancedPveCamp([outdoor, muire], 4, 30, new FixedRandom(0.25)),
                Is.EqualTo(muire));
            Assert.That(AutonomousBotDecisionEngine.SelectBalancedPveCamp([outdoor, muire], 4, 50, new FixedRandom(0.35)),
                Is.EqualTo(muire));
            Assert.That(AutonomousBotDecisionEngine.SelectBalancedPveCamp([outdoor], 1, 10, new FixedRandom(0.01)),
                Is.EqualTo(outdoor), "A dungeon is never invented when no legal dungeon survived the normal filters");
        });
    }

    [Test]
    public void DungeonPopulationSoftlyReducesPreferenceWithoutHardExclusion()
    {
        var outdoor = Camp("outdoor", eRealm.Hibernia, ConColor.BLUE, true);
        var crowded = outdoor with
        {
            Id = "crowded", RegionId = 221, IsDungeon = true,
            DungeonPopulation = 40, DungeonSoftCapacity = 20,
        };

        Assert.Multiple(() =>
        {
            Assert.That(AutonomousDungeonPopulationPolicy.AvailabilityPermille(0, 20), Is.EqualTo(1000));
            Assert.That(AutonomousDungeonPopulationPolicy.AvailabilityPermille(20, 20), Is.EqualTo(250));
            Assert.That(AutonomousDungeonPopulationPolicy.AvailabilityPermille(40, 20), Is.GreaterThanOrEqualTo(50));
            Assert.That(AutonomousBotDecisionEngine.SelectBalancedPveCamp([outdoor, crowded], 1, 10,
                new FixedRandom(0.05)), Is.EqualTo(outdoor));
            Assert.That(AutonomousBotDecisionEngine.SelectBalancedPveCamp([crowded], 1, 10,
                new FixedRandom(0.99)), Is.EqualTo(crowded), "Crowding is pressure, never a hard ban");
        });
    }

    private static AutonomousBotDecisionEngine.State State() =>
        new(eRealm.Albion, 20, 100, 100, 100, 20, 0, 0, 0, 0, 5, string.Empty, 1, false, false, false);

    private static AutonomousBotDecisionEngine.Camp Camp(string id, eRealm realm, ConColor con, bool reachable) =>
        new(id, "Test Zone", "boar", realm, 1, con, con, reachable, false, false, 20, 0, 1);

    private sealed class FixedRandom(double sample) : Random
    {
        protected override double Sample() => sample;
    }

    private sealed class IndexRandom(int index) : Random
    {
        public override int Next(int maxValue) => Math.Clamp(index, 0, maxValue - 1);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS;

public enum eAutonomousActivity
{
    Rest,
    Grind,
    GroupGrind,
    Dungeon,
    RvR,
    Vendor,
    Bank,
    RealmExchange,
    Trainer,
    Craft,
    Travel,
    TownDowntime,
}

public enum eWorldServiceKind
{
    Vendor,
    Trainer,
    StableMaster,
    VaultKeeper,
    RealmExchange,
    CraftStation,
}

/// <summary>
/// Chooses the next live-world activity. It awards nothing; executors must travel,
/// interact, fight, loot, craft, and pay through normal game systems.
/// </summary>
public static class AutonomousBotDecisionEngine
{
    public const int SoloDungeonPreferencePermille = 100;
    public const int GroupDungeonPreferencePermille = 300;
    public const int Level50GroupDungeonPreferencePermille = 400;

    public enum PveEnvironment
    {
        None,
        Outdoor,
        Dungeon,
    }

    public sealed record Camp(
        string Id,
        string ZoneName,
        string MonsterName,
        eRealm Realm,
        ushort RegionId,
        ConColor LowestCon,
        ConColor TypicalCon,
        bool Reachable,
        bool IsDungeon,
        bool IsFrontier,
        int LiveMobCount,
        int RecentDeaths,
        double TravelMinutes,
        int AverageMobLevel = 0,
        int DungeonPopulation = 0,
        int DungeonSoftCapacity = 0);

    public sealed record Service(
        eWorldServiceKind Kind,
        string Name,
        eRealm Realm,
        ushort RegionId,
        bool Reachable,
        double TravelMinutes);

    public sealed record State(
        eRealm Realm,
        int Level,
        byte HealthPercent,
        byte ManaPercent,
        byte EndurancePercent,
        int InventoryUsedPercent,
        int RareItems,
        int CraftingMaterialStacks,
        int UnspentTrainingPoints,
        int DeathsAtCurrentCamp,
        int MinutesAtCurrentCamp,
        string CurrentCampId,
        int GroupSize,
        bool InPlayerLedGroup,
        bool CanCraft,
        bool RvREligible,
        bool InSafeTown = false);

    public sealed record Decision(eAutonomousActivity Activity, string TargetId, string Reason);

    public static Decision Choose(
        State state,
        IReadOnlyCollection<Camp> camps,
        IReadOnlyCollection<Service> services,
        Random random = null)
    {
        random ??= Random.Shared;

        if (state.InPlayerLedGroup)
            return new(eAutonomousActivity.Travel, string.Empty, "Player-led mode owns movement and combat decisions.");

        if (state.HealthPercent < 50 || state.ManaPercent < 22 || state.EndurancePercent < 20)
            return new(eAutonomousActivity.Rest, string.Empty, "Recover actual health, power, or endurance before another pull.");

        if (state.InventoryUsedPercent >= 88)
        {
            if (state.RareItems > 0 && Find(services, eWorldServiceKind.RealmExchange) is Service exchange)
                return new(eAutonomousActivity.RealmExchange, exchange.Name, "Price or bank valuable real inventory before vendoring trash.");
            if (Find(services, eWorldServiceKind.VaultKeeper) is Service bank)
                return new(eAutonomousActivity.Bank, bank.Name, "Store retained loot and crafting materials.");
            if (Find(services, eWorldServiceKind.Vendor) is Service vendor)
                return new(eAutonomousActivity.Vendor, vendor.Name, "Sell ordinary real loot to clear backpack space.");
        }

        if (state.UnspentTrainingPoints > 0 && random.NextDouble() < 0.28 && Find(services, eWorldServiceKind.Trainer) is Service trainer)
            return new(eAutonomousActivity.Trainer, trainer.Name, "Chose to visit the real class trainer; never field auto-trained.");

        if (state.CanCraft && state.CraftingMaterialStacks >= 3 && random.NextDouble() < 0.16 && Find(services, eWorldServiceKind.CraftStation) is Service craft)
            return new(eAutonomousActivity.Craft, craft.Name, "Use owned materials at a reachable craft station.");

        // Leisure is intentionally a very low-weight choice. Recovery, a nearly
        // full backpack, training and crafting are all considered first, and the
        // remaining 96.5% of ordinary decisions continue into grinding/RvR.
        if (state.InSafeTown && random.NextDouble() < 0.035)
            return new(eAutonomousActivity.TownDowntime, string.Empty, "Take a short optional break in a safe town before returning to progression.");

        bool safer = state.DeathsAtCurrentCamp >= 2;

        List<Camp> legal = camps.Where(camp =>
                camp.Realm == state.Realm &&
                camp.Reachable &&
                camp.LiveMobCount > 0 &&
                camp.LowestCon > ConColor.GREY &&
                (camp.AverageMobLevel <= 0 || camp.AverageMobLevel <= state.Level + MaximumLevelAdvantage(state.GroupSize)))
            .ToList();

        IEnumerable<Camp> pve = legal;
        if (safer)
            pve = pve.Where(camp => camp.TypicalCon == ConColor.GREEN);

        Camp selected = SelectBalancedPveCamp(pve, state.GroupSize, state.Level, random);
        if (selected == null)
            return new(eAutonomousActivity.Travel, string.Empty, "No same-realm, reachable, XP-bearing camp is currently valid.");

        eAutonomousActivity activity = selected.IsDungeon
            ? eAutonomousActivity.Dungeon
            : state.GroupSize >= 2 ? eAutonomousActivity.GroupGrind : eAutonomousActivity.Grind;
        string reason = safer
            ? "Repeated deaths caused a move to a safer camp that still grants XP."
            : "Uniformly selected a reachable level-appropriate XP camp.";
        return new(activity, selected.Id, reason);
    }

    /// <summary>
    /// Makes exactly one equiprobable draw after the live caller has applied its
    /// realm, reachability, level and con filters.  Travel time, mob count,
    /// monster name, and camp population intentionally never participate.
    /// </summary>
    public static Camp SelectUniformGrindCamp(IEnumerable<Camp> camps, Random random = null)
    {
        Camp[] choices = camps?.Where(camp => camp != null).ToArray() ?? Array.Empty<Camp>();
        if (choices.Length == 0)
            return null;
        random ??= Random.Shared;
        return choices[random.Next(choices.Length)];
    }

    /// <summary>
    /// Chooses dungeon versus outdoor only after callers have applied realm,
    /// reachability, level, con, route and live-spawn checks. Population is a
    /// soft pressure, never a hard ban, so a full dungeon naturally yields to
    /// outdoor camps while an otherwise-empty candidate set remains usable.
    /// </summary>
    public static Camp SelectBalancedPveCamp(IEnumerable<Camp> camps, int groupSize = 1,
        int level = 1, Random random = null)
    {
        Camp[] choices = camps?.Where(camp => camp != null).ToArray() ?? Array.Empty<Camp>();
        if (choices.Length == 0)
            return null;

        random ??= Random.Shared;
        PveEnvironment environment = SelectPveEnvironment(choices, groupSize, level, random);
        return SelectWithinEnvironment(choices, environment, random);
    }

    public static PveEnvironment SelectPveEnvironment(IEnumerable<Camp> camps, int groupSize,
        int level, Random random = null)
    {
        Camp[] choices = camps?.Where(camp => camp != null).ToArray() ?? Array.Empty<Camp>();
        bool hasDungeon = choices.Any(camp => camp.IsDungeon);
        bool hasOutdoor = choices.Any(camp => !camp.IsDungeon);
        if (!hasDungeon) return hasOutdoor ? PveEnvironment.Outdoor : PveEnvironment.None;
        if (!hasOutdoor) return PveEnvironment.Dungeon;

        random ??= Random.Shared;
        int preference = groupSize >= 2
            ? level >= 50 ? Level50GroupDungeonPreferencePermille : GroupDungeonPreferencePermille
            : SoloDungeonPreferencePermille;
        int availability = choices.Where(camp => camp.IsDungeon)
            .GroupBy(camp => camp.RegionId)
            .Select(group => DungeonAvailability(group))
            .DefaultIfEmpty(0)
            .Max();
        int effectivePreference = preference * availability / 1_000;
        return random.Next(1_000) < effectivePreference ? PveEnvironment.Dungeon : PveEnvironment.Outdoor;
    }

    public static Camp SelectWithinEnvironment(IEnumerable<Camp> camps, PveEnvironment environment,
        Random random = null)
    {
        Camp[] choices = camps?.Where(camp => camp != null &&
            (environment == PveEnvironment.Dungeon ? camp.IsDungeon : !camp.IsDungeon)).ToArray()
            ?? Array.Empty<Camp>();
        if (choices.Length == 0)
            return null;
        random ??= Random.Shared;
        if (environment != PveEnvironment.Dungeon)
            return choices[random.Next(choices.Length)];

        var regions = choices.GroupBy(camp => camp.RegionId)
            .Select(group => new
            {
                Camps = group.ToArray(),
                Weight = Math.Max(1, DungeonCapacity(group) * DungeonAvailability(group)),
            }).ToArray();
        int totalWeight = regions.Sum(region => region.Weight);
        int draw = random.Next(totalWeight);
        foreach (var region in regions)
        {
            if (draw < region.Weight)
                return region.Camps[random.Next(region.Camps.Length)];
            draw -= region.Weight;
        }

        return regions[^1].Camps[^1];
    }

    private static int DungeonCapacity(IEnumerable<Camp> camps)
    {
        Camp[] region = camps.ToArray();
        int published = region.Max(camp => camp.DungeonSoftCapacity);
        return published > 0 ? published : AutonomousDungeonPopulationPolicy.SoftCapacity(
            region.Sum(camp => Math.Max(0, camp.LiveMobCount)));
    }

    private static int DungeonAvailability(IEnumerable<Camp> camps)
    {
        Camp[] region = camps.ToArray();
        int population = region.Max(camp => camp.DungeonPopulation);
        return AutonomousDungeonPopulationPolicy.AvailabilityPermille(population, DungeonCapacity(region));
    }

    /// <summary>
    /// A death-reduced ceiling can be impossible at very low levels (for
    /// example, level one has no XP-bearing blue creature in some realms).
    /// Select the lowest available non-grey con instead of leaving the actor in
    /// an endless planning state. Prefer a different camp/name when one exists.
    /// </summary>
    public static Camp SelectSafestAvailableAfterDeath(
        IEnumerable<Camp> camps,
        string failedCampId,
        string failedMonsterName,
        Random random = null)
    {
        Camp[] safe = camps?
            .Where(camp => camp != null && camp.Reachable && camp.LiveMobCount > 0 &&
                           camp.LowestCon > ConColor.GREY)
            .ToArray() ?? Array.Empty<Camp>();
        if (safe.Length == 0)
            return null;

        Camp[] alternatives = safe.Where(camp =>
                !string.Equals(camp.Id, failedCampId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(camp.MonsterName, failedMonsterName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Camp[] pool = alternatives.Length > 0 ? alternatives : safe;
        ConColor safestCon = pool.Min(camp => camp.TypicalCon);
        return SelectUniformGrindCamp(pool.Where(camp => camp.TypicalCon == safestCon), random);
    }

    private static Service Find(IEnumerable<Service> services, eWorldServiceKind kind) =>
        services.Where(service => service.Kind == kind && service.Reachable)
            .OrderBy(service => service.TravelMinutes)
            .FirstOrDefault();

    private static int MaximumLevelAdvantage(int groupSize) => groupSize switch
    {
        >= 6 => 6,
        5 => 5,
        4 => 4,
        3 => 3,
        2 => 2,
        _ => 1,
    };
}

/// <summary>
/// Low-frequency policy for real, persistent world bots taking short town breaks.
/// Temporary helpers and player-led bots are categorically excluded.
/// </summary>
public static class AutonomousTownDowntime
{
    public const double StartChancePerEligibilityCheck = 0.15;
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(30);

    public static bool RollAtTaskBoundary(double roll) => roll >= 0 && roll < StartChancePerEligibilityCheck;

    // Travel and higher-priority services consume the same original budget.
    // Never promise a fifteen-minute break if that much time no longer remains.
    public static TimeSpan? RollWithinBudget(TimeSpan remaining, Random random = null)
    {
        if (remaining < MinimumDuration) return null;
        random ??= Random.Shared;
        double maximum = Math.Min(MaximumDuration.TotalMilliseconds, remaining.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(MinimumDuration.TotalMilliseconds +
            random.NextDouble() * (maximum - MinimumDuration.TotalMilliseconds));
    }

    public static bool ShouldStart(
        bool isAutonomousWorldBot,
        bool isTemporaryHelper,
        bool inPlayerLedGroup,
        bool inSafeTown,
        bool busy,
        bool urgentRecovery,
        bool urgentTownBusiness,
        double roll)
    {
        return isAutonomousWorldBot && !isTemporaryHelper && !inPlayerLedGroup && inSafeTown &&
               !busy && !urgentRecovery && !urgentTownBusiness &&
               roll >= 0 && roll < StartChancePerEligibilityCheck;
    }

    public static TimeSpan RollDuration(Random random = null)
    {
        random ??= Random.Shared;
        double spanSeconds = (MaximumDuration - MinimumDuration).TotalSeconds;
        return MinimumDuration + TimeSpan.FromSeconds(random.NextDouble() * spanSeconds);
    }
}

/// <summary>
/// Resource recovery is a combat-readiness action, never a travel prerequisite.
/// Callers decide that they are at a legitimate rest point and use this policy
/// only to determine whether a real player resource is still missing.
/// </summary>
public static class AutonomousRestPolicy
{
    public const byte HealthRestThreshold = 70;
    public const byte PowerRestThreshold = 45;
    public const byte EnduranceRestThreshold = 35;

    public static bool NeedsRecovery(
        byte healthPercent,
        byte manaPercent,
        byte endurancePercent,
        bool usesPower) =>
        healthPercent < HealthRestThreshold ||
        (usesPower && manaPercent < PowerRestThreshold) ||
        endurancePercent < EnduranceRestThreshold;

    public static bool IsFullyRecovered(
        byte healthPercent,
        byte manaPercent,
        byte endurancePercent,
        bool usesPower) =>
        healthPercent >= 100 && (!usesPower || manaPercent >= 100) && endurancePercent >= 100;

    // Cosmetic movement around an idle real-player leader must never suppress
    // recovery. Actual leader travel and all non-ambient bot movement retain
    // the normal movement block.
    public static bool MovementBlocksRest(
        bool botMoving,
        bool leaderMoving,
        bool ambientWanderMovement) =>
        leaderMoving || botMoving && !ambientWanderMovement;

    public static bool CanSit(
        bool atRestPoint,
        bool isMoving,
        bool inCombat,
        byte healthPercent,
        byte manaPercent,
        byte endurancePercent,
        bool usesPower) =>
        atRestPoint && !isMoving && !inCombat &&
        NeedsRecovery(healthPercent, manaPercent, endurancePercent, usesPower);

    // Starting recovery uses the existing low-resource thresholds; once seated,
    // finish recovering instead of standing as soon as those thresholds are crossed.
    public static bool ShouldRest(
        bool atRestPoint, bool moving, bool inCombat, bool alreadyResting,
        byte healthPercent, byte manaPercent, byte endurancePercent, bool usesPower) =>
        atRestPoint && !moving && !inCombat &&
        (alreadyResting
            ? !IsFullyRecovered(healthPercent, manaPercent, endurancePercent, usesPower)
            : NeedsRecovery(healthPercent, manaPercent, endurancePercent, usesPower));
}

public static class AutonomousFormation
{
    public sealed record Offset(int Distance, int AngleDegrees);

    public static Offset For(string botName, bool dungeonOrTightInterior)
    {
        int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(botName ?? string.Empty) & int.MaxValue;
        // Keep player-led party members in the same loose circular formation, but
        // at 30% of the former radius (70% closer to their leader).
        int formerDistance = dungeonOrTightInterior ? 90 + hash % 90 : 210 + hash % 310;
        int minimumDistance = dungeonOrTightInterior ? 40 : 60;
        int distance = Math.Max(minimumDistance, (int)Math.Round(formerDistance * 0.30));
        int angle = (hash / 17) % 360;
        return new(distance, angle);
    }

    public static Offset ForIdleWander(string botName, bool dungeonOrTightInterior, int wanderCycle)
    {
        Offset formation = For(botName, dungeonOrTightInterior);
        int hash = HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(botName ?? string.Empty),
            wanderCycle);
        hash &= int.MaxValue;

        // Ambient wandering stays close: at most 30% beyond the normal circle,
        // with a small sideways drift so party members do not march in a line.
        double radialScale = 1.0 + (hash % 31) / 100d;
        int angleDrift = (hash / 31) % 71 - 35;
        int distance = (int)Math.Round(formation.Distance * radialScale);
        int angle = (formation.AngleDegrees + angleDrift + 360) % 360;
        return new(distance, angle);
    }
}

public static class AutonomousGroupLifecycle
{
    public sealed record GroupState(
        int MemberCount,
        int MinutesTogether,
        int MinutesIdle,
        int ObjectiveKillsRemaining,
        int RecentWipes,
        bool InDungeon,
        bool InCombat);

    public static bool ShouldInvite(int currentSize, bool sameRealm, bool levelsCompatible, bool nearby, Random random = null)
    {
        if (!sameRealm || !levelsCompatible || !nearby || currentSize < 1 || currentSize >= 5)
            return false;
        random ??= Random.Shared;
        return random.NextDouble() < 0.10;
    }

    public static bool AreLevelsCompatible(IEnumerable<int> levels)
    {
        int[] values = levels?.ToArray() ?? Array.Empty<int>();
        if (values.Length == 0)
            return false;
        // Level 50 is its own matchmaking bracket.  Below 50 the existing
        // five-level spread remains intact.
        if (values.Any(level => level >= 50))
            return values.All(level => level >= 50);
        return values.Max() - values.Min() <= 5;
    }

    public static bool ShouldDisband(GroupState state, Random random = null)
    {
        if (state.MemberCount < 2)
            return true;
        if (state.InCombat)
            return false;
        if (state.RecentWipes >= 3 || state.MinutesIdle >= 12)
            return true;

        random ??= Random.Shared;
        int softLifetime = state.InDungeon ? 70 : 38;
        if (state.MinutesTogether >= softLifetime)
            return random.NextDouble() < Math.Min(0.85, 0.18 + (state.MinutesTogether - softLifetime) / 50d);
        return false;
    }
}

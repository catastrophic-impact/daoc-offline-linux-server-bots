using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using DOL.GS.RealmAbilities;

namespace DOL.GS;

/// <summary>
/// Gamebots spend their realm ability points the way a player of their class would: the same
/// points (one per realm level, as GetPlayerRealmPointsTotal), the same Atlas costs, maximum levels
/// and prerequisites, following a fixed build for their archetype (melee, caster, healer, archer),
/// and only at their class trainer like their spec training. The build is deterministic and only
/// grows with points, so only the points spent are saved; the abilities are rebuilt from them on load. Passive abilities apply
/// through the normal ability bonuses; the few actives bots can use (Purge, Ignore Pain, Second
/// Wind, First Aid) are fired from the brain by <see cref="UseActives"/>.
/// </summary>
public static class AutonomousBotRealmAbilities
{
    private static readonly Logging.Logger Log = Logging.LoggerManager.Create(typeof(AutonomousBotRealmAbilities));

    public enum Archetype { Melee, Caster, Healer, Archer }

    public readonly record struct Step(string Key, int Level);

    public const string AugStr = "AtlasOF_AugStr", AugCon = "AtlasOF_AugCon", AugDex = "AtlasOF_AugDex",
        AugQui = "AtlasOF_AugQui", AugAcuity = "AtlasOF_AugAcuity", FirstAid = "AtlasOF_FirstAid",
        IgnorePain = "AtlasOF_IgnorePain", IgnorePainTank = "AtlasOF_IgnorePainTank", SecondWind = "AtlasOF_SecondWind",
        Purge = "AtlasOF_Purge", PurgeReduced = "AtlasOF_PurgeReduced";

    /// <summary>Prerequisites, as the Atlas handlers' CheckRequirement (written for GamePlayer only).</summary>
    public static (string Key, int Level)? Requirement(string key, int classId) => key switch
    {
        "AtlasOF_MasteryOfPain" or "AtlasOF_MasteryOfParrying" or "AtlasOF_MasteryOfBlocking" or
            "AtlasOF_DualistsReflexes" or "AtlasOF_FalconsEye" => (AugDex, 2),
        "AtlasOF_MasteryOfArchery" or "AtlasOF_HailOfBlows" or "AtlasOF_Bladedance" or "AtlasOF_WhirlingDervish" => (AugDex, 3),
        "AtlasOF_MasteryOfHealing" or "AtlasOF_MasteryOfMagery" or "AtlasOF_MasteryOfTheArcane" or
            "AtlasOF_WildPower" or "AtlasOF_WildHealing" or "AtlasOF_WildArcana" or "AtlasOF_WildMinion" => (AugAcuity, 2),
        "AtlasOF_MasteryOfTheArt" or "AtlasOF_MasteryOfConcentration" => (AugAcuity, 3),
        "AtlasOF_MasteryOfArms" => classId == (int)eCharacterClass.Friar ? (AugDex, 3) : (AugStr, 3),
        "AtlasOF_Dodger" or "AtlasOF_MasteryOfStealth" => (AugQui, 2),
        SecondWind or "AtlasOF_ArmorOfFaith" or "AtlasOF_AvoidPain" or "AtlasOF_BattleYell" => (AugCon, 3),
        IgnorePain or IgnorePainTank => (FirstAid, 2),
        _ => null,
    };

    /// <summary>The actives a bot knows how to use; every other active is never bought.</summary>
    public static readonly string[] UsableActives = { Purge, PurgeReduced, IgnorePainTank, IgnorePain, SecondWind, FirstAid };

    // Passives with little use to a bot; bought only after everything else.
    private static readonly HashSet<string> LowValue = new(StringComparer.OrdinalIgnoreCase)
        { "AtlasOF_Lifter", "AtlasOF_MasteryOfWater", "AtlasOF_ArrowSalvaging", "AtlasOF_LongWind", "AtlasOF_MasteryOfStealth" };

    private static Step[] Build(params (string Key, int Level)[] steps) =>
        steps.Select(s => new Step(s.Key.StartsWith("AtlasOF_") ? s.Key : "AtlasOF_" + s.Key, s.Level)).ToArray();

    private static readonly Step[] MeleeBuild = Build(("AugCon", 1), ("AugDex", 2), ("MasteryOfPain", 1), ("AugStr", 1),
        ("MasteryOfBlocking", 1), ("MasteryOfParrying", 1), ("Toughness", 1), ("AugCon", 2), (PurgeReduced, 1), (Purge, 1),
        ("AugStr", 3), ("MasteryOfArms", 1), ("AugCon", 3), (SecondWind, 1), ("MasteryOfPain", 2), ("Determination", 1),
        ("DeterminationHybrid", 1), ("AugDex", 3), (FirstAid, 2), (IgnorePainTank, 1), (IgnorePain, 1),
        ("Toughness", 2), ("MasteryOfBlocking", 2), ("MasteryOfParrying", 2), ("MasteryOfArms", 2), ("MasteryOfPain", 3),
        ("AvoidanceOfMagic", 1), ("AugQui", 2), ("Dodger", 1), ("DualistsReflexes", 1));

    private static readonly Step[] CasterBuild = Build(("AugAcuity", 2), ("MasteryOfMagery", 1), ("AugCon", 1),
        ("WildPower", 1), ("Serenity", 1), ("AugDex", 1), ("MasteryOfTheArcane", 1), (PurgeReduced, 1), (Purge, 1),
        ("AugAcuity", 3), ("MasteryOfMagery", 2), ("Toughness", 1), ("EtherealBond", 1), ("WildMinion", 1),
        ("AvoidanceOfMagic", 1), (FirstAid, 1), ("Determination", 1), ("AugCon", 2), ("MasteryOfMagery", 3),
        ("WildPower", 2), ("AugAcuity", 4), ("MasteryOfTheArcane", 2), ("WildArcana", 1));

    private static readonly Step[] HealerBuild = Build(("AugAcuity", 2), ("MasteryOfHealing", 1), ("WildHealing", 1),
        ("AugCon", 1), ("Serenity", 1), (PurgeReduced, 1), (Purge, 1), ("AugDex", 1), ("AugAcuity", 3),
        ("MasteryOfHealing", 2), ("Toughness", 1), ("EtherealBond", 1), ("WildHealing", 2), ("AvoidanceOfMagic", 1),
        ("AugCon", 2), (FirstAid, 1), ("Determination", 1), ("DeterminationHybrid", 1), ("MasteryOfHealing", 3),
        ("AugAcuity", 4), ("WildPower", 1), ("MasteryOfMagery", 1));

    private static readonly Step[] ArcherBuild = Build(("AugDex", 3), ("MasteryOfArchery", 1), ("FalconsEye", 1),
        ("AugQui", 2), ("Dodger", 1), ("AugCon", 1), (PurgeReduced, 1), (Purge, 1), ("AugStr", 1),
        ("MasteryOfArchery", 2), ("Toughness", 1), ("FalconsEye", 2), ("AugCon", 3), (SecondWind, 1),
        ("MasteryOfPain", 1), ("MasteryOfArchery", 3), ("AvoidanceOfMagic", 1), ("Determination", 1));

    public static Archetype ArchetypeOf(ICollection<string> available)
    {
        if (available.Contains("AtlasOF_MasteryOfArchery")) return Archetype.Archer;
        bool melee = available.Contains("AtlasOF_MasteryOfPain") || available.Contains("AtlasOF_MasteryOfParrying");
        if (!melee && (available.Contains("AtlasOF_MasteryOfHealing") || available.Contains("AtlasOF_WildHealing"))) return Archetype.Healer;
        if (!melee && available.Contains(AugAcuity)) return Archetype.Caster;
        return Archetype.Melee;
    }

    /// <summary>
    /// The abilities and levels bought with these points. available: the class's buyable keys with
    /// their maximum level; cost(key, currentLevel): points for the next level. The archetype build
    /// comes first (steps the class lacks or cannot meet yet are skipped), then the remaining
    /// abilities are raised cheapest-first. Buying stops at the first step it cannot afford, so more
    /// points only ever extend the result.
    /// </summary>
    public static Dictionary<string, int> Plan(IReadOnlyDictionary<string, int> available, Archetype archetype, int classId,
        int points, Func<string, int, int> cost)
    {
        var owned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Owned(string key) => owned.TryGetValue(key, out int level) ? level : 0;
        bool Meets(string key) => Requirement(key, classId) is not { } need || Owned(need.Key) >= need.Level;
        // One purge is enough: the reduced-cost variant replaces the full one where a class has it.
        bool Redundant(string key) => key == Purge && owned.ContainsKey(PurgeReduced) || key == PurgeReduced && owned.ContainsKey(Purge) ||
            key == IgnorePain && owned.ContainsKey(IgnorePainTank) || key == IgnorePainTank && owned.ContainsKey(IgnorePain);

        // Returns false when the next level is unaffordable (stop buying).
        bool Raise(string key, int target)
        {
            while (Owned(key) < target)
            {
                int price = cost(key, Owned(key));
                if (price <= 0 || price > points) return false;
                points -= price;
                owned[key] = Owned(key) + 1;
            }
            return true;
        }

        Step[] build = archetype switch
        {
            Archetype.Caster => CasterBuild, Archetype.Healer => HealerBuild, Archetype.Archer => ArcherBuild, _ => MeleeBuild,
        };
        foreach (Step step in build)
        {
            if (!available.TryGetValue(step.Key, out int max) || Redundant(step.Key) || !Meets(step.Key)) continue;
            if (!Raise(step.Key, Math.Min(step.Level, max))) return owned;
        }
        // The rest, cheapest next level first (ties by name), low-value passives last.
        while (true)
        {
            string next = null; int nextPrice = int.MaxValue; bool nextLow = true;
            foreach (var (key, max) in available.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (Owned(key) >= max || Redundant(key) || !Meets(key)) continue;
                int price = cost(key, Owned(key));
                if (price <= 0 || price >= 1000) continue;
                bool low = LowValue.Contains(key);
                if (next == null || (nextLow && !low) || (low == nextLow && price < nextPrice))
                    (next, nextPrice, nextLow) = (key, price, low);
            }
            if (next == null || !Raise(next, Owned(next) + 1)) return owned;
        }
    }

    /// <summary>Realm ability points, as the player rule: one per realm level, at least 1 from level 20.</summary>
    public static int Points(int level, int realmLevel) => level > 19 ? Math.Max(1, realmLevel) : realmLevel;

    private sealed class State
    {
        public int TrainedPoints;
        public int CheckedPoints = -1;
        public bool HasPurchase;
        public TimedRealmAbility[] Actives = Array.Empty<TimedRealmAbility>();
        public long NextUse;
    }

    private sealed record Catalog(Dictionary<string, RealmAbility> ByKey, Dictionary<string, int> Available, Archetype Archetype);

    private static readonly ConditionalWeakTable<GameBot, State> States = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Catalog> Catalogs = new();

    private static Catalog CatalogFor(int classId) => Catalogs.GetOrAdd(classId, id =>
    {
        var byKey = SkillBase.GetClassRealmAbilities(id)
            .Where(ra => ra is not RR5RealmAbility && (ra is not TimedRealmAbility || UsableActives.Contains(ra.KeyName)))
            .GroupBy(ra => ra.KeyName).ToDictionary(g => g.Key, g => g.First());
        var available = byKey.ToDictionary(pair => pair.Key, pair => pair.Value.MaxLevel);
        return new Catalog(byKey, available, ArchetypeOf(available.Keys));
    });

    private static int ClassOf(GameBot bot) => bot.CharacterClass?.ID ?? bot.ClassId;

    private static Dictionary<string, int> PlanFor(GameBot bot, int points)
    {
        Catalog catalog = CatalogFor(ClassOf(bot));
        return Plan(catalog.Available, catalog.Archetype, ClassOf(bot), points, (key, level) => catalog.ByKey[key].CostForUpgrade(level));
    }

    private static bool Eligible(GameBot bot) => bot is { IsAutonomousWorldBot: true, IsTemporaryGroupHelper: false };

    /// <summary>Realm ability points already spent at a trainer (saved with the bot).</summary>
    public static int TrainedPoints(GameBot bot) => bot != null && States.TryGetValue(bot, out State state) ? state.TrainedPoints : 0;

    /// <summary>Gives a loading bot back the abilities it bought with these points earlier.</summary>
    public static void Restore(GameBot bot, int trainedPoints)
    {
        if (!Eligible(bot) || trainedPoints <= 0) return;
        try
        {
            Grant(bot, PlanFor(bot, Math.Min(trainedPoints, Points(bot.Level, bot.RealmLevel))));
            States.GetOrCreateValue(bot).TrainedPoints = trainedPoints;
        }
        catch (Exception exception)
        {
            Log.Warn($"BOT_REALM_ABILITY_FAILED bot={bot.Name} class={ClassOf(bot)} restoring={trainedPoints}", exception);
        }
    }

    /// <summary>True when a trainer visit would buy something new with the bot's realm points.</summary>
    public static bool HasPurchase(GameBot bot)
    {
        if (!Eligible(bot)) return false;
        int points = Points(bot.Level, bot.RealmLevel);
        State state = States.GetOrCreateValue(bot);
        if (points <= state.TrainedPoints) return false;
        if (state.CheckedPoints != points)
        {
            try
            {
                state.HasPurchase = PlanFor(bot, points).Values.Sum() > PlanFor(bot, state.TrainedPoints).Values.Sum();
            }
            catch (Exception exception)
            {
                state.HasPurchase = false;
                Log.Warn($"BOT_REALM_ABILITY_FAILED bot={bot.Name} class={ClassOf(bot)}", exception);
            }
            state.CheckedPoints = points;
        }
        return state.HasPurchase;
    }

    /// <summary>Unspent realm points that alone justify a trip to the trainer; fewer are spent at the next level-up visit.</summary>
    public const int PointsWorthATrip = 3;

    /// <summary>True when realm points alone are worth a trainer trip (enough unspent points and something to buy).</summary>
    public static bool WorthTrainerTrip(GameBot bot) =>
        HasPurchase(bot) && Points(bot.Level, bot.RealmLevel) - TrainedPoints(bot) >= PointsWorthATrip;

    /// <summary>Spends the bot's realm points at a trainer. Returns what was bought ("" when nothing).</summary>
    public static string Train(GameBot bot)
    {
        if (!HasPurchase(bot)) return string.Empty;
        int points = Points(bot.Level, bot.RealmLevel);
        State state = States.GetOrCreateValue(bot);
        try
        {
            string gained = string.Join(",", Grant(bot, PlanFor(bot, points)));
            state.TrainedPoints = points;
            state.HasPurchase = false;
            Log.Info($"BOT_REALM_ABILITY_TRAINED bot=\"{bot.Name}\" class={ClassOf(bot)} realmLevel={bot.RealmLevel} points={points} " +
                $"archetype={CatalogFor(ClassOf(bot)).Archetype} gained=\"{gained}\"");
            return gained;
        }
        catch (Exception exception)
        {
            state.TrainedPoints = points;
            Log.Warn($"BOT_REALM_ABILITY_FAILED bot={bot.Name} class={ClassOf(bot)}", exception);
            return string.Empty;
        }
    }

    private static List<string> Grant(GameBot bot, Dictionary<string, int> plan)
    {
        var gained = new List<string>();
        foreach (var (key, level) in plan)
        {
            Ability current = bot.GetAbility(key);
            if (current != null && current.Level >= level) continue;
            if (SkillBase.GetAbility(key, level) is not RealmAbility ability) continue;
            bot.AddAbility(ability, false);
            gained.Add($"{key.Replace("AtlasOF_", "")}:{current?.Level ?? 0}->{level}");
        }
        State state = States.GetOrCreateValue(bot);
        state.Actives = UsableActives.Select(key => bot.GetAbility(key) as TimedRealmAbility).Where(a => a != null).ToArray();
        return gained;
    }

    /// <summary>Uses a bought active when the situation calls for it. Checked at most once a second.</summary>
    public static void UseActives(GameBot bot)
    {
        if (bot == null || !bot.IsAlive || !States.TryGetValue(bot, out State state) || state.Actives.Length == 0) return;
        long now = GameLoop.GameLoopTime;
        if (now < state.NextUse) return;
        state.NextUse = now + 1_000;
        foreach (TimedRealmAbility ability in state.Actives)
        {
            if (bot.GetSkillDisabledDuration(ability) > 0 || !Wanted(bot, ability.KeyName)) continue;
            ability.Execute(bot);
            // A purge that removed nothing only blocks itself for 5 ms; log real uses (long reuse timers).
            if (bot.GetSkillDisabledDuration(ability) > 1_000 && ability.KeyName != FirstAid)
                Log.Info($"BOT_REALM_ABILITY_USED bot={bot.Name} ability={ability.KeyName.Replace("AtlasOF_", "")} " +
                    $"health={bot.HealthPercent} endurance={bot.EndurancePercent} region={bot.CurrentRegionID}");
            return;
        }
    }

    private static bool Wanted(GameBot bot, string key) => key switch
    {
        // Crowd control in a fight against other realms (or when it is going badly).
        Purge or PurgeReduced => (bot.IsMezzed || bot.IsStunned) && (bot.InCombat || bot.HealthPercent < 90) &&
            (AutonomousWorldBotController.IsBattleground(bot) || bot.HealthPercent < 60),
        IgnorePain or IgnorePainTank => bot.InCombat && bot.HealthPercent < 30 && !bot.IsMezzed && !bot.IsStunned,
        SecondWind => bot.InCombat && bot.EndurancePercent < 10 && !bot.IsMezzed && !bot.IsStunned,
        FirstAid => !bot.InCombat && bot.HealthPercent < 50 && !bot.IsSitting,
        _ => false,
    };
}

using System.Collections.Generic;
using System.Linq;
using DOL.GS;
using DOL.GS.RealmAbilities;
using NUnit.Framework;
using static DOL.GS.AutonomousBotRealmAbilities;

namespace DOL.UnitTests
{
    /// <summary>Gamebots spend realm ability points like a player of their class.</summary>
    [TestFixture]
    public class UT_BotRealmAbilitiesOct6
    {
        private static readonly string[] Passives =
        {
            "AugStr", "AugCon", "AugDex", "AugQui", "AugAcuity", "Toughness", "AvoidanceOfMagic", "Determination", "Serenity",
            "MasteryOfPain", "MasteryOfParrying", "MasteryOfBlocking", "MasteryOfArms", "Dodger", "Regeneration", "Lifter",
            "MasteryOfMagery", "MasteryOfTheArcane", "WildPower", "EtherealBond", "MasteryOfHealing", "WildHealing",
            "MasteryOfArchery", "FalconsEye", "LongWind", "MasteryOfWater",
        };

        private static Dictionary<string, int> Catalog(params string[] keys) =>
            keys.Select(k => k.StartsWith("AtlasOF_") ? k : "AtlasOF_" + k).ToDictionary(k => k, k => k switch
            {
                Purge or PurgeReduced or SecondWind or IgnorePain or IgnorePainTank => 1,
                FirstAid => 3,
                _ => 5,
            });

        private static int Cost(string key, int level) => key switch
        {
            Purge or SecondWind => 10,
            PurgeReduced => 4,
            IgnorePain => 14,
            IgnorePainTank => 8,
            FirstAid => AtlasRAHelpers.GetCommonUpgradeCostFor3LevelsRA(level),
            _ => AtlasRAHelpers.GetCommonUpgradeCostFor5LevelsRA(level),
        };

        private static IEnumerable<TestCaseData> Classes()
        {
            string[] melee = Passives.Where(k => !k.Contains("Magery") && !k.Contains("Healing") && k != "MasteryOfArchery").ToArray();
            yield return new TestCaseData(Catalog(melee.Append(Purge).Append(SecondWind).Append(FirstAid).Append(IgnorePainTank).ToArray()),
                (int)eCharacterClass.Armsman, Archetype.Melee).SetName("Armsman-like");
            yield return new TestCaseData(Catalog("AugAcuity", "AugCon", "AugDex", "MasteryOfMagery", "WildPower", "Serenity",
                "Toughness", "EtherealBond", PurgeReduced, FirstAid, "MasteryOfTheArcane"), (int)eCharacterClass.Wizard, Archetype.Caster)
                .SetName("Wizard-like");
            yield return new TestCaseData(Catalog("AugAcuity", "AugCon", "AugDex", "MasteryOfHealing", "WildHealing", "Serenity",
                "Toughness", PurgeReduced, FirstAid), (int)eCharacterClass.Cleric, Archetype.Healer).SetName("Cleric-like");
            yield return new TestCaseData(Catalog("AugDex", "AugQui", "AugCon", "AugStr", "MasteryOfArchery", "FalconsEye", "Dodger",
                "Toughness", Purge, SecondWind), (int)eCharacterClass.Scout, Archetype.Archer).SetName("Scout-like");
        }

        [TestCaseSource(nameof(Classes))]
        public void TheBuildFitsTheClassGrowsWithPointsAndMeetsEveryPrerequisite(Dictionary<string, int> available, int classId, Archetype archetype)
        {
            Assert.That(ArchetypeOf(available.Keys), Is.EqualTo(archetype));
            Dictionary<string, int> previous = new();
            for (int points = 0; points <= 120; points++)
            {
                var plan = Plan(available, archetype, classId, points, Cost);
                int spent = plan.Sum(pair => Enumerable.Range(0, pair.Value).Sum(level => Cost(pair.Key, level)));
                Assert.That(spent, Is.LessThanOrEqualTo(points), $"points={points}");
                foreach (var (key, level) in plan)
                {
                    Assert.That(available.ContainsKey(key), Is.True, key);
                    Assert.That(level, Is.LessThanOrEqualTo(available[key]), key);
                    if (Requirement(key, classId) is { } need)
                        Assert.That(plan.GetValueOrDefault(need.Key), Is.GreaterThanOrEqualTo(need.Level), $"{key} needs {need.Key} {need.Level} (points={points})");
                }
                foreach (var (key, level) in previous)
                    Assert.That(plan.GetValueOrDefault(key), Is.GreaterThanOrEqualTo(level), $"{key} never shrinks (points={points})");
                Assert.That(plan.ContainsKey(Purge) && plan.ContainsKey(PurgeReduced), Is.False, "one purge");
                previous = plan;
            }
            Assert.That(previous.Count, Is.GreaterThan(4), "a high rank buys a full build");
        }

        [Test]
        public void AMeleeBotBuysItsFirstMasteryOnlyAfterTheAugmentItNeeds()
        {
            var available = Catalog(Passives.Where(k => k.StartsWith("Aug") || k == "MasteryOfPain").ToArray());
            var plan = Plan(available, Archetype.Melee, (int)eCharacterClass.Armsman, 4, Cost);
            Assert.That(plan, Is.EquivalentTo(new Dictionary<string, int> { ["AtlasOF_AugCon"] = 1, ["AtlasOF_AugDex"] = 1 }),
                "4 points: AugCon 1 (1) + AugDex 1 (1); AugDex 2 costs 3 more, so it waits");
            plan = Plan(available, Archetype.Melee, (int)eCharacterClass.Armsman, 6, Cost);
            Assert.That(plan["AtlasOF_MasteryOfPain"], Is.EqualTo(1));
        }

        [TestCase(5, 0, ExpectedResult = 0)]
        [TestCase(20, 0, ExpectedResult = 1)]
        [TestCase(50, 23, ExpectedResult = 23)]
        public int PointsFollowThePlayerRule(int level, int realmLevel) => Points(level, realmLevel);

        [TestCase("trained-level|50|realm-points|17", ExpectedResult = 17)]
        [TestCase("trained-level|50", ExpectedResult = 0)]
        [TestCase("", ExpectedResult = 0)]
        [TestCase(null, ExpectedResult = 0)]
        public int TrainedRealmPointsAreReadFromTheSavedAbilities(string serialized) => GameBot.ParseTrainedRealmPoints(serialized);

        [Test]
        public void OnlyActivesBotsCanUseAreBought() =>
            Assert.That(UsableActives, Is.EquivalentTo(new[] { Purge, PurgeReduced, IgnorePainTank, IgnorePain, SecondWind, FirstAid }));
    }
}

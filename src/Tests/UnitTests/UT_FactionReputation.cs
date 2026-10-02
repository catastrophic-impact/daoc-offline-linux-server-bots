using System.Linq;
using DOL.Database;
using DOL.GS;
using DOL.GS.Quests;
using DOL.Network;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public sealed class UT_FactionReputation
{
    private static FactionTargetCandidate Candidate(string name, byte typical, int spawns = 20) => new()
    {
        Name = name, TypicalLevel = typical, MinLevel = typical, MaxLevel = typical,
        SpawnCount = spawns, RegionId = 181, FactionId = 69
    };

    private static readonly FactionTargetCandidate[] Pool =
    {
        Candidate("botonid seedling", 4), Candidate("sporite", 11), Candidate("spiky botonid", 15),
        Candidate("botonid tuber", 30), Candidate("twisted barkstripper", 38), Candidate("corrupt sylvan", 39),
        Candidate("young botonid disperser", 45), Candidate("corruptor", 47), Candidate("botonid disperser", 51),
        Candidate("rare lurker", 44, spawns: 2)
    };

    [Test]
    public void LevelFiftyPlayersAreCappedAtFortyFiveNeverSentAboveIt()
    {
        var pool = FactionReputationTargets.SelectPool(Pool, 50, null);
        Assert.That(pool.Select(c => c.Name), Is.EquivalentTo(new[] { "young botonid disperser" }));
    }

    [Test]
    public void TargetIsNearestLevelRoundedDownWithAThreeLevelWindow()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FactionReputationTargets.SelectPool(Pool, 40, null).Select(c => c.Name),
                Is.EquivalentTo(new[] { "twisted barkstripper", "corrupt sylvan" }));
            Assert.That(FactionReputationTargets.SelectPool(Pool, 14, null).Select(c => c.Name),
                Is.EquivalentTo(new[] { "sporite" }));
            // Below every target: the lowest ones, never something far above.
            Assert.That(FactionReputationTargets.SelectPool(Pool, 1, null).Select(c => c.Name),
                Is.EquivalentTo(new[] { "botonid seedling" }));
        });
    }

    [Test]
    public void RerollExcludesThePreviousSpeciesAndRareSpeciesAreOnlyAFallback()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FactionReputationTargets.SelectPool(Pool, 50, "young botonid disperser").Select(c => c.Name),
                Is.EquivalentTo(new[] { "twisted barkstripper", "corrupt sylvan" }));
            Assert.That(FactionReputationTargets.SelectPool(Pool, 50, null).Any(c => c.Name == "rare lurker"), Is.False);
            Assert.That(FactionReputationTargets.SelectPool(new[] { Candidate("rare lurker", 44, spawns: 2) }, 50, null)
                .Select(c => c.Name), Is.EquivalentTo(new[] { "rare lurker" }));
            Assert.That(FactionReputationTargets.SelectPool(new[] { Candidate("sporite", 11) }, 20, "sporite"), Is.Empty);
        });
    }

    [Test]
    public void TurnInGivesTenOrFiveAfterAReroll()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FactionReputationQuest.ReputationGainFor(false), Is.EqualTo(10));
            Assert.That(FactionReputationQuest.ReputationGainFor(true), Is.EqualTo(5));
            Assert.That(FactionReputationQuest.RequiredKillCount, Is.EqualTo(10));
        });
    }

    [Test]
    public void ReputationIsShownOutOfOneHundredAndStablesOpenAtMinusFifty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FactionEmissaryRuntime.DescribeStanding(100), Is.EqualTo("-100/100 (Aggressive)"));
            Assert.That(FactionEmissaryRuntime.DescribeStanding(60), Is.EqualTo("-60/100 (Hostile)"));
            // Hostility 50 is Neutral, the first standing a stable master serves.
            Assert.That(FactionEmissaryRuntime.DescribeStanding(50), Is.EqualTo("-50/100 (Neutral)"));
            Assert.That(FactionEmissaryRuntime.DescribeStanding(-130), Is.EqualTo("100/100 (Friendly)"));
            Assert.That(Faction.ClampAggro(140), Is.EqualTo(100));
            Assert.That(Faction.ClampAggro(-140), Is.EqualTo(-100));
            Assert.That(Faction.StandingForAggro(51), Is.EqualTo(Faction.Standing.HOSTILE));
            Assert.That(Faction.StandingForAggro(FactionEmissaryRuntime.RequiredAggroForStables), Is.EqualTo(Faction.Standing.NEUTRAL));
        });
    }

    [Test]
    public void EnemyPenaltyOnlyTouchesLinkedFactionsThatStartHostile()
    {
        Faction krrzck = MakeFaction(89, "Krrzck", 100);
        Faction botonids = MakeFaction(69, "Botonids", 100);
        Faction watcher = MakeFaction(111, "The Watcher", 10);
        Faction stranger = MakeFaction(500, "Unlinked", 100);
        krrzck.EnemyFactions.Add(botonids);
        krrzck.EnemyFactions.Add(watcher); // Hypothetical: a friendly-by-default enemy is never pushed further.

        Assert.Multiple(() =>
        {
            Assert.That(FactionReputationQuest.ShouldPenalizeEnemy(krrzck, botonids), Is.True);
            Assert.That(FactionReputationQuest.ShouldPenalizeEnemy(krrzck, watcher), Is.False);
            Assert.That(FactionReputationQuest.ShouldPenalizeEnemy(krrzck, stranger), Is.False);
            Assert.That(FactionReputationQuest.ShouldPenalizeEnemy(krrzck, krrzck), Is.False);
            Assert.That(FactionReputationQuest.ShouldPenalizeEnemy(krrzck, null), Is.False);
        });
    }

    [TestCase("botonid seedling", "Domnann", 0, 10, false)]
    [TestCase("botonid seedling", "Domnann", 10, 10, true)]
    [TestCase("an unusually long monster name that must be shortened for the old client", "An equally long zone name that also needs trimming", 7, 10, false)]
    public void JournalDescriptionFitsTheOldClientField(string name, string zone, int progress, int required, bool ready)
    {
        string text = FactionReputationQuest.FormatJournalDescription(name, zone, progress, required, ready, "Cryptos Mythicos", 5);
        Assert.Multiple(() =>
        {
            Assert.That(BaseServer.DefaultEncoding.GetByteCount(text), Is.LessThanOrEqualTo(255));
            Assert.That(text, Does.Contain($"{progress}/{required}"));
        });
    }

    [Test]
    public void EachRealmHasOneEmissaryForItsNeutralTownFaction()
    {
        var definitions = FactionEmissaryRuntime.Definitions;
        Assert.Multiple(() =>
        {
            Assert.That(definitions.Select(d => d.Realm), Is.EquivalentTo(new[] { eRealm.Hibernia, eRealm.Albion, eRealm.Midgard }));
            Assert.That(definitions.Select(d => (d.FactionId, d.HostStableMaster, d.TownName)), Is.EquivalentTo(new[]
            {
                (89, "Zrrazk", "Necht"), (16, "Vilmalin", "Caer Diogel"), (172, "Korlis", "Hagall")
            }));
            Assert.That(definitions.All(d => AutonomousNeutralTownPolicy.TownFactionIds.Contains(d.FactionId)), Is.True);
            Assert.That(definitions.All(d => d.HuntRegionId == d.RegionId), Is.True);
        });
    }

    [Test]
    public void EmissariesNameOnlyTheStableMastersThatCheckReputation()
    {
        var byRealm = FactionEmissaryRuntime.Definitions.ToDictionary(d => d.Realm);
        Assert.Multiple(() =>
        {
            // Realm and faction-less stable masters serve everyone of their realm.
            Assert.That(FactionEmissaryRuntime.GatedStablesLine(byRealm[eRealm.Midgard]), Does.Contain("Korlis flies any Midgardian").And.Contain("Minerva"));
            Assert.That(FactionEmissaryRuntime.GatedStablesLine(byRealm[eRealm.Albion]), Does.Contain("Vilmalin carriesss anyone").And.Contain("Nimea").And.Contain("Callisa"));
            Assert.That(FactionEmissaryRuntime.GatedStablesLine(byRealm[eRealm.Hibernia]), Does.Contain("Zrrazk").And.Contain("Dalniver").And.Contain("Calvine"));
            Assert.That(FactionEmissaryRuntime.StandingLine(100), Does.StartWith("Your reputation: -100/100 (Aggressive).").And.Contain("need -50 or better"));
            Assert.That(FactionEmissaryRuntime.StandingLine(50), Does.Contain("-50/100 (Neutral)").And.Contain("enough"));
            Assert.That(byRealm[eRealm.Midgard].Heading, Is.EqualTo((ushort)2590));
            Assert.That(byRealm[eRealm.Hibernia].Heading, Is.Null);
            Assert.That(byRealm[eRealm.Albion].Heading, Is.Null);
        });
    }

    [Test]
    public void NeutralTownCoversOnlyThatFactionNearItsOwnStableMaster()
    {
        var anchors = new[] { new AutonomousNeutralTownPolicy.TownAnchor(181, 89, 100_000, 100_000) };
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousNeutralTownPolicy.IsWithinTown(anchors, 181, 89, 101_500, 101_900), Is.True);
            Assert.That(AutonomousNeutralTownPolicy.IsWithinTown(anchors, 181, 89, 102_000, 102_000), Is.False);
            Assert.That(AutonomousNeutralTownPolicy.IsWithinTown(anchors, 181, 69, 100_100, 100_100), Is.False);
            Assert.That(AutonomousNeutralTownPolicy.IsWithinTown(anchors, 200, 89, 100_100, 100_100), Is.False);
        });
    }

    private static Faction MakeFaction(int id, string name, int baseAggro)
    {
        var faction = new Faction();
        faction.LoadFromDatabase(new DbFaction { ID = id, Name = name, BaseAggroLevel = baseAggro });
        return faction;
    }
}

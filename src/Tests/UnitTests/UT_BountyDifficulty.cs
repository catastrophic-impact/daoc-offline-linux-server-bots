using System.Collections.Generic;
using System.Linq;
using DOL.GS;
using DOL.GS.Quests;
using DOL.Network;
using NUnit.Framework;

namespace DOL.UnitTests;

/// <summary>Normal / Hard / Very Hard leveling bounties and 0.33 bounty conversion.</summary>
[TestFixture]
public sealed class UT_BountyDifficulty
{
    [TestCase(1, 5)]
    [TestCase(19, 5)]
    [TestCase(20, 10)]
    [TestCase(29, 10)]
    [TestCase(30, 15)]
    [TestCase(39, 15)]
    [TestCase(40, 20)]
    [TestCase(49, 20)]
    [TestCase(50, 1)]
    public void KillCountsFollowTheLevelBrackets(int level, int kills) =>
        Assert.That(BountyQuest.RequiredKillsForLevel(level), Is.EqualTo(kills));

    [TestCase(BountyDifficulty.Normal, false, 2)]
    [TestCase(BountyDifficulty.Normal, true, 1)]
    [TestCase(BountyDifficulty.Hard, false, 4)]
    [TestCase(BountyDifficulty.Hard, true, 2)]
    [TestCase(BountyDifficulty.VeryHard, false, 8)]
    [TestCase(BountyDifficulty.VeryHard, true, 4)]
    public void BulbsByDifficultyAndReroll(BountyDifficulty difficulty, bool rerolled, int bulbs) =>
        Assert.That(BountyDifficultyRules.Bulbs(difficulty, rerolled), Is.EqualTo(bulbs));

    [TestCase(1, false, BountyDifficulty.Hard, 20L)]
    [TestCase(1, true, BountyDifficulty.Hard, 10L)]
    [TestCase(1, false, BountyDifficulty.VeryHard, 40L)]
    [TestCase(1, true, BountyDifficulty.VeryHard, 20L)]
    [TestCase(49, false, BountyDifficulty.VeryHard, 25_600_000_000L)]
    [TestCase(49, false, BountyDifficulty.Normal, 6_400_000_000L)]
    public void ExperienceIsBulbsOfTheAssignedLevel(int level, bool rerolled, BountyDifficulty difficulty, long expected) =>
        Assert.That(BountyRewardService.CalculateExperienceReward((byte)level, rerolled, 1.0, difficulty),
            Is.EqualTo(expected));

    [TestCase(10, BountyDifficulty.Normal, 11)]
    [TestCase(10, BountyDifficulty.Hard, 13)]
    [TestCase(10, BountyDifficulty.VeryHard, 15)]
    [TestCase(48, BountyDifficulty.Hard, 51)]
    [TestCase(49, BountyDifficulty.VeryHard, 51)]   // rounded down to the highest item level
    public void GearIsOneThreeOrFiveLevelsAboveTheAssignedLevel(int level, BountyDifficulty difficulty, int gear) =>
        Assert.That(BountyDifficultyRules.GearLevel((byte)level, difficulty), Is.EqualTo(gear));

    [Test]
    public void TargetLevelsRoundDownTwoLevelsAtMostAndNeverReachNormal()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BountyDifficultyRules.TargetLevels(10, BountyDifficulty.Hard), Is.EqualTo(new byte[] { 16, 15, 14 }));
            Assert.That(BountyDifficultyRules.TargetLevels(10, BountyDifficulty.VeryHard), Is.EqualTo(new byte[] { 22, 21, 20 }));
            Assert.That(BountyDifficultyRules.TargetLevels(49, BountyDifficulty.Hard), Is.EqualTo(new byte[] { 55, 54, 53 }));
            Assert.That(BountyDifficultyRules.TargetLevels(49, BountyDifficulty.VeryHard), Is.EqualTo(new byte[] { 61, 60, 59 }));
            Assert.That(BountyDifficultyRules.TargetLevels(49, BountyDifficulty.Normal), Is.Empty);
            Assert.That(BountyDifficultyRules.TargetLevels(50, BountyDifficulty.VeryHard), Is.Empty);
        });
    }

    private static BountyTargetCandidate Camp(string name, byte level, int spawns = 3) =>
        new() { Name = name, Level = level, SpawnCount = spawns, RepresentativeMobId = name + level };

    [Test]
    public void ChallengePoolUsesTheExactLevelWhenItHasEnoughVariety()
    {
        var candidates = Enumerable.Range(0, 8).Select(i => Camp($"monster {i}", 55))
            .Append(Camp("lower monster", 54))
            .Append(Camp("normal monster", 49))
            .ToArray();

        BountyTargetCandidate[] pool = BountyTargetCatalog.SelectChallengePool(candidates, 49, BountyDifficulty.Hard);

        Assert.That(pool.Select(c => c.Level).Distinct(), Is.EqualTo(new byte[] { 55 }));
        Assert.That(pool, Has.Length.EqualTo(8));
    }

    [Test]
    public void ThinChallengeLevelRoundsDownButNeverBelowTheFloor()
    {
        var candidates = new List<BountyTargetCandidate>
        {
            Camp("a", 61), Camp("b", 61),           // only two species at +12
            Camp("c", 60), Camp("d", 60),
            Camp("e", 59),
            Camp("too easy", 58), Camp("normal", 49),
            Camp("single spawn", 61, spawns: 1)
        };

        BountyTargetCandidate[] pool = BountyTargetCatalog.SelectChallengePool(candidates, 49, BountyDifficulty.VeryHard);

        Assert.Multiple(() =>
        {
            Assert.That(pool.Select(c => c.Name), Is.EquivalentTo(new[] { "a", "b", "c", "d", "e" }));
            Assert.That(pool.All(c => c.Level >= 59), Is.True, "Very Hard never rounds below +10.");
        });
    }

    [Test]
    public void SingleSpawnsAreUsedOnlyWhenNoCampExistsInTheWindow()
    {
        var candidates = new[] { Camp("lone", 14, spawns: 1), Camp("normal", 10) };
        BountyTargetCandidate[] pool = BountyTargetCatalog.SelectChallengePool(candidates, 10, BountyDifficulty.Hard);
        Assert.That(pool.Select(c => c.Name), Is.EqualTo(new[] { "lone" }));
    }

    [TestCase(BountyDifficulty.Normal, 53, 20, true)]
    [TestCase(BountyDifficulty.Hard, 53, 53, true)]
    [TestCase(BountyDifficulty.Hard, 53, 52, true)]
    [TestCase(BountyDifficulty.Hard, 53, 51, false)]
    [TestCase(BountyDifficulty.VeryHard, 61, 40, false)]
    public void HardKillsMustBeNearTheMarkedLevel(BountyDifficulty difficulty, int target, int killed, bool counts) =>
        Assert.That(BountyDifficultyRules.KillCounts(difficulty, target, killed), Is.EqualTo(counts));

    [TestCase("normal", BountyDifficulty.Normal)]
    [TestCase("Hard", BountyDifficulty.Hard)]
    [TestCase(" very hard ", BountyDifficulty.VeryHard)]
    public void DialogueChoicesParse(string text, BountyDifficulty expected)
    {
        Assert.That(BountyDifficultyRules.TryParseChoice(text, out BountyDifficulty parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(expected));
        Assert.That(BountyDifficultyRules.Parse(BountyDifficultyRules.Save(expected)), Is.EqualTo(expected));
    }

    [Test]
    public void UnknownChoiceAndMissingSavedDifficultyAreNormal()
    {
        Assert.That(BountyDifficultyRules.TryParseChoice("impossible", out _), Is.False);
        Assert.That(BountyDifficultyRules.Parse(null), Is.EqualTo(BountyDifficulty.Normal));
    }

    // A real bounty saved by 0.33 (taken from a live save): level 47, 17 of 48 kills.
    private const string Saved033Bounty =
        "zone=183;targetname=spiteful sylvanshade;zonename=Vale of Balor;targetlevel=47;y=362546;region=181;" +
        "z=3702;dungeon=0;epic=0;assigned=47;goal1Target=48;rerolled=0;required=48;" +
        "targetid=04625736-fbee-42c3-8c0d-2e9b1a892116;goal1Current=17;x=305451;";

    private static Dictionary<string, string> Parse(string saved) => saved.Split(';')
        .Where(pair => pair.Length > 0)
        .Select(pair => pair.Split('='))
        .ToDictionary(pair => pair[0], pair => pair[1]);

    [Test]
    public void BountySavedBy033IsRecognisedAndKeepsItsProgressUnderTheNewCount()
    {
        Dictionary<string, string> saved = Parse(Saved033Bounty);
        saved.TryGetValue("format", out string format);
        saved.TryGetValue("difficulty", out string difficulty);

        (int required, int progress) = BountyQuest.LegacyKillCounts(
            byte.Parse(saved["assigned"]), int.Parse(saved["required"]), int.Parse(saved["goal1Current"]));

        Assert.Multiple(() =>
        {
            Assert.That(BountyQuest.IsLegacyFormatValue(format), Is.True);
            Assert.That(BountyDifficultyRules.Parse(difficulty), Is.EqualTo(BountyDifficulty.Normal));
            Assert.That(required, Is.EqualTo(20));
            Assert.That(progress, Is.EqualTo(17));
            Assert.That(BountyQuest.IsLegacyFormatValue(BountyQuest.CurrentFormat), Is.False);
        });
    }

    [TestCase(47, 48, 30, 20, 20)]   // already past the new count: ready to claim
    [TestCase(12, 15, 3, 5, 3)]
    [TestCase(30, 33, 0, 15, 0)]
    [TestCase(30, 33, -4, 15, 0)]    // corrupt saved progress cannot go negative
    public void LegacyKillCountsDropToTheNewBracket(int assigned, int savedRequired, int savedProgress,
        int required, int progress) =>
        Assert.That(BountyQuest.LegacyKillCounts((byte)assigned, savedRequired, savedProgress),
            Is.EqualTo((required, progress)));

    [Test]
    public void VeryHardJournalFitsTheOldClientAndStatesItsReward()
    {
        var target = new BountyTargetCandidate
        {
            Name = new string('Z', 400),
            ZoneName = new string('Q', 400),
            IsDungeon = true
        };

        string description = BountyQuest.FormatJournalDescription(target, 49, false, 20, 20, true,
            BountyDifficulty.VeryHard);
        string name = BountyQuest.FormatQuestName(target, BountyDifficulty.VeryHard);

        Assert.Multiple(() =>
        {
            Assert.That(BaseServer.DefaultEncoding.GetByteCount(description), Is.LessThanOrEqualTo(255));
            Assert.That(description, Does.Contain("20/20"));
            Assert.That(description, Does.Contain("8 bulbs of Lv49 XP"));
            Assert.That(description, Does.Contain("Lv51 class items"));
            Assert.That(description, Does.EndWith("Map: /bountylocation."));
            Assert.That(name, Does.StartWith("Very Hard Bounty: "));
            Assert.That(BaseServer.DefaultEncoding.GetByteCount(name), Is.LessThanOrEqualTo(255));
        });
    }

    [Test]
    public void RerolledHardJournalShowsTwoBulbs()
    {
        var target = new BountyTargetCandidate { Name = "strapper vine", ZoneName = "Vigilant Rock" };
        string description = BountyQuest.FormatJournalDescription(target, 31, true, 7, 15, false, BountyDifficulty.Hard);
        Assert.That(description, Does.Contain("2 bulbs of Lv31 XP, 1-3 Lv34 class items"));
    }
}

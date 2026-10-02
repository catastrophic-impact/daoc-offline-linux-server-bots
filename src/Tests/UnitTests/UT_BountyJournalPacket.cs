using DOL.GS;
using DOL.GS.Quests;
using DOL.Network;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public sealed class UT_BountyJournalPacket
{
    [TestCase("strapper vine", "Vigilant Rock", false, 31, 0, 33, false)]
    [TestCase("strapper vine", "Vigilant Rock", false, 31, 33, 33, true)]
    [TestCase("Cuuldurach the Glimmer King", "Sheeroe Hills", false, 50, 0, 1, false)]
    [TestCase("King Tuscar", "Tuscaran Glacier", true, 50, 1, 1, true)]
    [TestCase("an unusually long monster name that must be shortened for the old client", "An equally long dungeon zone name", true, 49, 50, 50, true)]
    public void BountyJournalDescriptionFitsOldClientPascalFieldAndShowsProgress(
        string name, string zone, bool dungeon, byte level, int current, int required, bool ready)
    {
        var target = new BountyTargetCandidate
        {
            Name = name,
            ZoneName = zone,
            IsDungeon = dungeon,
            IsEpic = level == 50
        };

        string description = BountyQuest.FormatJournalDescription(target, level,
            rerolled: false, current, required, ready);

        Assert.Multiple(() =>
        {
            Assert.That(BaseServer.DefaultEncoding.GetByteCount(description), Is.LessThanOrEqualTo(255));
            Assert.That(description, Does.Contain($"{current}/{required}"));
            Assert.That(description, Does.Contain(name[..System.Math.Min(name.Length, 20)]));
            Assert.That(description, Does.EndWith("Map: /bountylocation."));
            Assert.That(BaseServer.DefaultEncoding.GetByteCount(BountyQuest.FormatQuestName(target)),
                Is.LessThanOrEqualTo(255));
            Assert.That(BaseServer.DefaultEncoding.GetByteCount(BountyQuest.FormatGoalName(target) + " (50/50)\r"),
                Is.LessThanOrEqualTo(255));
            if (ready)
                Assert.That(description, Does.Contain("Return to the Bounty Master"));
        });
    }

    [Test]
    public void RerolledDescriptionStillFitsAndStatesOneBulb()
    {
        var target = new BountyTargetCandidate { Name = "strapper vine", ZoneName = "Vigilant Rock" };
        string description = BountyQuest.FormatJournalDescription(target, 31, true, 7, 33, false);

        Assert.Multiple(() =>
        {
            Assert.That(BaseServer.DefaultEncoding.GetByteCount(description), Is.LessThanOrEqualTo(255));
            Assert.That(description, Does.Contain("7/33"));
            Assert.That(description, Does.Contain("1 bulb"));
        });
    }

    [Test]
    public void EvenOversizedDatabaseNamesCannotBreakQuestEntryOrHideKillCount()
    {
        var target = new BountyTargetCandidate
        {
            Name = new string('Z', 400),
            ZoneName = new string('Q', 400),
            IsDungeon = true
        };

        string description = BountyQuest.FormatJournalDescription(target, 49, false, 49, 50, true);

        Assert.Multiple(() =>
        {
            Assert.That(BaseServer.DefaultEncoding.GetByteCount(description), Is.LessThanOrEqualTo(255));
            Assert.That(description, Does.Contain("49/50"));
            Assert.That(description, Does.Contain("Return to the Bounty Master"));
            Assert.That(description, Does.EndWith("Map: /bountylocation."));
            Assert.That(BaseServer.DefaultEncoding.GetByteCount(BountyQuest.FormatQuestName(target)),
                Is.LessThanOrEqualTo(255));
            Assert.That(BaseServer.DefaultEncoding.GetByteCount(BountyQuest.FormatGoalName(target) + " (49/50)\r"),
                Is.LessThanOrEqualTo(255));
        });
    }

    [Test]
    public void CorruptSavedCountsCannotProduceTruncatedQuestText()
    {
        var target = new BountyTargetCandidate
        {
            Name = new string('N', 400),
            ZoneName = new string('Z', 400),
            IsDungeon = true
        };

        string description = BountyQuest.FormatJournalDescription(target, 255, true,
            int.MinValue, int.MaxValue, true);

        Assert.Multiple(() =>
        {
            Assert.That(BaseServer.DefaultEncoding.GetByteCount(description), Is.LessThanOrEqualTo(255));
            Assert.That(description, Does.Contain($"{int.MinValue}/{int.MaxValue}"));
            Assert.That(description, Does.EndWith("."));
        });
    }
}

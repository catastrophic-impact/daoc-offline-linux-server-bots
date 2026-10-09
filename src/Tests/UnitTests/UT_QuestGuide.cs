using System.Linq;
using System.Text.Json;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>The Quest Journal's QUEST GUIDE (owner 2026-10-07): period walkthroughs paged into the 2 KB detail window,
/// and the classic "speak the words of power" step.</summary>
[TestFixture]
public class UT_QuestGuide
{
    [Test]
    public void LongWalkthroughsArePagedIntoWindowSizedPieces()
    {
        var guide = new QuestGuide.Guide
        {
            Name = "Enchanting Willow", Realm = "Hibernia", Source = "Allakhazam quest page as archived 2004-10-13 (Wayback Machine)",
            Lines = Enumerable.Range(0, 40).Select(i => $"Paragraph {i}: " + string.Join(' ', Enumerable.Repeat("dead trees north of Connla", 20))).ToList()
        };
        var lines = QuestGuide.GuideLines(guide.Name, guide);
        var pages = QuestGuide.Paginate(lines);
        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo(QuestGuide.PeriodNote), "the period note heads every walkthrough");
            Assert.That(pages.Count, Is.GreaterThan(5));
            Assert.That(pages.All(p => p.Sum(l => l.Length + 2) <= QuestGuide.PageCharacters), Is.True);
            Assert.That(pages.SelectMany(p => p).All(l => l.Length <= 255), Is.True, "a packet line holds 255 characters");
            Assert.That(string.Join(" ", pages.SelectMany(p => p)), Does.Contain("Paragraph 39:"), "nothing is dropped");
        });
    }

    [Test]
    public void GuideFileFromTheBuilderParses()
    {
        const string sample = """
        {"Guides": {"hibernia|enchanting willow": {"Name": "Enchanting Willow", "Realm": "Hibernia",
          "Source": "Allakhazam quest page as archived 2004-10-13 (Wayback Machine)", "Updated": "Mon May  3 16:38:07 2004",
          "Level": "7", "Lines": ["- Locate NPC Ciar north of Connla and a fork in the road."]}}}
        """;
        using var doc = JsonDocument.Parse(sample);
        var guide = doc.RootElement.GetProperty("Guides").GetProperty("hibernia|enchanting willow").Deserialize<QuestGuide.Guide>();
        Assert.That(guide.Name, Is.EqualTo("Enchanting Willow"));
        Assert.That(guide.Lines, Has.Count.EqualTo(1));
    }

    [TestCase("Proximare spiritus, nonorare!", "proximare spiritus nonorare")]
    [TestCase("  abeo   decido animus exspecto terra. ", "abeo decido animus exspecto terra")]
    public void SpokenWordsIgnoreCaseSpacingAndPunctuation(string said, string words)
    {
        Assert.That(ClassicQuests.WordsOnly(said), Does.Contain(ClassicQuests.WordsOnly(words)));
    }
}

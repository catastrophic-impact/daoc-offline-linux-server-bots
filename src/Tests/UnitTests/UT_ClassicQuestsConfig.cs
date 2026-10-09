using System.Text.Json;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>
/// classic-quests.json is written by the goal 10 generator (development-source/research/period-data/quests); these
/// samples are copied from its output so a renamed property cannot silently switch a step off.
/// </summary>
[TestFixture]
public class UT_ClassicQuestsConfig
{
    private const string Sample = """
    {
      "Quests": {
        "20001": { "Steps": [ null,
          { "Marker": { "Region": 1, "X": 529929, "Y": 479033, "Z": 2200 },
            "Custom": { "Kind": "trade", "Target": "Stonemason Harwin",
                        "Trades": { "cq_flat_chert": "cq_flint_knife", "cq_round_chert": "" },
                        "Drops": [ { "Mob": "river spriteling", "Item": "cq_flat_chert", "Chance": 30 } ] } },
          { "Grant": [ "cq_vial_of_mud" ], "GrantFrom": "Lirele" },
          { "Custom": { "Kind": "use_item", "Item": "cq_wounding_stone", "Consume": true, "Give": true,
                        "Region": 1, "X": 43000, "Y": 45000, "Z": 0, "Radius": 600 } },
          { "Custom": { "Kind": "quest", "Quest": "Wizard Lost|Barbaric Tales|Forged Excellence", "Count": 2 } },
          { "Custom": { "Kind": "choose", "Target": "Master Vismer",
                        "Choices": { "Staff of Frozen Tears": "cq_alb_staff_of_frozen_tears" } } } ],
          "Races": [ "Briton" ] }
      },
      "QuestMonsterIds": [ "abc" ],
      "Chat": { "Troya": { "have time": "Excellent!" } }
    }
    """;

    [Test]
    public void GeneratorOutput_ParsesIntoEveryStepField()
    {
        var config = JsonSerializer.Deserialize<ClassicQuests.Config>(Sample, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var steps = config.Quests[20001].Steps;
        Assert.Multiple(() =>
        {
            Assert.That(steps[0], Is.Null);
            Assert.That(steps[1].Custom.Kind, Is.EqualTo("trade"));
            Assert.That(steps[1].Custom.Trades["cq_flat_chert"], Is.EqualTo("cq_flint_knife"));
            Assert.That(steps[1].Custom.Trades["cq_round_chert"], Is.Empty);
            Assert.That(steps[1].Custom.Drops[0].Mob, Is.EqualTo("river spriteling"));
            Assert.That(steps[1].Custom.Drops[0].Chance, Is.EqualTo(30));
            Assert.That(steps[2].Grant, Is.EqualTo(new[] { "cq_vial_of_mud" }));
            Assert.That(steps[2].GrantFrom, Is.EqualTo("Lirele"));
            Assert.That(steps[3].Custom.Consume && steps[3].Custom.Give, Is.True);
            Assert.That(steps[3].Custom.Radius, Is.EqualTo(600));
            Assert.That(steps[4].Custom.Count, Is.EqualTo(2));
            Assert.That(steps[5].Custom.Choices["Staff of Frozen Tears"], Is.EqualTo("cq_alb_staff_of_frozen_tears"));
            Assert.That(config.Chat["Troya"]["have time"], Is.EqualTo("Excellent!"));
        });
    }

    [Test]
    public void CustomStep_DefaultsToAModestRadius()
    {
        Assert.That(new ClassicQuests.CustomStep().Radius, Is.EqualTo(600));
    }
}

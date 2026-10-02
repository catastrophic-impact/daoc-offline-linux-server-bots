using DOL.GS;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public sealed class UT_BountyConsecutiveSelection
{
    private static readonly BountyTargetCandidate[] Candidates =
    {
        new() { Name = "large frog", ZoneName = "Connacht", RepresentativeMobId = "frog-1" },
        new() { Name = "Large Frog", ZoneName = "Lough Derg", RepresentativeMobId = "frog-2" },
        new() { Name = "beach rat", ZoneName = "Shannon Estuary", RepresentativeMobId = "rat-1" }
    };

    [Test]
    public void NewContractExcludesAllSpawnsOfPreviousMonster()
    {
        BountyTargetCandidate[] choices = BountyQuest.GetAssignmentChoices(Candidates,
            " LARGE FROG ");

        Assert.Multiple(() =>
        {
            Assert.That(choices, Has.Length.EqualTo(1));
            Assert.That(choices[0].Name, Is.EqualTo("beach rat"));
        });
    }

    [Test]
    public void NewContractDeclinesWhenNoOtherMonsterIsEligible()
    {
        BountyTargetCandidate[] choices = BountyQuest.GetAssignmentChoices(Candidates[..2],
            "large frog");

        Assert.That(choices, Is.Empty);
    }

    [Test]
    public void RerollStillFailsWhenNoDifferentMonsterIsEligible()
    {
        BountyTargetCandidate[] choices = BountyQuest.GetAssignmentChoices(Candidates[..2],
            "large frog");

        Assert.That(choices, Is.Empty);
    }

    [Test]
    public void FirstContractWithoutHistoryKeepsEntirePool()
    {
        BountyTargetCandidate[] choices = BountyQuest.GetAssignmentChoices(Candidates,
            null);

        Assert.That(choices, Has.Length.EqualTo(3));
    }

    [Test]
    public void CatalogFiltersPreviousNameBeforeDensityAndLivePreference()
    {
        BountyTargetCandidate[] filtered = BountyTargetCatalog.ExcludeMonsterName(Candidates,
            "large frog");

        Assert.That(filtered, Has.Length.EqualTo(1));
        Assert.That(filtered[0].Name, Is.EqualTo("beach rat"));
    }
}

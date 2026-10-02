using System.Runtime.CompilerServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public class UT_TemporaryCompanionRecovery
{
    [TestCase(true, true, true, true, true, false, true)]
    [TestCase(true, true, true, false, true, false, false)]
    [TestCase(true, true, true, true, true, true, false)]
    [TestCase(false, true, true, true, true, false, false)]
    [TestCase(true, false, true, true, true, false, false)]
    [TestCase(true, true, false, true, true, false, false)]
    [TestCase(true, true, true, true, false, false, false)]
    public void SafeReleaseWaitsForLivingOwnerAndClearCombat(bool temporary, bool active,
        bool grouped, bool ownerAlive, bool ownerActive, bool fighting, bool expected) =>
        Assert.That(TemporaryCompanionRecovery.CanReleaseToOwner(temporary, active, grouped,
            ownerAlive, ownerActive, fighting), Is.EqualTo(expected));

    [TestCase(2999, false)] [TestCase(3000, false)] [TestCase(3001, true)]
    public void OrdinaryFormationHasGenerousLeash(int distance, bool expected) =>
        Assert.That(TemporaryCompanionRecovery.ShouldRecall(true, true, true, true, true, false, false,
            true, distance, false), Is.EqualTo(expected));

    [Test]
    public void CrossRegionAndDungeonReturnsDoNotDependOnOutdoorRouting()
    {
        Assert.That(TemporaryCompanionRecovery.ShouldRecall(true, true, true, true, true, false, false,
            false, 0, false), Is.True);
        Assert.That(TemporaryCompanionRecovery.ShouldRecall(true, true, true, true, true, false, false,
            true, 0, true), Is.True, "Released companions return even if the corpse was close.");
    }

    [Test]
    public void PersistentBotsAreExcludedEvenAfterRevivalOrAcrossRegions()
    {
        Assert.That(TemporaryCompanionRecovery.ShouldRecall(false, true, true, true, true, false, false,
            false, 99999, true), Is.False);
        var persistent = (GameBot)RuntimeHelpers.GetUninitializedObject(typeof(GameBot));
        Assert.That(persistent.TryReturnTemporaryCompanionToLeader(true), Is.False);
    }

    [TestCase(false, true, true, true)] [TestCase(true, false, true, true)]
    [TestCase(true, true, false, true)] [TestCase(true, true, true, false)]
    public void DeadRemovedUngroupedOrUnboundCompanionsAreNotRecalled(bool alive, bool active, bool grouped, bool follows) =>
        Assert.That(TemporaryCompanionRecovery.ShouldRecall(true, alive, active, grouped, follows, false, false,
            false, 99999, true), Is.False);

    [TestCase(true, false)] [TestCase(false, true)]
    public void RealHorseTravelRemainsAuthoritative(bool riding, bool ownerRiding) =>
        Assert.That(TemporaryCompanionRecovery.ShouldRecall(true, true, true, true, true, riding, ownerRiding,
            true, 99999, false), Is.False);

    [Test]
    public void OwnerResurrectionRequiresLearnedSpellAndNoPartyCombat()
    {
        Assert.That(TemporaryCompanionRecovery.CanPrioritizeOwnerResurrection(true, true, false, true, true, true, false), Is.True);
        Assert.That(TemporaryCompanionRecovery.CanPrioritizeOwnerResurrection(true, true, false, true, true, true, true), Is.False);
        Assert.That(TemporaryCompanionRecovery.CanPrioritizeOwnerResurrection(true, true, false, true, true, false, false), Is.False);
        Assert.That(TemporaryCompanionRecovery.CanPrioritizeOwnerResurrection(false, true, false, true, true, true, false), Is.False);
        Assert.That(TemporaryCompanionRecovery.CanPrioritizeOwnerResurrection(true, false, false, true, true, true, false), Is.False);
        Assert.That(TemporaryCompanionRecovery.CanPrioritizeOwnerResurrection(true, true, true, true, true, true, false), Is.False);
        Assert.That(TemporaryCompanionRecovery.CanPrioritizeOwnerResurrection(true, true, false, false, true, true, false), Is.False);
    }
}

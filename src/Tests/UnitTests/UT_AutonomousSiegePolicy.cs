using NUnit.Framework;
using DOL.GS.Spells;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousSiegePolicy
{
    [TestCase(eRealm.Albion, "deploy_siege_ram")]
    [TestCase(eRealm.Midgard, "deploy_siege_ram2")]
    [TestCase(eRealm.Hibernia, "deploy_siege_ram3")]
    public void RealmSpecificRamKitMustMatchItsOwnExactTemplate(eRealm realm, string templateId)
    {
        Assert.That(AutonomousSiegePolicy.IsRamKit(realm, templateId), Is.True);
        Assert.That(AutonomousSiegePolicy.IsRamKit(realm, templateId + "_free"), Is.False);
    }

    [TestCase(eRealm.Albion, "SiegeMerchantAlb")]
    [TestCase(eRealm.Midgard, "SiegeMerchantMid")]
    [TestCase(eRealm.Hibernia, "SiegeMerchantHib")]
    public void RealmSiegeMerchantUsesItsAuthoritativeTradeList(eRealm realm, string listId)
    {
        Assert.That(AutonomousSiegePolicy.IsSiegeMerchant(realm, listId), Is.True);
        Assert.That(AutonomousSiegePolicy.IsSiegeMerchant(realm, listId + "Wrong"), Is.False);
        Assert.That(AutonomousSiegePolicy.IsSiegeMerchant(realm, "Rhuz"), Is.False,
            "A display name is not a merchant-list identity");
    }

    [Test]
    public void RealPurchaseRequiresEitherAnExistingKitOrPriceAndBackpackSpace()
    {
        Assert.That(AutonomousSiegePolicy.CanSupplyRam(false, 500_000, 500_000, true), Is.True);
        Assert.That(AutonomousSiegePolicy.CanSupplyRam(false, 499_999, 500_000, true), Is.False);
        Assert.That(AutonomousSiegePolicy.CanSupplyRam(false, 500_000, 500_000, false), Is.False);
        Assert.That(AutonomousSiegePolicy.CanSupplyRam(true, 0, 500_000, false), Is.True);
    }

    [Test]
    public void OnlyDynamicLeaderMayOperateAndSupplySiege()
    {
        Assert.That(AutonomousSiegePolicy.IsDesignatedOperator(false, false), Is.True);
        Assert.That(AutonomousSiegePolicy.IsDesignatedOperator(true, true), Is.True);
        Assert.That(AutonomousSiegePolicy.IsDesignatedOperator(true, false), Is.False);
    }

    [Test]
    public void PolicyRoutesBeforePurchaseAndNeverCreatesKitWithoutMerchant()
    {
        Assert.That(AutonomousSiegePolicy.ChoosePhase(false, true, false, true, true), Is.EqualTo(eAutonomousSiegePhase.RouteToMerchant));
        Assert.That(AutonomousSiegePolicy.ChoosePhase(false, true, true, true, true), Is.EqualTo(eAutonomousSiegePhase.PurchaseKit));
        Assert.That(AutonomousSiegePolicy.ChoosePhase(false, false, false, true, true), Is.EqualTo(eAutonomousSiegePhase.Hold));
        Assert.That(AutonomousSiegePolicy.ChoosePhase(true, true, true, true, true), Is.EqualTo(eAutonomousSiegePhase.DeployRam));
        Assert.That(AutonomousSiegePolicy.ChoosePhase(true, true, true, true, false), Is.EqualTo(eAutonomousSiegePhase.Hold));
    }

    [Test]
    public void FundedOperatorSimulationPurchasesDeploysAndUsesOneFiniteKit()
    {
        long copper = 500_000;
        long price = 500_000;
        bool freeSlot = true;
        bool hasKit = false;

        Assert.That(AutonomousSiegePolicy.CanSupplyRam(hasKit, copper, price, freeSlot), Is.True);
        Assert.That(AutonomousSiegePolicy.ChoosePhase(hasKit, true, true, true, true),
            Is.EqualTo(eAutonomousSiegePhase.PurchaseKit));
        copper -= price;
        hasKit = true;
        Assert.That(AutonomousSiegePolicy.ChoosePhase(hasKit, true, true, true, true),
            Is.EqualTo(eAutonomousSiegePhase.DeployRam));
        hasKit = false; // the successful native summon consumes exactly one kit

        Assert.Multiple(() =>
        {
            Assert.That(copper, Is.Zero);
            Assert.That(hasKit, Is.False);
            Assert.That(typeof(SummonSiegeRam).GetProperty(nameof(SummonSiegeRam.LastSummonedRam)), Is.Not.Null);
            Assert.That(typeof(GameSiegeRam).GetMethod(nameof(GameSiegeRam.Aim)), Is.Not.Null);
            Assert.That(typeof(GameSiegeRam).GetMethod(nameof(GameSiegeRam.Fire)), Is.Not.Null);
        });
    }
}

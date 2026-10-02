using NUnit.Framework;
using DOL.Database;

namespace DOL.GS.Tests
{
    [TestFixture]
    public class UT_AutonomousAuctionValuation
    {
        [Test]
        public void Recommend_ValuesStrongerRarerEquipmentAboveCommonEquipment()
        {
            var common = AutonomousAuctionValuation.Recommend(new(12, 1, 90, 80, 1, 8, false));
            var rare = AutonomousAuctionValuation.Recommend(new(12, 1, 90, 80, 6, 38, false));

            Assert.That(rare.BuyoutCopper, Is.GreaterThan(common.BuyoutCopper));
            Assert.That(rare.MinimumBidCopper, Is.LessThan(rare.BuyoutCopper));
        }

        [Test]
        public void Recommend_ValuesARealMaterialStackByQuantity()
        {
            var single = AutonomousAuctionValuation.Recommend(new(20, 1, 100, 100, 3, 0, true));
            var stack = AutonomousAuctionValuation.Recommend(new(20, 25, 100, 100, 3, 0, true));

            Assert.That(stack.BuyoutCopper, Is.GreaterThan(single.BuyoutCopper * 20));
        }

        [Test]
        public void Recommend_SellerDispositionChangesAskWithoutChangingTheItem()
        {
            var facts = new AutonomousAuctionValuation.ItemFacts(30, 1, 100, 100, 5, 45, false);
            var eager = AutonomousAuctionValuation.Recommend(facts, -0.20);
            var patient = AutonomousAuctionValuation.Recommend(facts, 0.30);

            Assert.That(patient.BuyoutCopper, Is.GreaterThan(eager.BuyoutCopper));
        }

        [Test]
        public void ExchangeUpgradeScore_ValuesRealItemStatsAndLevel()
        {
            DbInventoryItem weaker = new() { Level = 10, Quality = 90, DPS_AF = 20, Bonus1 = 2 };
            DbInventoryItem stronger = new() { Level = 14, Quality = 95, DPS_AF = 28, Bonus1 = 6, Bonus2 = 4 };

            Assert.That(AutonomousBotEconomy.EquipmentValue(stronger), Is.GreaterThan(AutonomousBotEconomy.EquipmentValue(weaker)));
        }

        [Test]
        public void ExchangeBuyer_DistinguishesPlayerListingsFromBotListings()
        {
            Assert.That(AutonomousBotEconomy.IsPlayerListing("character-object-id"), Is.True);
            Assert.That(AutonomousBotEconomy.IsPlayerListing(AutonomousBotEconomy.GetOwnerId(17)), Is.False);
        }

        [Test]
        public void VendorSale_UsesFiniteStackValueAndRejectsUndroppableItems()
        {
            DbInventoryItem single = new() { Price = 1, Count = 1, PackSize = 1, IsDropable = true };
            DbInventoryItem stack = new() { Price = 1, Count = 12, PackSize = 1, IsDropable = true };
            DbInventoryItem protectedItem = new() { Price = 100, Count = 1, PackSize = 1, IsDropable = false };

            Assert.That(AutonomousBotEconomy.CalculateStandardVendorSaleCopper(stack),
                Is.GreaterThan(AutonomousBotEconomy.CalculateStandardVendorSaleCopper(single)));
            Assert.That(AutonomousBotEconomy.CalculateStandardVendorSaleCopper(protectedItem), Is.Zero);
        }

        [Test]
        public void PersistentBotInventory_QueuesRealRemovalButNotExchangeTransfer()
        {
            var inventory = new InspectableBotInventory("offlinebot:42");
            DbInventoryItem item = new()
            {
                SlotPosition = (int)eInventorySlot.FirstBackpack,
                IsPersisted = true,
            };

            Assert.That(inventory.AddItem(eInventorySlot.FirstBackpack, item), Is.True);
            Assert.That(inventory.RemoveItem(item), Is.True);
            Assert.That(inventory.PendingDeletionCount, Is.EqualTo(1));

            Assert.That(inventory.AddItem(eInventorySlot.FirstBackpack, item), Is.True);
            Assert.That(inventory.PendingDeletionCount, Is.Zero);
            Assert.That(inventory.RemoveItemWithoutDbDeletion(item), Is.True);
            Assert.That(inventory.PendingDeletionCount, Is.Zero);
        }

        private sealed class InspectableBotInventory(string ownerId) : BotInventory(ownerId)
        {
            public int PendingDeletionCount => _itemsAwaitingDeletion.Count;
        }
    }
}

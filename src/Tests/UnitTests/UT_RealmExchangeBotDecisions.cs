using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using DOL.GS.ServerRules;
using DOL.GS.ServerProperties;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_RealmExchangeBotDecisions
    {
        // As in UT_AutonomousLootFlow, these bots are inert: they never log in,
        // register in the world, start timers, or write a database row.
        private sealed class DecisionBot : GameBot
        {
            private DecisionBot() : base((OfflineWorldBotRecord)null) { }
            public override byte Level { get; set; }
            public override int EffectiveLevel => Level;
            public override eRealm Realm { get; set; }
            public override ICharacterClass CharacterClass => new ClassEnchanter();
            public override void RefreshItemBonuses() { }
        }

        private sealed class DecisionServer : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
            protected override IServerRules ServerRulesImpl => new NormalServerRules();
        }

        // Test candidates and cache-only listings must never cause a query or
        // write through an uninitialized live server database.
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, EmptyReadDatabase>();

        public class EmptyReadDatabase : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (!method.Name.StartsWith("Select") && !method.Name.StartsWith("Find"))
                    throw new InvalidOperationException("Realm Exchange decision test attempted a database mutation: " + method.Name);
                Type result = method.ReturnType;
                if (result.IsGenericType && typeof(IEnumerable).IsAssignableFrom(result))
                    return Activator.CreateInstance(typeof(List<>).MakeGenericType(result.GetGenericArguments()[0]));
                return null;
            }
        }

        private GameServer _previousServer;
        private MarketSearchEngine _previousMarket;
        private MarketSearchEngine _testMarket;
        private int _previousSellRatio;
        private static long _nextId = 910000;
        private readonly List<DbInventoryItem> _marketItems = new();
        private readonly List<DecisionBot> _registeredBots = new();

        [SetUp]
        public void SetUp()
        {
            _previousServer = GameServer.Instance;
            _previousSellRatio = DOL.GS.ServerProperties.Properties.ITEM_SELL_RATIO;
            DOL.GS.ServerProperties.Properties.ITEM_SELL_RATIO = 50;
            GameServer.LoadTestDouble((DecisionServer)RuntimeHelpers.GetUninitializedObject(typeof(DecisionServer)));
            var marketField = typeof(MarketCache).GetField("_searchEngine", BindingFlags.Static | BindingFlags.NonPublic);
            _previousMarket = (MarketSearchEngine)marketField.GetValue(null);
            _testMarket = new MarketSearchEngine();
            marketField.SetValue(null, _testMarket);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (DbInventoryItem item in _marketItems)
                MarketCache.RemoveItem(item);
            foreach (DecisionBot bot in _registeredBots)
                AutonomousBotRegistry.Unregister(bot);
            _marketItems.Clear();
            _registeredBots.Clear();
            _testMarket.Dispose();
            typeof(MarketCache).GetField("_searchEngine", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, _previousMarket);
            GameServer.LoadTestDouble(_previousServer);
            DOL.GS.ServerProperties.Properties.ITEM_SELL_RATIO = _previousSellRatio;
        }

        private static void Field(Type type, object target, string name, object value) =>
            type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private static void Property(GameBot bot, string name, object value) =>
            typeof(GameBot).GetProperty(name).SetValue(bot, value);

        private static T Inert<T>() where T : GameLiving
        {
            T living = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            Field(typeof(GameLiving), living, "<TempProperties>k__BackingField", new PropertyCollection());
            if (living is GameNPC)
                Field(typeof(GameNPC), living, "m_brains", new ArrayList());
            return living;
        }

        private static DecisionBot Bot()
        {
            DecisionBot bot = Inert<DecisionBot>();
            bot.Level = 20;
            bot.Realm = eRealm.Hibernia;
            bot.Name = "Exchange test bot";
            bot.DatabaseID = Interlocked.Increment(ref _nextId);
            bot.InternalID = AutonomousBotEconomy.GetOwnerId(bot.DatabaseID);
            bot.Inventory = new BotInventory(bot.InternalID);
            Property(bot, nameof(GameBot.IsAutonomousWorldBot), true);
            Field(typeof(GameNPC), bot, "m_ownBrain", new BotBrain { Body = bot });
            Field(typeof(GameLiving), bot, "_abilitiesLock", new Lock());
            Field(typeof(GameLiving), bot, "m_abilities", new Dictionary<string, Ability>
            {
                [Abilities.HibArmor] = new Ability(new DbAbility { KeyName = Abilities.HibArmor, Name = "Cloth" }, 1)
            });
            return bot;
        }

        private void GivePersistentRecord(DecisionBot bot, bool betweenInventoryServices, long money = 100_000)
        {
            Field(typeof(GameBot), bot, "<PersistentRecord>k__BackingField", new OfflineWorldBotRecord
            {
                BotId = bot.DatabaseID,
                MoneyCopper = money,
                ObjectiveAssignmentId = betweenInventoryServices
                    ? "between-pve-services-I-" + bot.DatabaseID + "-0"
                    : "ordinary-pve-" + bot.DatabaseID,
            });
            AutonomousBotRegistry.Register(bot);
            _registeredBots.Add(bot);
        }

        private void AddMarketItem(DbInventoryItem item, string ownerId, int slot, int price)
        {
            item.OwnerID = ownerId;
            item.OwnerLot = RealmExchangeBroker.HiberniaOwnerLot;
            item.SlotPosition = slot;
            item.SellPrice = price;
            Assert.That(MarketCache.AddItem(item), Is.True);
            _marketItems.Add(item);
        }

        private static DbItemTemplate Item(string name, int level, int quality, int slot, int price, int dpsAf = 1)
        {
            return new DbItemTemplate
            {
                Id_nb = Guid.NewGuid().ToString(), Name = name, Realm = (int)eRealm.Hibernia,
                Level = level, Item_Type = slot, Object_Type = (int)eObjectType.Cloth,
                Quality = quality, MaxCount = 1, PackSize = 1, MaxCondition = 10000,
                MaxDurability = 10000, DPS_AF = dpsAf, Price = price,
            };
        }

        private static DbInventoryItem AddBackpack(DecisionBot bot, DbItemTemplate template)
        {
            DbInventoryItem item = GameInventoryItem.Create(template);
            Assert.That(bot.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item), Is.True);
            return item;
        }

        private static void FillBackpack(DecisionBot bot)
        {
            while (bot.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack) != eInventorySlot.Invalid)
                AddBackpack(bot, Item("ordinary drop", 1, 80, (int)eInventorySlot.FirstBackpack, 2));
        }

        [Test]
        public void FullInertBackpack_SelectsValuableCandidateBeforeVendorTrash()
        {
            DecisionBot bot = Bot();
            GivePersistentRecord(bot, true);
            FillBackpack(bot);
            DbInventoryItem valuable = bot.Inventory.GetItem(eInventorySlot.FirstBackpack);
            valuable.Name = "rare cloth trousers";
            valuable.Level = 20;
            valuable.Quality = 99;
            valuable.DPS_AF = 30;
            valuable.Price = 10_000;
            valuable.IsROG = true;

            AutonomousBotEconomy.ListingCandidate listing = AutonomousBotEconomy.FindValuableListingCandidate(bot);
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousBotEconomy.IsBackpackFull(bot), Is.True);
                Assert.That(listing, Is.Not.Null);
                Assert.That(listing.Item, Is.SameAs(valuable));
                Assert.That(AutonomousBotEconomy.FindVendorTrashCandidate(bot), Is.Not.SameAs(valuable));
                Assert.That(AutonomousBotEconomy.GetNeededService(bot), Is.EqualTo(eWorldServiceKind.RealmExchange),
                    "the best listing must be selected from all 40 items before any vendor clearing begins");
            });
        }

        [Test]
        public void VendorValueMatchesStandardMerchantAppraisalFormula()
        {
            DbInventoryItem stack = GameInventoryItem.Create(Item("bundled cloth", 10, 90,
                (int)eInventorySlot.FirstBackpack, 173));
            stack.Count = 13;
            stack.PackSize = 5;
            int originalRatio = Properties.ITEM_SELL_RATIO;
            try
            {
                Properties.ITEM_SELL_RATIO = 37;
                Assert.That(AutonomousBotEconomy.CalculateStandardVendorSaleCopper(stack), Is.EqualTo(166));
            }
            finally
            {
                Properties.ITEM_SELL_RATIO = originalRatio;
            }
        }

        [Test]
        public void PotentialEquipmentUpgradeIsProtectedFromListingAndVendorTrash()
        {
            DecisionBot bot = Bot();
            DbInventoryItem worn = GameInventoryItem.Create(Item("worn trousers", 5, 80,
                (int)eInventorySlot.LegsArmor, 100, 5));
            Assert.That(bot.Inventory.AddItem(eInventorySlot.LegsArmor, worn), Is.True);
            DbInventoryItem upgrade = AddBackpack(bot, Item("better trousers", 20, 99,
                (int)eInventorySlot.LegsArmor, 10_000, 30));
            FillBackpack(bot);

            Assert.Multiple(() =>
            {
                Assert.That(AutonomousBotEconomy.TryGetEquipmentUpgrade(bot, upgrade, out eInventorySlot slot), Is.True);
                Assert.That(slot, Is.EqualTo(eInventorySlot.LegsArmor));
                Assert.That(AutonomousBotEconomy.FindValuableListingCandidate(bot)?.Item, Is.Not.SameAs(upgrade));
                Assert.That(AutonomousBotEconomy.FindVendorTrashCandidate(bot), Is.Not.SameAs(upgrade));
            });
        }

        [Test]
        public void CandidateAppraisalIsDeterministicWhileListingRollVariesWithinAllowedRange()
        {
            DbInventoryItem item = GameInventoryItem.Create(Item("unusual cloth", 20, 98,
                (int)eInventorySlot.LegsArmor, 5_000, 24));
            item.IsROG = true;

            int appraisal = AutonomousBotEconomy.RecommendListingPrice(item);
            int[] rolls = Enumerable.Range(0, 96).Select(_ => AutonomousBotEconomy.RollListingPrice(item)).ToArray();
            double rarity = Math.Clamp((item.Quality - 85) / 3d + 2, 0, 10);
            var facts = new AutonomousAuctionValuation.ItemFacts(item.Level, Math.Max(1, item.Count), item.Quality,
                Math.Max(1, (int)item.ConditionPercent), rarity, 0, false);
            int minimum = (int)AutonomousAuctionValuation.Recommend(facts, -0.35).BuyoutCopper;
            int maximum = (int)AutonomousAuctionValuation.Recommend(facts, 0.50).BuyoutCopper;

            Assert.Multiple(() =>
            {
                Assert.That(AutonomousBotEconomy.RecommendListingPrice(item), Is.EqualTo(appraisal));
                Assert.That(rolls, Has.All.InRange(minimum, maximum));
                Assert.That(rolls.Distinct().Count(), Is.GreaterThan(1));
            });
        }

        [Test]
        public void ExchangeAppraisalFindsPlayerUpgradeBeyondFortyEightCheapUnusableListings()
        {
            DecisionBot bot = Bot();
            GivePersistentRecord(bot, true);
            for (int i = 0; i < 49; i++)
            {
                DbInventoryItem unusable = GameInventoryItem.Create(Item("cheap unusable " + i, 1, 80,
                    (int)eInventorySlot.FirstBackpack, 2));
                AddMarketItem(unusable, AutonomousBotEconomy.GetOwnerId(800000 + i),
                    (int)eInventorySlot.Consignment_First + i, 1);
            }

            DbInventoryItem playerUpgrade = GameInventoryItem.Create(Item("player's better trousers", 20, 99,
                (int)eInventorySlot.LegsArmor, 5_000, 30));
            AddMarketItem(playerUpgrade, "real-player-owner", (int)eInventorySlot.Consignment_First + 49, 500);

            AutonomousBotEconomy.PurchaseCandidate candidate = AutonomousBotEconomy.FindBestUsefulExchangePurchase(bot);
            Assert.Multiple(() =>
            {
                Assert.That(candidate, Is.Not.Null);
                Assert.That(candidate.Item, Is.SameAs(playerUpgrade));
                Assert.That(candidate.IsPlayerListing, Is.True);
            });
        }

        [Test]
        public void NeededServiceIsNullOutsideBetweenTaskInventoryPhase()
        {
            DecisionBot bot = Bot();
            GivePersistentRecord(bot, false);
            FillBackpack(bot);
            DbInventoryItem valuable = bot.Inventory.GetItem(eInventorySlot.FirstBackpack);
            valuable.Level = 20;
            valuable.Quality = 99;
            valuable.Price = 10_000;
            valuable.IsROG = true;

            Assert.That(AutonomousBotEconomy.GetNeededService(bot), Is.Null);
        }

        [Test]
        public void FullBotListingCapAllowsNonUpgradeValuableItemToBecomeVendorTrash()
        {
            DecisionBot bot = Bot();
            GivePersistentRecord(bot, true);
            for (int i = 0; i < 100; i++)
            {
                DbInventoryItem existing = GameInventoryItem.Create(Item("existing listing " + i, 1, 80,
                    (int)eInventorySlot.FirstBackpack, 2));
                AddMarketItem(existing, bot.InternalID, (int)eInventorySlot.Consignment_First + i, 1);
            }

            DbInventoryItem unwanted = AddBackpack(bot, Item("valuable but wrong-slot gear", 20, 99,
                (int)eInventorySlot.FirstBackpack, 10_000, 30));
            unwanted.IsROG = true;

            Assert.Multiple(() =>
            {
                Assert.That(AutonomousBotEconomy.FindFreeListingSlot(bot.InternalID), Is.EqualTo(-1));
                Assert.That(AutonomousBotEconomy.TryGetEquipmentUpgrade(bot, unwanted, out _), Is.False);
                Assert.That(AutonomousBotEconomy.FindVendorTrashCandidate(bot), Is.SameAs(unwanted));
            });
        }

        [Test]
        public void OccupiedListingSlotVendorsEverythingDisposableBeforeCheckingForUpgrade()
        {
            DecisionBot bot = Bot();
            GivePersistentRecord(bot, true);

            DbInventoryItem existingListing = GameInventoryItem.Create(Item("existing bot listing", 1, 80,
                (int)eInventorySlot.FirstBackpack, 2));
            AddMarketItem(existingListing, bot.InternalID, (int)eInventorySlot.Consignment_First, 100);

            DbInventoryItem playerUpgrade = GameInventoryItem.Create(Item("player market upgrade", 20, 99,
                (int)eInventorySlot.LegsArmor, 5_000, 30));
            AddMarketItem(playerUpgrade, "real-player-owner", (int)eInventorySlot.Consignment_First + 1, 500);
            FillBackpack(bot);

            Assert.That(AutonomousBotEconomy.FindFreeListingSlot(bot.InternalID), Is.EqualTo(-1),
                "one existing listing must consume the bot's complete listing allowance");
            Assert.That(AutonomousBotEconomy.GetNeededService(bot), Is.EqualTo(eWorldServiceKind.Vendor),
                "a full backpack must be cleared at a vendor before an exchange purchase");

            int removed = 0;
            for (DbInventoryItem trash = AutonomousBotEconomy.FindVendorTrashCandidate(bot);
                 trash != null;
                 trash = AutonomousBotEconomy.FindVendorTrashCandidate(bot))
            {
                Assert.That(bot.Inventory.RemoveItemWithoutDbDeletion(trash), Is.True);
                removed++;
            }
            Assert.That(removed, Is.GreaterThan(0));
            AutonomousBotEconomy.MarkInventoryChanged(bot);

            Assert.That(AutonomousBotEconomy.GetNeededService(bot), Is.EqualTo(eWorldServiceKind.RealmExchange),
                "after a slot is available the same inventory maintenance phase must appraise and route to the realm-local upgrade");
            Assert.That(AutonomousBotEconomy.FindBestUsefulExchangePurchase(bot)?.Item, Is.SameAs(playerUpgrade));
        }

        [Test]
        public void ExchangeAppraisalRejectsUsableItemListedAtAnotherRealmBroker()
        {
            DecisionBot bot = Bot();
            GivePersistentRecord(bot, true);
            DbInventoryItem neutralPlayerUpgrade = GameInventoryItem.Create(Item("neutral cross-realm trousers", 20, 99,
                (int)eInventorySlot.LegsArmor, 5_000, 30));
            neutralPlayerUpgrade.OwnerID = "real-albion-player";
            neutralPlayerUpgrade.OwnerLot = RealmExchangeBroker.AlbionOwnerLot;
            neutralPlayerUpgrade.SlotPosition = (int)eInventorySlot.Consignment_First;
            neutralPlayerUpgrade.SellPrice = 500;
            Assert.That(MarketCache.AddItem(neutralPlayerUpgrade), Is.True);
            _marketItems.Add(neutralPlayerUpgrade);

            Assert.That(AutonomousBotEconomy.FindBestUsefulExchangePurchase(bot), Is.Null);
        }
    }
}

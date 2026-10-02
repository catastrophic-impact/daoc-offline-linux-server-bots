using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerClass;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_RealmExchangeEconomy
    {
        private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<GameBot> _bots = new();
        private GameServer _previousServer;
        private MarketSearchEngine _previousMarket;
        private IObjectDatabase _database;
        private MemoryDatabase _store;

        private sealed class ExchangeBot : GameBot
        {
            private ExchangeBot() : base((OfflineWorldBotRecord)null) { }
            public bool AllowTestEquipment;
            public override byte Level { get; set; } = 10;
            public override int EffectiveLevel => Level;
            public override eRealm Realm { get; set; } = eRealm.Hibernia;
            public override ICharacterClass CharacterClass => new ClassEnchanter();
            public override bool HasAbilityToUseItem(DbItemTemplate item) =>
                AllowTestEquipment || base.HasAbilityToUseItem(item);
            public override void RefreshItemBonuses() { }
        }

        private sealed class ExchangePlayer : GamePlayer
        {
            private ExchangePlayer() : base(null, null) { }
            public GameClient TestClient;
            public IPacketLib TestOut;
            public override GameClient Client => TestClient;
            public override IPacketLib Out => TestOut;
        }

        private sealed class TestServer : GameServer
        {
            public IObjectDatabase TestDatabase;
            protected override IObjectDatabase DataBaseImpl => TestDatabase;
            protected override IServerRules ServerRulesImpl => new NormalServerRules();
        }

        public class MemoryDatabase : DispatchProxy
        {
            public DbCoreCharacter Character;
            public DbCoreCharacterXCustomParam Proceeds;
            public DbAccountXMoney Wallet;
            public int Writes;
            public bool FailNextSave;
            public DbInventoryItem ExistingInventory;
            public int DuplicateInventoryAdds;

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name is nameof(IObjectDatabase.AddObject) or nameof(IObjectDatabase.SaveObject))
                {
                    if (method.Name == nameof(IObjectDatabase.AddObject) && args[0] is DbInventoryItem adding &&
                        ExistingInventory != null && adding.ObjectId == ExistingInventory.ObjectId)
                    {
                        DuplicateInventoryAdds++;
                        return false;
                    }
                    Writes++;
                    if (method.Name == nameof(IObjectDatabase.SaveObject) && FailNextSave)
                    {
                        FailNextSave = false;
                        return false;
                    }
                    IEnumerable<DataObject> objects = args[0] switch
                    {
                        DataObject item => [item],
                        IEnumerable<DataObject> items => items,
                        _ => Array.Empty<DataObject>()
                    };
                    foreach (DataObject item in objects)
                    {
                        if (item is DbCoreCharacterXCustomParam proceeds)
                            Proceeds = proceeds;
                        if (item is DbAccountXMoney wallet)
                            Wallet = wallet;
                    }
                    return true;
                }

                if (method.Name == nameof(IObjectDatabase.SelectObject))
                {
                    Type type = method.GetGenericArguments()[0];
                    if (type == typeof(DbCoreCharacter)) return Character;
                    if (type == typeof(DbCoreCharacterXCustomParam)) return Proceeds;
                    if (type == typeof(DbAccountXMoney)) return Wallet;
                    if (type == typeof(DbInventoryItem)) return ExistingInventory;
                    return null;
                }

                if (method.Name is nameof(IObjectDatabase.SelectObjects) or nameof(IObjectDatabase.SelectAllObjects))
                {
                    Type itemType = method.GetGenericArguments()[0];
                    return Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
                }

                if (method.Name == nameof(IObjectDatabase.MultipleSelectObjects))
                {
                    Type itemType = method.GetGenericArguments()[0];
                    return Activator.CreateInstance(typeof(List<>).MakeGenericType(typeof(IList<>).MakeGenericType(itemType)));
                }

                if (method.ReturnType == typeof(bool)) return true;
                if (method.ReturnType == typeof(int)) return 0;
                return null;
            }
        }

        public class NoOpPacketLib : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args) => method.ReturnType != typeof(void) && method.ReturnType.IsValueType
                ? Activator.CreateInstance(method.ReturnType) : null;
        }

        [SetUp]
        public void SetUp()
        {
            _previousServer = GameServer.Instance;
            _database = DispatchProxy.Create<IObjectDatabase, MemoryDatabase>();
            _store = (MemoryDatabase)(object)_database;
            TestServer server = (TestServer)RuntimeHelpers.GetUninitializedObject(typeof(TestServer));
            server.TestDatabase = _database;
            GameServer.LoadTestDouble(server);

            Field(typeof(MarketCache), "_searchEngine", out _previousMarket);
            SetField(typeof(MarketCache), "_searchEngine", new MarketSearchEngine());
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameBot bot in _bots)
                AutonomousBotRegistry.Unregister(bot);
            _bots.Clear();

            MarketSearchEngine market;
            Field(typeof(MarketCache), "_searchEngine", out market);
            market.Dispose();
            SetField(typeof(MarketCache), "_searchEngine", _previousMarket);
            GameServer.LoadTestDouble(_previousServer);
        }

        [Test]
        public void BlockedFortySlotBackpack_IsClearedOnlyAfterMerchantArrival_WithoutTouchingEquipmentOrMoney()
        {
            ExchangeBot bot = Bot(9901, 12345);
            DbInventoryItem equipped = Item("worn gloves");
            Assert.That(bot.Inventory.AddItem(eInventorySlot.HandsArmor, equipped), Is.True);
            List<DbInventoryItem> backpack = FillBackpack(bot);
            foreach (DbInventoryItem item in backpack)
            {
                item.IsDropable = false;
                item.IsTradable = false;
            }
            // Musical bots keep spare instruments in the backpack while
            // twisting; a carried shield can be part of a weapon swap too.
            backpack[0].Object_Type = (int)eObjectType.Instrument;
            backpack[1].Object_Type = (int)eObjectType.Shield;

            Assert.That(AutonomousBotEconomy.TryClearBlockedBackpackAtMerchant(bot, false, out int premature), Is.False);
            Assert.That(premature, Is.Zero);
            Assert.That(AutonomousBotEconomy.IsBackpackFull(bot), Is.True);

            Assert.That(AutonomousBotEconomy.TryClearBlockedBackpackAtMerchant(bot, true, out int removed), Is.True);
            Assert.That(removed, Is.EqualTo(38));
            Assert.That(AutonomousBotEconomy.IsBackpackFull(bot), Is.False);
            Assert.That(bot.Inventory.GetItem(eInventorySlot.HandsArmor), Is.SameAs(equipped));
            Assert.That(bot.PersistentRecord.MoneyCopper, Is.EqualTo(12345));
            Assert.That(bot.Inventory.AllItems, Does.Contain(backpack[0]));
            Assert.That(bot.Inventory.AllItems, Does.Contain(backpack[1]));
            Assert.That(backpack.Skip(2).All(item => !bot.Inventory.AllItems.Contains(item)), Is.True);
        }

        [Test]
        public void BlockedBackpackRecovery_WaitsForOrdinarySaleAndNeverClearsPartlyFilledBag()
        {
            ExchangeBot bot = Bot(9902, 100);
            List<DbInventoryItem> backpack = FillBackpack(bot);
            foreach (DbInventoryItem item in backpack)
                item.IsDropable = false;
            DbInventoryItem sale = backpack[0];
            sale.IsDropable = true;
            sale.IsTradable = false;
            sale.Price = 1;
            sale.Item_Type = (int)eInventorySlot.FirstBackpack;
            sale.Object_Type = (int)eObjectType.GenericItem;

            Assert.That(AutonomousBotEconomy.FindVendorTrashCandidate(bot), Is.SameAs(sale));
            Assert.That(AutonomousBotEconomy.TryClearBlockedBackpackAtMerchant(bot, true, out int removed), Is.False);
            Assert.That(removed, Is.Zero);
            Assert.That(AutonomousBotEconomy.IsBackpackFull(bot), Is.True);

            Assert.That(bot.Inventory.RemoveItem(backpack[1]), Is.True);
            sale.IsDropable = false;
            Assert.That(AutonomousBotEconomy.TryClearBlockedBackpackAtMerchant(bot, true, out removed), Is.False);
            Assert.That(removed, Is.Zero);
            Assert.That(bot.Inventory.AllItems.Count(item => item.SlotPosition >= (int)eInventorySlot.FirstBackpack &&
                item.SlotPosition <= (int)eInventorySlot.LastBackpack), Is.EqualTo(39));
        }

        [Test]
        public void SpareInstrumentsAndShields_AreNotOfferedToVendorOrExchange()
        {
            ExchangeBot bot = Bot(9903, 100);
            DbInventoryItem instrument = Item("spare lute");
            instrument.Object_Type = (int)eObjectType.Instrument;
            instrument.Price = 1;
            DbInventoryItem shield = Item("spare shield");
            shield.Object_Type = (int)eObjectType.Shield;
            shield.Price = 1;
            Assert.That(bot.Inventory.AddItem(eInventorySlot.FirstBackpack, instrument), Is.True);
            Assert.That(bot.Inventory.AddItem((eInventorySlot)((int)eInventorySlot.FirstBackpack + 1), shield), Is.True);

            Assert.That(AutonomousBotEconomy.FindVendorTrashCandidate(bot), Is.Null);
            Assert.That(AutonomousBotEconomy.FindValuableListingCandidate(bot), Is.Null);
            Assert.That(bot.Inventory.AllItems, Does.Contain(instrument));
            Assert.That(bot.Inventory.AllItems, Does.Contain(shield));
        }

        [Test]
        public void FortyUsableFocusStaves_KeepOnlyBestSpare_AndLeaveSaleCandidates()
        {
            ExchangeBot bot = Bot(9904, 500);
            bot.AllowTestEquipment = true;
            DbInventoryItem worn = Staff("worn focus staff", 12);
            Assert.That(bot.Inventory.AddItem(eInventorySlot.TwoHandWeapon, worn), Is.True);
            List<DbInventoryItem> backpack = [];
            for (eInventorySlot slot = eInventorySlot.FirstBackpack; slot <= eInventorySlot.LastBackpack; slot++)
            {
                DbInventoryItem staff = Staff($"spare focus staff {slot}", slot == eInventorySlot.LastBackpack ? 9 : 3);
                staff.IsTradable = false;
                Assert.That(bot.Inventory.AddItem(slot, staff), Is.True);
                backpack.Add(staff);
            }

            DbInventoryItem bestSpare = backpack[^1];
            DbInventoryItem sale = AutonomousBotEconomy.FindVendorTrashCandidate(bot);
            Assert.That(sale, Is.Not.Null, "Thirty-nine obsolete staves must not be reserved as loadout gear");
            Assert.That(sale, Is.Not.SameAs(bestSpare));
            Assert.That(bot.Inventory.AllItems, Does.Contain(worn));

            foreach (DbInventoryItem staff in backpack)
            {
                staff.IsDropable = false;
                staff.IsTradable = false;
            }
            Assert.That(AutonomousBotEconomy.TryClearBlockedBackpackAtMerchant(bot, true, out int removed), Is.True);
            Assert.That(removed, Is.EqualTo(39));
            Assert.That(bot.Inventory.AllItems, Does.Contain(bestSpare));
            Assert.That(bot.Inventory.GetItem(eInventorySlot.TwoHandWeapon), Is.SameAs(worn));
            Assert.That(bot.PersistentRecord.MoneyCopper, Is.EqualTo(500));
        }

        [Test]
        public void BlockedBackpack_KeepsOneOfEachInstrumentKind_WithoutKeepingDuplicates()
        {
            ExchangeBot bot = Bot(9905, 700);
            List<DbInventoryItem> backpack = FillBackpack(bot);
            foreach (DbInventoryItem item in backpack)
            {
                item.IsDropable = false;
                item.IsTradable = false;
            }
            backpack[0].Object_Type = backpack[1].Object_Type = backpack[2].Object_Type =
                (int)eObjectType.Instrument;
            backpack[0].DPS_AF = backpack[1].DPS_AF = 1;
            backpack[2].DPS_AF = 2;
            backpack[1].Level = 12;

            Assert.That(AutonomousBotEconomy.TryClearBlockedBackpackAtMerchant(bot, true, out int removed), Is.True);
            Assert.That(removed, Is.EqualTo(38));
            Assert.That(bot.Inventory.AllItems, Does.Not.Contain(backpack[0]));
            Assert.That(bot.Inventory.AllItems, Does.Contain(backpack[1]));
            Assert.That(bot.Inventory.AllItems, Does.Contain(backpack[2]));
            Assert.That(bot.PersistentRecord.MoneyCopper, Is.EqualTo(700));
        }

        [Test]
        public void ListingRepairsStalePersistenceFlagWithoutDuplicateInsert()
        {
            ExchangeBot seller = Bot(901, 500);
            DbInventoryItem item = FillBackpack(seller).First();
            item.ObjectId = "already-saved-item";
            item.IsPersisted = false;
            _store.ExistingInventory = new DbInventoryItem
            {
                ObjectId = item.ObjectId, OwnerID = AutonomousBotEconomy.GetOwnerId(seller.DatabaseID),
                SlotPosition = item.SlotPosition, Template = item.Template, Count = item.Count,
                ITemplate_Id = item.ITemplate_Id, UTemplate_Id = item.UTemplate_Id,
                IsPersisted = true
            };
            Assert.That(AutonomousBotEconomy.TryList(seller, item, 700), Is.True);
            Assert.That(_store.DuplicateInventoryAdds, Is.Zero);
            Assert.That(item.IsPersisted, Is.True);
            Assert.That(AutonomousBotEconomy.TryList(seller, item, 700), Is.False);
        }

        [Test]
        public void StaleListingCannotOverwriteAnotherOwnersInventory()
        {
            ExchangeBot seller = Bot(902, 500);
            DbInventoryItem item = FillBackpack(seller).First();
            item.ObjectId = "belongs-to-somebody-else";
            _store.ExistingInventory = new DbInventoryItem
            {
                ObjectId = item.ObjectId, OwnerID = "player-owner", SlotPosition = item.SlotPosition,
                Template = item.Template, Count = item.Count, IsPersisted = true
            };
            Assert.That(AutonomousBotEconomy.TryList(seller, item, 700), Is.False);
            Assert.That(seller.Inventory.AllItems, Does.Contain(item));
            Assert.That(_store.Writes, Is.Zero);
        }

        [Test]
        public void FullBackpack_ListsExactRealItemAndFreesThatSlot()
        {
            ExchangeBot seller = Bot(101, 500);
            DbInventoryItem listed = FillBackpack(seller).First();
            eInventorySlot source = (eInventorySlot)listed.SlotPosition;

            Assert.That(AutonomousBotEconomy.TryList(seller, listed, 700), Is.True);

            Assert.That(seller.Inventory.GetItem(source), Is.Null);
            Assert.That(listed.OwnerID, Is.EqualTo(AutonomousBotEconomy.GetOwnerId(seller.DatabaseID)));
            Assert.That(listed.OwnerLot, Is.EqualTo(RealmExchangeBroker.HiberniaOwnerLot));
            Assert.That(listed.SellPrice, Is.EqualTo(700));
            Assert.That(MarketCache.SearchItems(new ItemQuery { Owner = listed.OwnerID }), Does.Contain(listed));
        }

        [Test]
        public void FullBackpack_CannotBuyAndDoesNotDebitOrRemoveListing()
        {
            ExchangeBot buyer = Bot(102, 900);
            FillBackpack(buyer);
            DbInventoryItem listing = Listing("offlinebot:103", 400, (int)eInventorySlot.Consignment_First);
            MarketCache.AddItem(listing);

            Assert.That(AutonomousBotEconomy.TryBuy(buyer, listing), Is.False);

            Assert.That(buyer.PersistentRecord.MoneyCopper, Is.EqualTo(900));
            Assert.That(listing.OwnerLot, Is.EqualTo(RealmExchangeBroker.HiberniaOwnerLot));
            Assert.That(listing.SellPrice, Is.EqualTo(400));
            Assert.That(MarketCache.SearchItems(new ItemQuery { Owner = listing.OwnerID }), Does.Contain(listing));
        }

        [Test]
        public void BotToBotPurchase_TransfersExactItemCreditsSellerAndCanEquipIt()
        {
            ExchangeBot buyer = Bot(104, 900);
            ExchangeBot seller = Bot(105, 100);
            DbInventoryItem listing = Listing(AutonomousBotEconomy.GetOwnerId(seller.DatabaseID), 400,
                (int)eInventorySlot.Consignment_First, eInventorySlot.HandsArmor);
            MarketCache.AddItem(listing);

            Assert.That(AutonomousBotEconomy.TryBuy(buyer, listing), Is.True);

            Assert.That(buyer.PersistentRecord.MoneyCopper, Is.EqualTo(500));
            Assert.That(seller.PersistentRecord.MoneyCopper, Is.EqualTo(500));
            Assert.That(buyer.Inventory.GetItem(eInventorySlot.FirstBackpack), Is.SameAs(listing));
            Assert.That(listing.OwnerID, Is.EqualTo(AutonomousBotEconomy.GetOwnerId(buyer.DatabaseID)));
            Assert.That(listing.OwnerLot, Is.Zero);
            Assert.That(listing.SellPrice, Is.Zero);

            AutonomousBotEconomy.PurchaseCandidate purchase = new(listing, 1, eInventorySlot.HandsArmor, true, false);
            Assert.That(AutonomousBotEconomy.TryEquipPurchasedUpgrade(buyer, purchase), Is.True);
            Assert.That(buyer.Inventory.GetItem(eInventorySlot.HandsArmor), Is.SameAs(listing));
        }

        [Test]
        public void BotPurchaseOfHumanListing_CreatesClaimableProceeds()
        {
            _store.Character = new DbCoreCharacter { ObjectId = "human-object", Realm = (int)eRealm.Hibernia };
            ExchangeBot buyer = Bot(103, 900);
            DbInventoryItem listing = Listing("human-character", 400, (int)eInventorySlot.Consignment_First);
            MarketCache.AddItem(listing);

            Assert.That(AutonomousBotEconomy.TryBuy(buyer, listing), Is.True);
            Assert.That(buyer.PersistentRecord.MoneyCopper, Is.EqualTo(500));
            Assert.That(InvokeBroker<long>("GetPendingPlayerProceeds", "human-character", eRealm.Hibernia), Is.EqualTo(400));
            Assert.That(_store.Proceeds.Value, Does.StartWith("400|0|"));
        }

        [Test]
        public void HumanClaim_UpdatesCharacterAndAccountWallet_ThenCannotClaimTwice()
        {
            ExchangePlayer player = Player("human-character", "account-one", eRealm.Hibernia, 100);
            _store.Character = PlayerCharacter(player);
            _store.Proceeds = new DbCoreCharacterXCustomParam(_store.Character.ObjectId, "RealmExchange.Proceeds.3", "400|0|0");
            _store.Wallet = Wallet(player.Client.Account.ObjectId, eRealm.Hibernia, 100);

            var claim = Claim(player, eRealm.Hibernia);

            Assert.That(claim.Success, Is.True);
            Assert.That(claim.Copper, Is.EqualTo(400));
            Assert.That(player.GetCurrentMoney(), Is.EqualTo(500));
            Assert.That(MoneyValue(_store.Character), Is.EqualTo(500));
            Assert.That(MoneyValue(_store.Wallet), Is.EqualTo(500));
            Assert.That(_store.Proceeds.Value, Is.EqualTo("0|0|0"));

            Assert.That(Claim(player, eRealm.Hibernia).Success, Is.False);
            Assert.That(player.GetCurrentMoney(), Is.EqualTo(500));
            Assert.That(MoneyValue(_store.Wallet), Is.EqualTo(500));
        }

        [Test]
        public void FailedHumanClaimSave_RestoresCharacterWalletAndPendingProceeds()
        {
            ExchangePlayer player = Player("human-character", "account-two", eRealm.Hibernia, 100);
            _store.Character = PlayerCharacter(player);
            _store.Proceeds = new DbCoreCharacterXCustomParam(_store.Character.ObjectId, "RealmExchange.Proceeds.3", "400|0|0");
            _store.Wallet = Wallet(player.Client.Account.ObjectId, eRealm.Hibernia, 100);
            _store.FailNextSave = true;

            Assert.That(Claim(player, eRealm.Hibernia).Success, Is.False);

            Assert.That(player.GetCurrentMoney(), Is.EqualTo(100));
            Assert.That(MoneyValue(_store.Character), Is.EqualTo(100));
            Assert.That(MoneyValue(_store.Wallet), Is.EqualTo(100));
            Assert.That(_store.Proceeds.Value, Is.EqualTo("400|0|0"));
        }

        [Test]
        public void HumanClaimAtWrongRealm_IsRejectedWithoutChangingMoney()
        {
            ExchangePlayer player = Player("human-character", "account-three", eRealm.Albion, 100);
            _store.Character = PlayerCharacter(player);
            Assert.That(player.Realm, Is.EqualTo(eRealm.Albion));
            _store.Proceeds = new DbCoreCharacterXCustomParam(_store.Character.ObjectId, "RealmExchange.Proceeds.3", "400|0|0");
            _store.Wallet = Wallet(player.Client.Account.ObjectId, eRealm.Albion, 100);

            Assert.That(Claim(player, eRealm.Hibernia).Success, Is.False);

            Assert.That(player.GetCurrentMoney(), Is.EqualTo(100));
            Assert.That(MoneyValue(_store.Wallet), Is.EqualTo(100));
            Assert.That(_store.Proceeds.Value, Is.EqualTo("400|0|0"));
        }

        [TestCase("self")]
        [TestCase("not-tradable")]
        [TestCase("missing-seller")]
        [TestCase("insufficient-money")]
        public void InvalidBotPurchase_IsRejectedWithoutChangingCoinOrListing(string reason)
        {
            ExchangeBot buyer = Bot(106, reason == "insufficient-money" ? 399 : 900);
            string sellerId = reason switch
            {
                "self" => AutonomousBotEconomy.GetOwnerId(buyer.DatabaseID),
                "missing-seller" => "offlinebot:999999",
                _ => "offlinebot:107"
            };
            DbInventoryItem listing = Listing(sellerId, 400, (int)eInventorySlot.Consignment_First);
            if (reason == "not-tradable") listing.IsTradable = false;
            MarketCache.AddItem(listing);

            Assert.That(AutonomousBotEconomy.TryBuy(buyer, listing), Is.False);

            Assert.That(buyer.PersistentRecord.MoneyCopper, Is.EqualTo(reason == "insufficient-money" ? 399 : 900));
            Assert.That(listing.OwnerID, Is.EqualTo(sellerId));
            Assert.That(listing.OwnerLot, Is.EqualTo(RealmExchangeBroker.HiberniaOwnerLot));
            Assert.That(listing.SellPrice, Is.EqualTo(400));
        }

        [Test]
        public void SecondPurchaseOfSameListing_IsRejected()
        {
            ExchangeBot buyer = Bot(108, 900);
            ExchangeBot seller = Bot(109, 100);
            DbInventoryItem listing = Listing(AutonomousBotEconomy.GetOwnerId(seller.DatabaseID), 400,
                (int)eInventorySlot.Consignment_First);
            MarketCache.AddItem(listing);

            Assert.That(AutonomousBotEconomy.TryBuy(buyer, listing), Is.True);
            Assert.That(AutonomousBotEconomy.TryBuy(buyer, listing), Is.False);
            Assert.That(buyer.PersistentRecord.MoneyCopper, Is.EqualTo(500));
            Assert.That(seller.PersistentRecord.MoneyCopper, Is.EqualTo(500));
        }

        [Test]
        public void SellerBalanceOverflow_RejectsPurchaseBeforeBuyerIsCharged()
        {
            ExchangeBot buyer = Bot(111, 900);
            ExchangeBot seller = Bot(112, long.MaxValue - 399);
            DbInventoryItem listing = Listing(AutonomousBotEconomy.GetOwnerId(seller.DatabaseID), 400,
                (int)eInventorySlot.Consignment_First);
            MarketCache.AddItem(listing);

            Assert.That(AutonomousBotEconomy.TryBuy(buyer, listing), Is.False);

            Assert.That(buyer.PersistentRecord.MoneyCopper, Is.EqualTo(900));
            Assert.That(seller.PersistentRecord.MoneyCopper, Is.EqualTo(long.MaxValue - 399));
            Assert.That(listing.OwnerID, Is.EqualTo(AutonomousBotEconomy.GetOwnerId(seller.DatabaseID)));
            Assert.That(listing.OwnerLot, Is.EqualTo(RealmExchangeBroker.HiberniaOwnerLot));
            Assert.That(listing.SellPrice, Is.EqualTo(400));
        }

        [Test]
        public void SaveFailure_RestoresListingAndBuyerPersistentRecordMoney()
        {
            ExchangeBot buyer = Bot(113, 900);
            ExchangeBot seller = Bot(114, 100);
            DbInventoryItem listing = Listing(AutonomousBotEconomy.GetOwnerId(seller.DatabaseID), 400,
                (int)eInventorySlot.Consignment_First);
            MarketCache.AddItem(listing);
            _store.FailNextSave = true;

            Assert.That(AutonomousBotEconomy.TryBuy(buyer, listing), Is.False);

            Assert.That(buyer.PersistentRecord.MoneyCopper, Is.EqualTo(900));
            Assert.That(seller.PersistentRecord.MoneyCopper, Is.EqualTo(100));
            Assert.That(buyer.Inventory.GetItem(eInventorySlot.FirstBackpack), Is.Null);
            Assert.That(listing.OwnerID, Is.EqualTo(AutonomousBotEconomy.GetOwnerId(seller.DatabaseID)));
            Assert.That(listing.OwnerLot, Is.EqualTo(RealmExchangeBroker.HiberniaOwnerLot));
            Assert.That(listing.SellPrice, Is.EqualTo(400));
            Assert.That(MarketCache.SearchItems(new ItemQuery { Owner = listing.OwnerID }), Does.Contain(listing));
        }

        [Test]
        public void ListingLimit_RejectsSecondWithoutLoss_ThenReusesFreedSlot()
        {
            ExchangeBot seller = Bot(110, 500);
            string owner = AutonomousBotEconomy.GetOwnerId(seller.DatabaseID);
            MarketCache.AddItem(Listing(owner, 100, (int)eInventorySlot.Consignment_First));

            DbInventoryItem candidate = Item("limit candidate");
            seller.Inventory.AddItem(eInventorySlot.FirstBackpack, candidate);
            Assert.That(AutonomousBotEconomy.TryList(seller, candidate, 200), Is.False);
            Assert.That(seller.Inventory.GetItem(eInventorySlot.FirstBackpack), Is.SameAs(candidate));

            DbInventoryItem sold = MarketCache.SearchItems(new ItemQuery { Owner = owner })
                .Single(item => item.SlotPosition == (int)eInventorySlot.Consignment_First);
            MarketCache.RemoveItem(sold);
            Assert.That(AutonomousBotEconomy.TryList(seller, candidate, 200), Is.True);
            Assert.That(candidate.SlotPosition, Is.EqualTo((int)eInventorySlot.Consignment_First));
        }

        private ExchangeBot Bot(long id, long copper)
        {
            ExchangeBot bot = (ExchangeBot)RuntimeHelpers.GetUninitializedObject(typeof(ExchangeBot));
            bot.DatabaseID = id;
            bot.InternalID = AutonomousBotEconomy.GetOwnerId(id);
            bot.Realm = eRealm.Hibernia;
            bot.Level = 10;
            bot.ObjectState = GameObject.eObjectState.Active;
            bot.Inventory = new BotInventory(bot.InternalID);
            SetProperty(typeof(GameBot), bot, nameof(GameBot.IsAutonomousWorldBot), true);
            SetProperty(typeof(GameBot), bot, nameof(GameBot.PersistentRecord), new OfflineWorldBotRecord
            {
                BotId = id,
                Realm = (int)eRealm.Hibernia,
                Level = 10,
                MoneyCopper = copper
            });
            Field(typeof(GameLiving), bot, "<TempProperties>k__BackingField", new PropertyCollection());
            Field(typeof(GameLiving), bot, "_abilitiesLock", new System.Threading.Lock());
            Field(typeof(GameLiving), bot, "m_abilities", new Dictionary<string, Ability>());
            Field(typeof(GameNPC), bot, "m_brains", new ArrayList());
            AutonomousBotRegistry.Register(bot);
            _bots.Add(bot);
            return bot;
        }

        private static ExchangePlayer Player(string characterId, string accountId, eRealm realm, long money)
        {
            ExchangePlayer player = (ExchangePlayer)RuntimeHelpers.GetUninitializedObject(typeof(ExchangePlayer));
            GameClient client = new((Socket)null)
            {
                Account = new DbAccount { ObjectId = accountId, PrivLevel = (uint)ePrivLevel.Player },
                Out = DispatchProxy.Create<IPacketLib, NoOpPacketLib>()
            };
            player.TestClient = client;
            player.TestOut = client.Out;
            player.InternalID = characterId;
            player.Realm = realm;
            Field(typeof(GamePlayer), player, "m_dbCharacter", Character(characterId + "-object", realm, 0));
            player.AddMoney(money);
            return player;
        }

        private static DbCoreCharacter Character(string objectId, eRealm realm, long money)
        {
            DbCoreCharacter character = new() { ObjectId = objectId, Realm = (int)realm };
            SetMoney(character, money);
            return character;
        }

        private static DbCoreCharacter PlayerCharacter(GamePlayer player) => (DbCoreCharacter)typeof(GamePlayer)
            .GetField("m_dbCharacter", PrivateInstance).GetValue(player);

        private static DbAccountXMoney Wallet(string accountId, eRealm realm, long money)
        {
            DbAccountXMoney wallet = new() { AccountId = accountId, Realm = (int)realm };
            SetMoney(wallet, money);
            return wallet;
        }

        private static (bool Success, long Copper) Claim(GamePlayer player, eRealm realm)
        {
            object[] arguments = [player, realm, 0L];
            bool success = InvokeBroker<bool>("TryClaimPlayerProceeds", arguments);
            return (success, (long)arguments[2]);
        }

        private static long MoneyValue(DbCoreCharacter character) =>
            Money.GetMoney(character.Mithril, character.Platinum, character.Gold, character.Silver, character.Copper);

        private static long MoneyValue(DbAccountXMoney wallet) =>
            Money.GetMoney(wallet.Mithril, wallet.Platinum, wallet.Gold, wallet.Silver, wallet.Copper);

        private static void SetMoney(DbCoreCharacter character, long money)
        {
            character.Mithril = Money.GetMithril(money);
            character.Platinum = Money.GetPlatinum(money);
            character.Gold = Money.GetGold(money);
            character.Silver = Money.GetSilver(money);
            character.Copper = Money.GetCopper(money);
        }

        private static void SetMoney(DbAccountXMoney wallet, long money)
        {
            wallet.Mithril = Money.GetMithril(money);
            wallet.Platinum = Money.GetPlatinum(money);
            wallet.Gold = Money.GetGold(money);
            wallet.Silver = Money.GetSilver(money);
            wallet.Copper = Money.GetCopper(money);
        }

        private static List<DbInventoryItem> FillBackpack(ExchangeBot bot)
        {
            List<DbInventoryItem> items = [];
            for (eInventorySlot slot = eInventorySlot.FirstBackpack; slot <= eInventorySlot.LastBackpack; slot++)
            {
                DbInventoryItem item = Item($"bag {slot}");
                Assert.That(bot.Inventory.AddItem(slot, item), Is.True);
                items.Add(item);
            }
            return items;
        }

        private static DbInventoryItem Listing(string owner, int price, int slot, eInventorySlot itemSlot = eInventorySlot.HandsArmor)
        {
            DbInventoryItem item = Item("exchange listing", itemSlot);
            item.OwnerID = owner;
            item.OwnerLot = RealmExchangeBroker.HiberniaOwnerLot;
            item.SlotPosition = slot;
            item.SellPrice = price;
            return item;
        }

        private static DbInventoryItem Item(string name, eInventorySlot itemSlot = eInventorySlot.HandsArmor)
        {
            DbItemTemplate template = new()
            {
                Id_nb = Guid.NewGuid().ToString(),
                Name = name,
                Realm = (int)eRealm.Hibernia,
                Level = 10,
                Quality = 100,
                Condition = 100,
                MaxCondition = 10000,
                MaxDurability = 10000,
                PackSize = 1,
                MaxCount = 1,
                Item_Type = (int)itemSlot,
                Object_Type = (int)eObjectType.Cloth,
                IsTradable = true,
                IsDropable = true
            };
            return GameInventoryItem.Create(template);
        }

        private static DbInventoryItem Staff(string name, int level)
        {
            DbInventoryItem item = Item(name, eInventorySlot.TwoHandWeapon);
            item.Object_Type = (int)eObjectType.Staff;
            item.Level = level;
            item.DPS_AF = 30;
            item.SPD_ABS = 40;
            item.Price = 1;
            return item;
        }

        private static void Field(Type type, object target, string name, object value) => type
            .GetField(name, PrivateInstance).SetValue(target, value);

        private static void Field<T>(Type type, string name, out T value) => value = (T)type
            .GetField(name, PrivateStatic).GetValue(null);

        private static void SetField(Type type, string name, object value) => type
            .GetField(name, PrivateStatic).SetValue(null, value);

        private static void SetProperty(Type type, object target, string name, object value) => type
            .GetProperty(name).SetValue(target, value);

        private static T InvokeBroker<T>(string method, params object[] arguments) => (T)typeof(RealmExchangeBroker)
            .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, arguments);
    }
}

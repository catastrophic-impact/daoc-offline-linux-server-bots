using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [NonParallelizable]
    public class UT_FrontierSupplyMerchants
    {
        private EpicTestServerScope _server;
        private int _previousSaleRatio;

        [SetUp]
        public void SetUp()
        {
            _server = new EpicTestServerScope();
            _previousSaleRatio = GS.ServerProperties.Properties.ITEM_SELL_RATIO;
            GS.ServerProperties.Properties.ITEM_SELL_RATIO = 50;
        }

        [TearDown]
        public void TearDown()
        {
            GS.ServerProperties.Properties.ITEM_SELL_RATIO = _previousSaleRatio;
            _server.Dispose();
        }

        [TestCase("SiegeMerchantAlb", "Siege Equipment")]
        [TestCase("SiegeMerchantMid", "Siege Equipment")]
        [TestCase("SiegeMerchantHib", "Siege Equipment")]
        [TestCase("OFMerchant_Alb", "Teleport Medallions")]
        [TestCase("OFMerchant_Mid", "Teleport Medallions")]
        [TestCase("OFMerchant_Hib", "Teleport Medallions")]
        [TestCase("OFMerchant_Alb_Home", "Teleport Medallions")]
        [TestCase("OFMerchant_Mid_Home", "Teleport Medallions")]
        [TestCase("OFMerchant_Hib_Home", "Teleport Medallions")]
        [TestCase("OFMerchant_Alb_HomeHib", "Teleport Medallions")]
        [TestCase("OFMerchant_Alb_HomeMid", "Teleport Medallions")]
        [TestCase("OFMerchant_Mid_HomeHib", "Teleport Medallions")]
        [TestCase("OFMerchant_Mid_HomeAlb", "Teleport Medallions")]
        [TestCase("OFMerchant_Hib_HomeMid", "Teleport Medallions")]
        [TestCase("OFMerchant_Hib_HomeAlb", "Teleport Medallions")]
        [TestCase("ordinary", null)]
        public void SupplyLabelsPreserveIdentityAndOtherFlags(string list, string expected)
        {
            var merchant = Merchant();
            merchant.Name = "Cameron";
            merchant.GuildName = "Original label";
            merchant.Flags = GameNPC.eFlags.DONTSHOWNAME | GameNPC.eFlags.PEACE;
            merchant.TradeItems = new MerchantTradeItems(list);
            FrontierSupplyMerchantPolicy.ApplyLabel(merchant);
            Assert.That(merchant.Name, Is.EqualTo("Cameron"));
            Assert.That(merchant.GuildName, Is.EqualTo(expected ?? "Original label"));
            Assert.That(merchant.Flags, Is.EqualTo(expected == null
                ? GameNPC.eFlags.DONTSHOWNAME | GameNPC.eFlags.PEACE : GameNPC.eFlags.PEACE));
        }

        [TestCase("deploy_heavy_siege_ram", 1000000)]
        [TestCase("deploy_siege_ram", 500000)]
        [TestCase("deploy_siege_ram2", 500000)]
        [TestCase("deploy_siege_ram3", 500000)]
        [TestCase("deploy_siege_trebuchet", 500000)]
        [TestCase("deploy_siege_trebuchet2", 500000)]
        [TestCase("deploy_siege_trebuchet3", 500000)]
        [TestCase("deploy_siege_ballista", 500000)]
        [TestCase("deploy_siege_ballista2", 500000)]
        [TestCase("deploy_siege_ballista3", 500000)]
        [TestCase("deploy_siege_catapult", 500000)]
        [TestCase("deploy_siege_catapult2", 500000)]
        [TestCase("deploy_siege_catapult3", 500000)]
        public void PaidSiegeKitUsesNormalAppraisalButRemainsProtectedFromBotTrashSale(string id, long price)
        {
            var item = Item(id, price, false);
            Assert.That(FrontierSupplyMerchantPolicy.CanSell(item), Is.True);
            Assert.That(Merchant().OnPlayerAppraise(null, item, true),
                Is.EqualTo(price / 2));
            GS.ServerProperties.Properties.ITEM_SELL_RATIO = 37;
            Assert.That(Merchant().OnPlayerAppraise(null, item, true), Is.EqualTo(price * 37 / 100));
            Assert.That(item.IsDropable, Is.False);
            Assert.That(AutonomousBotEconomy.CalculateStandardVendorSaleCopper(item), Is.Zero);
            item.IsTradable = false;
            Assert.That(FrontierSupplyMerchantPolicy.CanSell(item), Is.False);
        }

        [Test]
        public void UnrelatedNoDropItemsAndFreeSiegeKeepExistingProtection()
        {
            Assert.That(FrontierSupplyMerchantPolicy.CanSell(Item("quest_item", 500000, false)), Is.False);
            Assert.That(FrontierSupplyMerchantPolicy.CanSell(Item("deploy_siege_ram", 0, false)), Is.False);
            Assert.That(FrontierSupplyMerchantPolicy.CanSell(Item("deploy_siege_ram_fake", 500000, false)), Is.False);
            var normal = Item("normal", 1000, true);
            normal.Count = 6;
            normal.PackSize = 3;
            Assert.That(Merchant().OnPlayerAppraise(null, normal, true),
                Is.EqualTo(AutonomousBotEconomy.CalculateStandardVendorSaleCopper(normal)));
            Assert.That(FrontierSupplyMerchantPolicy.CanSell(null), Is.False);
        }

        private static DbInventoryItem Item(string id, long price, bool dropable)
        {
            return new DbInventoryItem
            {
                Template = new DbItemTemplate { Id_nb = id, Price = price, IsDropable = dropable, IsTradable = true },
                Count = 1
            };
        }

        private static GameMerchant Merchant()
        {
            // Exercise merchant labels/appraisal without starting regions,
            // initializing combat stats or creating live NPCs.
            var merchant = (GameMerchant)RuntimeHelpers.GetUninitializedObject(typeof(GameMerchant));
            merchant.ObjectState = GameObject.eObjectState.Inactive;
            return merchant;
        }

        [Test]
        public void WaitingOffsetsAreStableSpreadOutAndWithinRealInteractionRange()
        {
            var distinct = new HashSet<Vector3>();
            for (int id = 1; id <= 100; id++)
            for (int attempt = 0; attempt < 18; attempt++)
            {
                var point = AutonomousSupplyMerchantSpace.Offset(id, attempt, 256);
                Assert.That(AutonomousSupplyMerchantSpace.IsInteractionSpot(Vector3.Zero, point, 248), Is.True);
                Assert.That(point, Is.EqualTo(AutonomousSupplyMerchantSpace.Offset(id, attempt, 256)));
                distinct.Add(point);
            }
            Assert.That(distinct.Count, Is.GreaterThan(100));
            Assert.That(AutonomousSupplyMerchantSpace.IsInteractionSpot(Vector3.Zero, Vector3.Zero, 256), Is.False);
            Assert.That(AutonomousSupplyMerchantSpace.IsInteractionSpot(Vector3.Zero, new(160, 0, 300), 256), Is.False);
            Assert.That(AutonomousSupplyMerchantSpace.IsInteractionSpot(Vector3.Zero, new(float.NaN, 0, 0), 256), Is.False);
        }

        [Test, Explicit("Read-only check against all installed supply merchants and their navigation meshes")]
        public void InstalledMerchantsHaveConnectedClearInteractionSpotsAndAllStockCanResell()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string previous = Environment.CurrentDirectory;
            var loaded = new List<Zone>();
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                    (name, assembly, search) => name == "lib/Detour" ? NativeLibrary.Load(Path.Combine(root, "lib", "Detour.dll")) : IntPtr.Zero);
                Environment.CurrentDirectory = root;
                using var db = new SQLiteConnection($"Data Source={Path.GetFullPath("../data/opendaoc.sqlite3.db")};Read Only=True;Pooling=False;");
                db.Open();
                var regions = new Dictionary<int, Region>();
                foreach (int id in new[] { 1, 100, 200 })
                    regions[id] = UT_AuditedDungeonInstalledMesh.BuildRegion(db, id, loaded);
                var nav = PathfindingProvider.LocalPathfindingMgr;
                using var command = db.CreateCommand();
                command.CommandText = "select Name,ItemsListTemplateID,Realm,Region,X,Y,Z from Mob where ItemsListTemplateID like 'SiegeMerchant%' or ItemsListTemplateID like 'OFMerchant%'";
                using (var reader = command.ExecuteReader())
                {
                    int count = 0;
                    while (reader.Read())
                    {
                        string name = reader.GetString(0), list = reader.GetString(1);
                        Region region = regions[reader.GetInt32(3)];
                        var realm = (eRealm)reader.GetInt32(2);
                        Vector3 center = new(reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6));
                        Assert.That(FrontierSupplyMerchantPolicy.Label(list), Is.Not.Null, name);
                        var points = new HashSet<Vector3>();
                        for (int botId = 1; botId <= 20; botId++)
                        {
                            bool ok = AutonomousSupplyMerchantSpace.TryResolve(nav, region, realm, center, center, botId, 256, out var point);
                            Assert.That(ok, Is.True, $"{name}/{list} realm={realm} region={region.ID} center={center} bot={botId}");
                            Assert.That(AutonomousSupplyMerchantSpace.IsInteractionSpot(center, point, 256), Is.True);
                            points.Add(point);
                            // A bot reaching the vicinity from a different clear slot
                            // must also be able to use its own interaction point.
                            Assert.That(AutonomousSupplyMerchantSpace.TryResolve(nav, region, realm, point, center,
                                botId + 1000, 256, out _), Is.True, name);
                        }
                        TestContext.WriteLine($"{name}/{list} region={region.ID}: 40 connected approaches passed; {points.Count} distinct slots");
                        count++;
                    }
                    Assert.That(count, Is.EqualTo(15));
                }
                command.CommandText = "select distinct t.Id_nb,t.Price,t.IsDropable,t.IsTradable from MerchantItem m join ItemTemplate t on t.Id_nb=m.ItemTemplateID where m.ItemListID like 'SiegeMerchant%'";
                using var stock = command.ExecuteReader();
                while (stock.Read())
                {
                    var item = Item(stock.GetString(0), stock.GetInt64(1), stock.GetInt32(2) != 0);
                    item.IsTradable = stock.GetInt32(3) != 0;
                    Assert.That(FrontierSupplyMerchantPolicy.CanSell(item), Is.True, item.Id_nb);
                    Assert.That(Merchant().OnPlayerAppraise(null, item, true), Is.EqualTo(item.Price / 2), item.Id_nb);
                    TestContext.WriteLine($"Stock {item.Id_nb}: price={item.Price}; resale={Merchant().OnPlayerAppraise(null, item, true)}");
                }
            }
            finally
            {
                foreach (var zone in loaded) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
        }
    }
}

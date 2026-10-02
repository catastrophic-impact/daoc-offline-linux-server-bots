using System;
using System.IO;
using System.Linq;
using DOL.Database;
using DOL.Database.Handlers;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable]
public sealed class UT_RealmExchangeSaleLedger
{
    private string _path;
    private SqliteObjectDatabase _database;
    private DbSinglePermission _payment;

    [SetUp]
    public void SetUp()
    {
        _path = Path.Combine(Path.GetTempPath(), "daoc-sale-ledger-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        _database = Open();
        _payment = new DbSinglePermission { PlayerID = "ledger-test", Command = "before" };
        Assert.That(_database.AddObject(_payment), Is.True);
    }

    private SqliteObjectDatabase Open()
    {
        var database = new SqliteObjectDatabase($"Data Source={_path};Version=3;Pooling=False;");
        database.RegisterDataObject(typeof(RealmExchangeSaleRecord));
        database.RegisterDataObject(typeof(DbSinglePermission));
        return database;
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static RealmExchangeSaleLedger.Sale Sale(int number = 1, string seller = "player-one", string buyer = "offlinebot:2") =>
        new(2, "Fortifying Cloth Gloves " + number, 1, 100, seller, "Seller", buyer, "Buyer", DateTime.UtcNow);

    [TestCase("player-one", "offlinebot:2")]
    [TestCase("offlinebot:1", "offlinebot:2")]
    [TestCase("offlinebot:1", "player-two")]
    [TestCase("player-one", "player-two")]
    public void CompletedPurchaseSnapshotsNamesRealmAndExactPrice(string seller, string buyer)
    {
        _payment.Command = "paid";
        Assert.That(RealmExchangeSaleLedger.CommitSale(_database, Sale(1, seller, buyer), _payment), Is.True);
        var fresh = Open();
        var rows = fresh.SelectAllObjects<RealmExchangeSaleRecord>();
        Assert.That(rows.Count, Is.EqualTo(50));
        var sold = rows.Single(row => row.SaleSequence > 0);
        Assert.Multiple(() =>
        {
            Assert.That(sold.Realm, Is.EqualTo(2));
            Assert.That(sold.ItemName, Is.EqualTo("Fortifying Cloth Gloves 1"));
            Assert.That(sold.PriceCopper, Is.EqualTo(100));
            Assert.That(sold.SellerId, Is.EqualTo(seller));
            Assert.That(sold.BuyerId, Is.EqualTo(buyer));
            Assert.That(sold.SellerName, Is.EqualTo("Seller"));
            Assert.That(sold.BuyerName, Is.EqualTo("Buyer"));
            Assert.That(fresh.SelectAllObjects<DbSinglePermission>().Single().Command, Is.EqualTo("paid"));
        });
    }

    [Test]
    public void KeepsOnlyLatestFiftyAndContinuesAfterReload()
    {
        for (int i = 1; i <= 53; i++)
            Assert.That(RealmExchangeSaleLedger.CommitSale(_database, Sale(i), _payment), Is.True);
        var fresh = Open();
        Assert.That(RealmExchangeSaleLedger.CommitSale(fresh, Sale(54), fresh.SelectAllObjects<DbSinglePermission>().Single()), Is.True);
        var rows = Open().SelectAllObjects<RealmExchangeSaleRecord>().OrderByDescending(row => row.SaleSequence).ToArray();
        Assert.That(rows.Length, Is.EqualTo(50));
        Assert.That(rows[0].ItemName, Is.EqualTo("Fortifying Cloth Gloves 54"));
        Assert.That(rows[^1].ItemName, Is.EqualTo("Fortifying Cloth Gloves 5"));
        Assert.That(rows.Select(row => row.SaleSequence), Is.EqualTo(Enumerable.Range(5, 50).Reverse().Select(value => (long)value)));
    }

    [Test]
    public void FailedCommitPreservesPreviousHistoryAndOtherPaymentRows()
    {
        for (int i = 1; i <= 50; i++)
            Assert.That(RealmExchangeSaleLedger.CommitSale(_database, Sale(i), _payment), Is.True);
        var missing = new DbSinglePermission { PlayerID = "deleted", Command = "before" };
        Assert.That(_database.AddObject(missing), Is.True);
        var fresh = Open();
        Assert.That(fresh.DeleteObject(fresh.SelectAllObjects<DbSinglePermission>().Single(row => row.PlayerID == "deleted")), Is.True);
        _payment.Command = "must-rollback";
        Assert.That(RealmExchangeSaleLedger.CommitSale(_database, Sale(51), _payment, missing), Is.False);
        var afterFailure = Open();
        Assert.That(afterFailure.SelectAllObjects<DbSinglePermission>().Single().Command, Is.EqualTo("before"));
        Assert.That(afterFailure.SelectAllObjects<RealmExchangeSaleRecord>().Min(row => row.SaleSequence), Is.EqualTo(1));
        Assert.That(afterFailure.SelectAllObjects<RealmExchangeSaleRecord>().Max(row => row.SaleSequence), Is.EqualTo(50));
        _payment.Command = "paid";
        Assert.That(RealmExchangeSaleLedger.CommitSale(_database, Sale(52), _payment), Is.True);
        Assert.That(Open().SelectAllObjects<RealmExchangeSaleRecord>().Single(row => row.SaleSequence == 51).ItemName,
            Is.EqualTo("Fortifying Cloth Gloves 52"));
    }

    [Test]
    public void InvalidSaleDoesNotWriteHistoryOrPayment()
    {
        _payment.Command = "must-not-write";
        Assert.That(RealmExchangeSaleLedger.CommitSale(_database, Sale() with { PriceCopper = 0 }, _payment), Is.False);
        Assert.That(RealmExchangeSaleLedger.CommitSale(_database, Sale() with { Realm = 0 }, _payment), Is.False);
        Assert.That(RealmExchangeSaleLedger.CommitSale(_database, Sale() with { BuyerId = "player-one" }, _payment), Is.False);
        var fresh = Open();
        Assert.That(fresh.SelectAllObjects<RealmExchangeSaleRecord>(), Is.Empty);
        Assert.That(fresh.SelectAllObjects<DbSinglePermission>().Single().Command, Is.EqualTo("before"));
    }
}

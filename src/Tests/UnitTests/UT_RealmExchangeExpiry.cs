using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, NonParallelizable]
public sealed class UT_RealmExchangeExpiry
{
    private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private GameServer _previousServer;
    private MarketSearchEngine _previousMarket;
    private IObjectDatabase _database;
    private DeleteDatabase _store;

    private sealed class InertServer : GameServer
    {
        public IObjectDatabase TestDatabase;
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl => TestDatabase;
    }

    public class DeleteDatabase : DispatchProxy
    {
        public bool DeleteSucceeds = true;
        public readonly List<DbInventoryItem> Deleted = [];

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(IObjectDatabase.DeleteObject))
            {
                if (args[0] is DbInventoryItem item)
                    Deleted.Add(item);
                return DeleteSucceeds;
            }

            if (method.ReturnType == typeof(bool))
                return true;
            if (method.ReturnType == typeof(int))
                return 0;
            return null;
        }
    }

    [SetUp]
    public void SetUp()
    {
        _previousServer = GameServer.Instance;
        _database = DispatchProxy.Create<IObjectDatabase, DeleteDatabase>();
        _store = (DeleteDatabase)(object)_database;
        InertServer server = (InertServer)RuntimeHelpers.GetUninitializedObject(typeof(InertServer));
        server.TestDatabase = _database;
        GameServer.LoadTestDouble(server);

        _previousMarket = GetMarketEngine();
        SetMarketEngine(new MarketSearchEngine());
    }

    [TearDown]
    public void TearDown()
    {
        MarketSearchEngine testMarket = GetMarketEngine();
        testMarket.Dispose();
        SetMarketEngine(_previousMarket);
        GameServer.LoadTestDouble(_previousServer);
    }

    [Test]
    public void BotListing_ExpiresExactlyTwentyFourHoursAfterListing()
    {
        DateTime listedUtc = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        DbInventoryItem item = BotListing(listedUtc.ToString("O"));
        DateTime expiry = listedUtc.AddHours(24);

        Assert.That(RealmExchangeExpiry.ExpiresUtc(item), Is.EqualTo(expiry));
        Assert.That(RealmExchangeExpiry.IsExpired(item, expiry.AddTicks(-1)), Is.False);
        Assert.That(RealmExchangeExpiry.IsExpired(item, expiry), Is.True);
        Assert.That(RealmExchangeExpiry.Describe(item, listedUtc), Does.Contain("expires in 24h"));
    }

    [Test]
    public void HumanListing_NeverExpiresEvenWhenItsTimestampLooksLikeABotListing()
    {
        DbInventoryItem item = new()
        {
            OwnerLot = RealmExchangeBroker.HiberniaOwnerLot,
            OwnerID = "human-character-id",
            RealmExchangeListedUtc = DateTime.UtcNow.AddDays(-365).ToString("O"),
        };

        Assert.That(RealmExchangeExpiry.ExpiresUtc(item), Is.Null);
        Assert.That(RealmExchangeExpiry.IsExpired(item, DateTime.UtcNow), Is.False);
        Assert.That(RealmExchangeExpiry.Describe(item, DateTime.UtcNow), Is.EqualTo("Player listing: no expiration."));
    }

    [Test]
    public void BotListing_WithInvalidTimestampDoesNotExpire()
    {
        DbInventoryItem item = BotListing("not-a-date");

        Assert.That(RealmExchangeExpiry.ExpiresUtc(item), Is.Null);
        Assert.That(RealmExchangeExpiry.IsExpired(item, DateTime.UtcNow.AddYears(10)), Is.False);
        Assert.That(RealmExchangeExpiry.Describe(item, DateTime.UtcNow), Is.EqualTo("Player listing: no expiration."));
    }

    [Test]
    public void ExpireDue_DeletesOnlyDueBotsAndFreesTheirMarketIndexIdempotently()
    {
        DateTime now = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        DbInventoryItem dueBot = BotListing(now.AddHours(-24).ToString("O"));
        dueBot.OwnerID = AutonomousBotEconomy.GetOwnerId(101);
        DbInventoryItem youngerBot = BotListing(now.AddHours(-23).ToString("O"));
        youngerBot.OwnerID = AutonomousBotEconomy.GetOwnerId(102);
        DbInventoryItem human = new()
        {
            OwnerLot = RealmExchangeBroker.HiberniaOwnerLot,
            OwnerID = "human-owner",
            RealmExchangeListedUtc = now.AddDays(-30).ToString("O"),
        };
        Add(dueBot);
        Add(youngerBot);
        Add(human);

        Assert.That(RealmExchangeExpiry.ExpireDue(now), Is.EqualTo(1));
        Assert.That(_store.Deleted, Is.EqualTo(new[] { dueBot }));
        Assert.That(MarketCache.SearchItems(new ItemQuery { Owner = dueBot.OwnerID }), Is.Empty);
        Assert.That(MarketCache.SearchItems(new ItemQuery { Owner = youngerBot.OwnerID }), Does.Contain(youngerBot));
        Assert.That(MarketCache.SearchItems(new ItemQuery { Owner = human.OwnerID }), Does.Contain(human));
        Assert.That(RealmExchangeExpiry.ExpireDue(now), Is.Zero);
        Assert.That(_store.Deleted, Has.Count.EqualTo(1));
    }

    [Test]
    public void ExpireDue_WhenDatabaseDeleteFails_PreservesTheDueListingInCache()
    {
        DateTime now = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        DbInventoryItem dueBot = BotListing(now.AddHours(-24).ToString("O"));
        _store.DeleteSucceeds = false;
        Add(dueBot);

        Assert.That(RealmExchangeExpiry.ExpireDue(now), Is.Zero);
        Assert.That(_store.Deleted, Is.EqualTo(new[] { dueBot }));
        Assert.That(MarketCache.SearchItems(new ItemQuery { Owner = dueBot.OwnerID }), Does.Contain(dueBot));
    }

    private static DbInventoryItem BotListing(string listedUtc) => new()
    {
        OwnerLot = RealmExchangeBroker.HiberniaOwnerLot,
        OwnerID = AutonomousBotEconomy.GetOwnerId(42),
        RealmExchangeListedUtc = listedUtc,
    };

    private static void Add(DbInventoryItem item)
    {
        item.Template = new DbItemTemplate { Id_nb = Guid.NewGuid().ToString(), Name = "exchange expiry test", Item_Type = 25, Object_Type = (int)eObjectType.Cloth };
        Assert.That(MarketCache.AddItem(item), Is.True);
    }

    private static MarketSearchEngine GetMarketEngine() => (MarketSearchEngine)typeof(MarketCache)
        .GetField("_searchEngine", PrivateStatic)!
        .GetValue(null)!;

    private static void SetMarketEngine(MarketSearchEngine engine) => typeof(MarketCache)
        .GetField("_searchEngine", PrivateStatic)!
        .SetValue(null, engine);
}

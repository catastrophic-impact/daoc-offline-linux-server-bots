using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS.ServerRules;
using DOL.UnitTests;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, NonParallelizable]
public sealed class UT_RealmExchangeInterface
{
    private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, EmptyReadDatabase>();
    private GameServer _previousServer;
    private MarketSearchEngine _previousMarket;
    private PetTestLanguageScope _languageScope;

    private sealed class InertServer : GameServer
    {
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
    }

    public class EmptyReadDatabase : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.ReturnType == typeof(bool))
                return true;
            if (method.ReturnType.IsGenericType && typeof(IEnumerable).IsAssignableFrom(method.ReturnType))
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(method.ReturnType.GetGenericArguments()[0]));
            return null;
        }
    }

    [SetUp]
    public void SetUp()
    {
        _previousServer = GameServer.Instance;
        GameServer.LoadTestDouble((InertServer)RuntimeHelpers.GetUninitializedObject(typeof(InertServer)));
        _languageScope = new PetTestLanguageScope();
        _previousMarket = GetMarketEngine();
        SetMarketEngine(new MarketSearchEngine());
    }

    [TearDown]
    public void TearDown()
    {
        MarketSearchEngine testMarket = GetMarketEngine();
        testMarket.Dispose();
        SetMarketEngine(_previousMarket);
        _languageScope?.Dispose();
        GameServer.LoadTestDouble(_previousServer);
    }

    [Test]
    public void BuildGreeting_ContainsTheNativeNpcSpeechLinks()
    {
        string greeting = RealmExchangeBroker.BuildGreeting(12_345);

        Assert.That(greeting, Does.Contain("[Browse the exchange]"));
        Assert.That(greeting, Does.Contain("[Manage my listings]"));
        Assert.That(greeting, Does.Contain("[Claim sale proceeds]"));
        Assert.That(greeting, Does.Contain("[How does this work?]"));
    }

    [TestCase(1, eObjectType.Cloth)]
    [TestCase(2, eObjectType.Leather)]
    public void FindExchangeItems_AppliesNativeFiltersToNeutralRealmItemsWithinItsBroker(
        byte armorType,
        eObjectType objectType)
    {
        DbInventoryItem expected = Listing("Azure twilight armor", RealmExchangeBroker.HiberniaOwnerLot, objectType, 15);
        expected.Bonus1Type = (int)eProperty.Dexterity;
        expected.Bonus1 = 10;
        Add(expected);

        Add(Listing("Azure wrong armor", RealmExchangeBroker.HiberniaOwnerLot,
            objectType == eObjectType.Cloth ? eObjectType.Leather : eObjectType.Cloth, 15));
        Add(Listing("Azure too high", RealmExchangeBroker.HiberniaOwnerLot, objectType, 21));
        DbInventoryItem otherBroker = Listing("Azure another broker", RealmExchangeBroker.AlbionOwnerLot, objectType, 15);
        otherBroker.Bonus1Type = (int)eProperty.Dexterity;
        otherBroker.Bonus1 = 10;
        Add(otherBroker);

        MarketSearch.SearchData search = new()
        {
            realm = eRealm.Albion,
            name = "aZuRe",
            armorType = armorType,
            bonus1 = 1,
            bonus1Value = 2,
            levelMin = 10,
            levelMax = 20,
        };

        List<DbInventoryItem> results = new MarketSearch(null)
            .FindExchangeItems(search, RealmExchangeBroker.HiberniaOwnerLot);

        Assert.That(results, Is.EqualTo(new[] { expected }),
            "Exchange browsing must be broker-lot scoped while retaining neutral-realm items without the housing realm index.");
    }

    [Test]
    public void FindExchangeItems_ReturnsAllMatchesBeyondHousingCapInStableOrder()
    {
        for (int index = 0; index < 301; index++)
            Add(Listing($"catalog {300 - index:D3}", RealmExchangeBroker.HiberniaOwnerLot, eObjectType.Cloth, 10));

        MarketSearch.SearchData search = new() { name = "CATALOG", levelMax = 50 };
        MarketSearch marketSearch = new(null);

        List<DbInventoryItem> first = marketSearch.FindExchangeItems(search, RealmExchangeBroker.HiberniaOwnerLot);
        List<DbInventoryItem> second = marketSearch.FindExchangeItems(search, RealmExchangeBroker.HiberniaOwnerLot);

        Assert.That(first, Has.Count.EqualTo(301));
        Assert.That(first.Select(item => item.Name), Is.Ordered.Using<string>(StringComparer.OrdinalIgnoreCase));
        Assert.That(second.Select(item => item.Name), Is.EqualTo(first.Select(item => item.Name)));
    }

    [Test]
    public void ExchangeOwnerLots_AreIndexedToTheirRealmForMarketCandidates()
    {
        Assert.That(MarketSearchEngine.GetRealmOfLot(RealmExchangeBroker.AlbionOwnerLot), Is.EqualTo(eRealm.Albion));
        Assert.That(MarketSearchEngine.GetRealmOfLot(RealmExchangeBroker.MidgardOwnerLot), Is.EqualTo(eRealm.Midgard));
        Assert.That(MarketSearchEngine.GetRealmOfLot(RealmExchangeBroker.HiberniaOwnerLot), Is.EqualTo(eRealm.Hibernia));
    }

    [Test]
    public void GetClientInventory_OnSecondHumanListingPageMapsTheSecondHundredSlots()
    {
        RealmExchangeBroker broker = (RealmExchangeBroker)RuntimeHelpers.GetUninitializedObject(typeof(RealmExchangeBroker));
        typeof(GameNPC).GetField("m_brains", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(broker, new ArrayList());
        broker.Realm = eRealm.Hibernia;
        GamePlayer player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        player.InternalID = "human-page-test";
        SetTempProperties(player);
        player.TempProperties.SetProperty("OfflineRealmExchange.ListingsPage", 1);

        for (int slot = (int)eInventorySlot.Consignment_First; slot <= (int)eInventorySlot.Consignment_Last + 100; slot++)
        {
            DbInventoryItem item = Listing($"human listing {slot}", RealmExchangeBroker.HiberniaOwnerLot, eObjectType.Cloth, 10);
            item.OwnerID = player.InternalID;
            item.SlotPosition = slot;
            Add(item);
        }

        Dictionary<int, DbInventoryItem> page = broker.GetClientInventory(player);

        Assert.That(page, Has.Count.EqualTo(100));
        Assert.That(page.Keys, Is.EquivalentTo(Enumerable.Range((int)eInventorySlot.HousingInventory_First, 100)));
        Assert.That(page[(int)eInventorySlot.HousingInventory_First].SlotPosition,
            Is.EqualTo((int)eInventorySlot.Consignment_First + 100));
        Assert.That(page[(int)eInventorySlot.HousingInventory_Last].SlotPosition,
            Is.EqualTo((int)eInventorySlot.Consignment_Last + 100));
    }

    private static DbInventoryItem Listing(string name, ushort ownerLot, eObjectType objectType, int level)
    {
        DbItemTemplate template = new()
        {
            Id_nb = Guid.NewGuid().ToString(),
            Name = name,
            Realm = (int)eRealm.None,
            Level = level,
            Quality = 100,
            Condition = 100,
            MaxCondition = 10_000,
            MaxDurability = 10_000,
            PackSize = 1,
            MaxCount = 1,
            Item_Type = (int)eInventorySlot.TorsoArmor,
            Object_Type = (int)objectType,
            IsTradable = true,
            IsDropable = true,
        };

        DbInventoryItem item = GameInventoryItem.Create(template);
        item.OwnerLot = ownerLot;
        item.OwnerID = $"seller-{Guid.NewGuid():N}";
        item.SlotPosition = (int)eInventorySlot.Consignment_First;
        item.SellPrice = 100;
        return item;
    }

    private static void Add(DbInventoryItem item) => Assert.That(MarketCache.AddItem(item), Is.True);

    private static MarketSearchEngine GetMarketEngine() => (MarketSearchEngine)typeof(MarketCache)
        .GetField("_searchEngine", PrivateStatic)!
        .GetValue(null)!;

    private static void SetMarketEngine(MarketSearchEngine engine) => typeof(MarketCache)
        .GetField("_searchEngine", PrivateStatic)!
        .SetValue(null, engine);

    private static void SetTempProperties(GamePlayer player) => typeof(GameLiving)
        .GetField("<TempProperties>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(player, new PropertyCollection());
}

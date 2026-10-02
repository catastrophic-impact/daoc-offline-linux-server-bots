using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DOL.Database;
using DOL.GS.PacketHandler;

namespace DOL.GS;

/// <summary>
/// A capital-city broker backed by real persisted inventory items and character coin.
/// The stock market window is used for browsing; the consignment window is used for
/// placing and pricing the character's own items.
/// </summary>
[NPCGuildScript("Realm Exchange")]
public sealed partial class RealmExchangeBroker : GameNPC, IGameInventoryObject
{
    public const ushort AlbionOwnerLot = 65001;
    public const ushort MidgardOwnerLot = 65002;
    public const ushort HiberniaOwnerLot = 65003;

    private const string ModeProperty = "OfflineRealmExchange.Mode";
    private const string BrowseListProperty = "OfflineRealmExchange.BrowseItems";
    private const string ModeBrowse = "browse";
    private const string ModeSell = "sell";
    private const string ListingsPageProperty = "OfflineRealmExchange.ListingsPage";
    private const string ProceedsKeyPrefix = "RealmExchange.Proceeds.";
    private const int PageSize = 20;

    internal static readonly Lock TransactionLock = new();
    private readonly Lock _inventoryLock = new();
    private readonly Dictionary<string, GamePlayer> _observers = new(StringComparer.OrdinalIgnoreCase);

    public Lock Lock => _inventoryLock;
    public eInventorySlot FirstClientSlot => eInventorySlot.HousingInventory_First;
    public eInventorySlot LastClientSlot => eInventorySlot.HousingInventory_Last;
    public int FirstDbSlot => (int)eInventorySlot.Consignment_First;
    public int LastDbSlot => (int)eInventorySlot.Consignment_Last;

    private ushort ExchangeOwnerLot => GetOwnerLot(Realm);

    internal static ushort GetOwnerLot(eRealm realm) => realm switch
    {
        eRealm.Albion => AlbionOwnerLot,
        eRealm.Midgard => MidgardOwnerLot,
        eRealm.Hibernia => HiberniaOwnerLot,
        _ => 0,
    };

    public override bool Interact(GamePlayer player)
    {
        if (!base.Interact(player))
            return false;

        TurnTo(player, 5000);
        if (player.Realm != Realm)
            return false;
        player.TargetObject = this;
        long pendingProceeds = GetPendingPlayerProceeds(player.InternalID, Realm);
        // DetailWindow text has no clickable NPC links. The native NPC speech
        // popup routes bracketed choices back through WhisperReceive.
        player.Out.SendMessage(BuildGreeting(pendingProceeds), eChatType.CT_Say, eChatLoc.CL_PopupWindow);
        return true;
    }

    public static string BuildGreeting(long pendingProceeds) =>
        "Welcome to the Realm Exchange. These are real items sold by inhabitants of your realm.\n\n" +
        "[Browse the exchange]\n[Manage my listings]\n" +
        (pendingProceeds > 0 ? $"[Claim sale proceeds]: {Money.GetString(pendingProceeds)}\n" : "Sale proceeds: none waiting\n") +
        "[How does this work?]";

    public override bool WhisperReceive(GameLiving source, string text)
    {
        if (!base.WhisperReceive(source, text) || source is not GamePlayer player || !CanUseExchange(player))
            return false;

        string command = text.Trim().ToLowerInvariant();
        if (command is "next listing page" or "previous listing page")
        {
            int page = player.TempProperties.GetProperty<int>(ListingsPageProperty);
            int lastUsed = GetDbItems(player).Select(item => item.SlotPosition).DefaultIfEmpty(FirstDbSlot).Max();
            int maximumPage = Math.Max(0, (lastUsed - FirstDbSlot) / 100 + 1);
            page = Math.Clamp(page + (command.StartsWith("next") ? 1 : -1), 0, maximumPage);
            player.TempProperties.SetProperty(ListingsPageProperty, page);
            OpenListings(player);
            return true;
        }
        if (command.Contains("browse"))
        {
            OpenBrowse(player);
            return true;
        }

        if (command.Contains("manage") || command.Contains("listing") || command == "sell")
        {
            OpenListings(player);
            return true;
        }

        if (command.Contains("claim") || command.Contains("proceeds"))
        {
            if (TryClaimPlayerProceeds(player, Realm, out long claimed))
                player.Out.SendMessage($"You claimed {Money.GetString(claimed)} from Realm Exchange sales.", eChatType.CT_Merchant, eChatLoc.CL_ChatWindow);
            else
                player.Out.SendMessage("You have no Realm Exchange sale proceeds waiting in this realm.", eChatType.CT_Merchant, eChatLoc.CL_ChatWindow);
            return true;
        }

        if (command.Contains("how") || command.Contains("work") || command == "help")
        {
            player.Out.SendCustomTextWindow(
                "Realm Exchange - Help",
                [
                    "Browse: opens the realm market search. Drag a result to an empty backpack slot to buy it.",
                    "Manage: opens your listing chest. Drag a tradable backpack item into an empty slot, then enter its copper price.",
                    "Drag one of your listed items back to your backpack to cancel it.",
                    "A purchase transfers that exact persisted item and that exact amount of copper between buyer and seller.",
                    "Player sale proceeds remain safely held by the exchange while you are offline. Return here and choose Claim sale proceeds.",
                ]);
            return true;
        }

        return false;
    }

    private void OpenBrowse(GamePlayer player)
    {
        SetActive(player, ModeBrowse);
        player.Out.SendMarketExplorerWindow();
        player.Out.SendMessage("Filter by name, armor type (cloth/leather/etc.), level range or bonuses. Use the market's page controls; examine an item for full stats. Drag a result to your backpack to buy. /whisper manage opens your listings.", eChatType.CT_Merchant, eChatLoc.CL_ChatWindow);
    }

    private void OpenListings(GamePlayer player)
    {
        SetActive(player, ModeSell);
        var slots = Enumerable.Range((int)FirstClientSlot, 100).ToDictionary(slot => slot, _ => (DbInventoryItem)null);
        foreach (var item in GetClientInventory(player)) slots[item.Key] = item.Value;
        player.Out.SendInventoryItemsUpdate(slots, eInventoryWindowType.ConsignmentOwner);
        player.Out.SendConsignmentMerchantMoney(0);
        player.Out.SendMessage("Drag a real item into an empty exchange slot, then set its price in copper.", eChatType.CT_Merchant, eChatLoc.CL_ChatWindow);
        int page = player.TempProperties.GetProperty<int>(ListingsPageProperty);
        player.Out.SendMessage($"Your listings - page {page + 1}. Your listings have no total cap and never expire.\n[Previous listing page]  [Next listing page]\n[Browse the exchange]", eChatType.CT_Say, eChatLoc.CL_PopupWindow);
    }

    private void SetActive(GamePlayer player, string mode)
    {
        player.ActiveInventoryObject?.RemoveObserver(player);
        player.ActiveInventoryObject = this;
        player.TargetObject = this;
        player.TempProperties.SetProperty(ModeProperty, mode);
        AddObserver(player);
    }

    public string GetOwner(GamePlayer player) => player.InternalID;

    public IEnumerable<DbInventoryItem> GetDbItems(GamePlayer player)
    {
        if (player == null)
            return Array.Empty<DbInventoryItem>();

        ItemQuery query = new() { Owner = player.InternalID };
        return MarketCache.SearchItems(query).Where(IsThisRealmExchangeItem).ToArray();
    }

    public Dictionary<int, DbInventoryItem> GetClientInventory(GamePlayer player)
    {
        int firstPageSlot = FirstDbSlot + player.TempProperties.GetProperty<int>(ListingsPageProperty) * 100;
        int offset = (int)FirstClientSlot - firstPageSlot;
        return GetDbItems(player)
            .Where(item => item.SlotPosition >= firstPageSlot && item.SlotPosition < firstPageSlot + 100)
            .GroupBy(item => item.SlotPosition)
            .ToDictionary(group => group.Key + offset, group => group.First());
    }

    public bool SearchInventory(GamePlayer player, MarketSearch.SearchData searchData)
    {
        if (!CanUseExchange(player) || player.ActiveInventoryObject != this)
            return false;
        List<DbInventoryItem> all = new MarketSearch(player).FindExchangeItems(searchData, ExchangeOwnerLot);

        // The legacy protocol has one-byte page numbers; never wrap page 256
        // back to zero. Narrower filters expose any matching listing.
        int maxPage = Math.Min(byte.MaxValue, Math.Max(0, (all.Count - 1) / PageSize));
        int page = Math.Min(searchData.page, maxPage);
        List<DbInventoryItem> pageItems = all.Skip(page * PageSize).Take(PageSize).ToList();

        player.TempProperties.SetProperty(BrowseListProperty, pageItems);
        player.TempProperties.SetProperty(MarketExplorer.EXPLORER_ITEM_LIST, pageItems);
        player.Out.SendMarketExplorerWindow(pageItems, (byte)page, (byte)maxPage);
        if (searchData.page == 0)
            player.Out.SendMessage($"Realm Exchange returned {all.Count} real listing(s).", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        if (all.Count > PageSize * (byte.MaxValue + 1))
            player.Out.SendMessage("This client can display 5,120 matches per search. Narrow the name, level, armor or bonus filters to find additional items.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        return true;
    }

    public bool CanHandleMove(GamePlayer player, eInventorySlot fromClientSlot, eInventorySlot toClientSlot)
    {
        if (!CanUseExchange(player) || player.ActiveInventoryObject != this)
            return false;

        string mode = player.TempProperties.GetProperty(ModeProperty, ModeBrowse);
        if (mode == ModeBrowse)
            return fromClientSlot >= eInventorySlot.MarketExplorerFirst &&
                   (int)fromClientSlot < (int)eInventorySlot.MarketExplorerFirst + PageSize &&
                   GameInventoryObjectExtensions.IsBackpackSlot(toClientSlot);

        if (GameInventoryObjectExtensions.IsBackpackSlot(fromClientSlot))
        {
            DbInventoryItem incoming = player.Inventory.GetItem(fromClientSlot);
            if (incoming == null || !incoming.IsTradable)
            {
                player.Out.SendMessage("That item cannot be traded.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return false;
            }
            if (GetClientInventory(player).ContainsKey((int)toClientSlot))
                return false;
        }

        return this.CanHandleRequest(fromClientSlot, toClientSlot);
    }

    public bool MoveItem(GamePlayer player, eInventorySlot fromClientSlot, eInventorySlot toClientSlot, ushort itemCount)
    {
        if (!CanHandleMove(player, fromClientSlot, toClientSlot))
            return false;

        string mode = player.TempProperties.GetProperty(ModeProperty, ModeBrowse);
        if (mode == ModeBrowse)
        {
            List<DbInventoryItem> items = player.TempProperties.GetProperty<List<DbInventoryItem>>(BrowseListProperty);
            int index = (int)fromClientSlot - (int)eInventorySlot.MarketExplorerFirst;
            if (items == null || index < 0 || index >= items.Count)
                return false;

            BuyItem(player, items[index]);
            return true;
        }

        lock (TransactionLock)
        {
            var page = new ListingPageInventory(this, player.TempProperties.GetProperty<int>(ListingsPageProperty));
            IDictionary<int, DbInventoryItem> updates = GameInventoryObjectExtensions.MoveItem(page, player, fromClientSlot, toClientSlot, itemCount);
            // A broker is shared, but each customer's chest is private. Never
            // broadcast one seller's slot changes to another seller's chest.
            if (updates != null && updates.Count > 0)
                player.Out.SendInventoryItemsUpdate(updates, eInventoryWindowType.Update);
            return updates != null;
        }
    }

    public bool OnAddItem(GamePlayer player, DbInventoryItem item, int previousSlot)
    {
        if (item == null || !item.IsTradable || ExchangeOwnerLot == 0)
            return false;

        item.OwnerLot = ExchangeOwnerLot;
        item.OwnerID = player.InternalID;
        item.SellPrice = 0;
        MarketCache.AddItem(item);
        GameServer.Database.SaveObject(item);
        return true;
    }

    public bool OnRemoveItem(GamePlayer player, DbInventoryItem item, int previousSlot)
    {
        MarketCache.RemoveItem(item);
        item.OwnerLot = 0;
        item.SellPrice = 0;
        return true;
    }

    public bool OnMoveItem(GamePlayer player, DbInventoryItem firstItem, int previousFirstSlot, DbInventoryItem secondItem, int previousSecondSlot) => true;

    public void OnItemManipulationError(GamePlayer player) =>
        player.Out.SendMessage("The Realm Exchange could not move that item.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

    public bool SetSellPrice(GamePlayer player, eInventorySlot clientSlot, uint sellPrice)
    {
        lock (TransactionLock)
            return SetSellPriceCore(player, clientSlot, sellPrice);
    }

    private bool SetSellPriceCore(GamePlayer player, eInventorySlot clientSlot, uint sellPrice)
    {
        if (!CanUseExchange(player) || player.ActiveInventoryObject != this || player.TempProperties.GetProperty(ModeProperty, string.Empty) != ModeSell)
            return false;

        eInventorySlot mappedSlot = clientSlot + (int)FirstClientSlot;
        if (!GetClientInventory(player).TryGetValue((int)mappedSlot, out DbInventoryItem item))
            return false;

        if (!item.IsTradable)
        {
            player.Out.SendCustomDialog("That item cannot be traded.", null);
            return false;
        }

        int price = sellPrice > int.MaxValue ? int.MaxValue : (int)sellPrice;
        MarketCache.UpdateItem(item, static (listedItem, value) => listedItem.SellPrice = value, price);
        GameServer.Database.SaveObject(item);
        player.Out.SendMessage(price > 0 ? $"{item.Name} is listed for {Money.GetString(price)}." : $"{item.Name} is stored but not offered for sale.", eChatType.CT_Merchant, eChatLoc.CL_ChatWindow);
        return true;
    }

    private void BuyItem(GamePlayer buyer, DbInventoryItem item)
    {
        lock (TransactionLock)
        lock (typeof(AutonomousBotEconomy))
        lock (AutonomousBotStatusPersistence.DatabaseWriteLock)
        {
            if (!CanUseExchange(buyer) || !IsAvailableListing(item, Realm))
            {
                ChatUtil.SendErrorMessage(buyer, "That listing is no longer available.");
                return;
            }

            if (item.OwnerID == buyer.InternalID)
            {
                ChatUtil.SendErrorMessage(buyer, "You cannot buy your own listing.");
                return;
            }

            eInventorySlot destination = buyer.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);
            if (destination == eInventorySlot.Invalid)
            {
                ChatUtil.SendErrorMessage(buyer, "You need an empty backpack slot.");
                return;
            }

            int price = item.SellPrice;
            string sellerId = item.OwnerID;
            int sellerSlot = item.SlotPosition;
            SellerPayment payment = PrepareSellerPayment(sellerId, price, Realm);
            DbAccountXMoney wallet = PlayerWallet(buyer);
            if (payment == null || wallet == null)
            {
                ChatUtil.SendErrorMessage(buyer, "The seller's account could not accept payment. Nothing was charged.");
                return;
            }
            long originalMoney = buyer.GetCurrentMoney();
            if (!buyer.RemoveMoney(price))
            {
                ChatUtil.SendErrorMessage(buyer, $"You need {Money.GetString(price)}.");
                return;
            }

            MarketCache.RemoveItem(item);
            item.OwnerLot = 0;
            item.SellPrice = 0;
            if (!buyer.Inventory.AddItemWithoutDbAddition(destination, item))
            {
                item.OwnerID = sellerId;
                item.SlotPosition = sellerSlot;
                item.OwnerLot = ExchangeOwnerLot;
                item.SellPrice = price;
                MarketCache.AddItem(item);
                buyer.AddMoney(price);
                ChatUtil.SendErrorMessage(buyer, "The exchange could not deliver the item; your money was returned.");
                return;
            }

            payment.Apply();
            SetWallet(wallet, buyer.GetCurrentMoney());
            var sale = new RealmExchangeSaleLedger.Sale((int)Realm, item.Name, item.Count, price,
                sellerId, payment.SellerName, buyer.InternalID, buyer.Name, DateTime.UtcNow);
            if (!RealmExchangeSaleLedger.CommitSale(GameServer.Database, sale, item, buyer.DBCharacter, wallet, payment.Row))
            {
                payment.Restore();
                buyer.Inventory.RemoveItemWithoutDbDeletion(item);
                item.OwnerID = sellerId;
                item.SlotPosition = sellerSlot;
                item.OwnerLot = ExchangeOwnerLot;
                item.SellPrice = price;
                MarketCache.AddItem(item);
                buyer.AddMoney(price);
                SetWallet(wallet, originalMoney);
                ChatUtil.SendErrorMessage(buyer, "The exchange could not save this purchase. Your money and the listing were restored.");
                return;
            }

            InventoryLogging.LogInventoryAction(buyer, this, eInventoryActionType.Merchant, price);
            buyer.Out.SendMessage($"You bought {item.GetName(1, false)} for {Money.GetString(price)}.", eChatType.CT_Merchant, eChatLoc.CL_ChatWindow);
        }
    }

    internal static bool CreditSeller(string sellerId, long copper, eRealm saleRealm)
    {
        if (AutonomousBotEconomy.TryParseOwnerId(sellerId, out long botId))
            return AutonomousBotEconomy.AddMoney(botId, copper);

        DbCoreCharacter character = DOLDB<DbCoreCharacter>.SelectObject(DB.Column("DOLCharacters_ID").IsEqualTo(sellerId));
        if (character == null || (eRealm)character.Realm != saleRealm || copper <= 0)
            return false;

        RealmExchangeProceedsState state = GetReconciledProceedsState(character, saleRealm, out DbCoreCharacterXCustomParam record);
        if (state.PendingCopper > long.MaxValue - copper)
            return false;

        state = new RealmExchangeProceedsState(state.PendingCopper + copper, false, 0);
        SaveProceedsRecord(character.ObjectId, saleRealm, record, state);

        GamePlayer online = ClientService.Instance.GetPlayers()
            .FirstOrDefault(player => player.InternalID == sellerId || player.ObjectId == sellerId);
        online?.Out.SendMessage(
            $"Your Realm Exchange listing sold for {Money.GetString(copper)}. The proceeds are ready to claim at the exchange.",
            eChatType.CT_Merchant,
            eChatLoc.CL_ChatWindow);
        return true;
    }

    internal static long GetPendingPlayerProceeds(string characterId, eRealm realm)
    {
        if (string.IsNullOrWhiteSpace(characterId) || realm == eRealm.None)
            return 0;

        lock (TransactionLock)
        {
            DbCoreCharacter character = DOLDB<DbCoreCharacter>.SelectObject(DB.Column("DOLCharacters_ID").IsEqualTo(characterId));
            if (character == null || (eRealm)character.Realm != realm)
                return 0;
            return GetReconciledProceedsState(character, realm, out _).PendingCopper;
        }
    }

    internal static bool TryClaimPlayerProceeds(GamePlayer player, eRealm brokerRealm, out long claimedCopper)
    {
        claimedCopper = 0;
        if (player == null || player.Realm != brokerRealm || string.IsNullOrWhiteSpace(player.InternalID))
            return false;

        lock (TransactionLock)
        lock (typeof(AutonomousBotEconomy))
        lock (AutonomousBotStatusPersistence.DatabaseWriteLock)
        {
            DbCoreCharacter character = DOLDB<DbCoreCharacter>.SelectObject(DB.Column("DOLCharacters_ID").IsEqualTo(player.InternalID));
            if (character == null || (eRealm)character.Realm != brokerRealm)
                return false;

            RealmExchangeProceedsState state = GetReconciledProceedsState(character, brokerRealm, out DbCoreCharacterXCustomParam record);
            if (state.PendingCopper <= 0 || player.GetCurrentMoney() > long.MaxValue - state.PendingCopper)
                return false;

            long startingCopper = player.GetCurrentMoney();
            DbAccountXMoney wallet = PlayerWallet(player);
            if (wallet == null || record == null) return false;
            string originalProceeds = record.Value;
            player.AddMoney(state.PendingCopper);
            SetWallet(wallet, player.GetCurrentMoney());
            record.Value = new RealmExchangeProceedsState(0, false, 0).Serialize();
            if (!CommitExchangeRows(player.DBCharacter, wallet, record))
            {
                player.RemoveMoney(state.PendingCopper);
                SetWallet(wallet, startingCopper);
                record.Value = originalProceeds;
                return false;
            }
            claimedCopper = state.PendingCopper;
            return true;
        }
    }

    private static RealmExchangeProceedsState GetReconciledProceedsState(
        DbCoreCharacter character,
        eRealm realm,
        out DbCoreCharacterXCustomParam record)
    {
        string key = GetProceedsKey(realm);
        record = DOLDB<DbCoreCharacterXCustomParam>.SelectObject(
            DB.Column("DOLCharactersObjectId").IsEqualTo(character.ObjectId)
                .And(DB.Column("KeyName").IsEqualTo(key)));
        RealmExchangeProceedsState state = RealmExchangeProceedsState.Parse(record?.Value);
        if (!state.ClaimInProgress)
            return state;

        long storedCopper = Money.GetMoney(character.Mithril, character.Platinum, character.Gold, character.Silver, character.Copper);
        long expectedCopper = state.StartingCopper > long.MaxValue - state.PendingCopper
            ? long.MaxValue
            : state.StartingCopper + state.PendingCopper;
        state = storedCopper >= expectedCopper
            ? new RealmExchangeProceedsState(0, false, 0)
            : new RealmExchangeProceedsState(state.PendingCopper, false, 0);
        SaveProceedsRecord(character.ObjectId, realm, record, state);
        return state;
    }

    private static void SaveProceedsRecord(
        string characterId,
        eRealm realm,
        DbCoreCharacterXCustomParam record,
        RealmExchangeProceedsState state)
    {
        if (record == null)
        {
            record = new DbCoreCharacterXCustomParam(characterId, GetProceedsKey(realm), state.Serialize());
            GameServer.Database.AddObject(record);
            return;
        }

        record.Value = state.Serialize();
        GameServer.Database.SaveObject(record);
    }

    private static string GetProceedsKey(eRealm realm) => $"{ProceedsKeyPrefix}{(int)realm}";

    private readonly record struct RealmExchangeProceedsState(long PendingCopper, bool ClaimInProgress, long StartingCopper)
    {
        public string Serialize() => $"{PendingCopper}|{(ClaimInProgress ? 1 : 0)}|{StartingCopper}";

        public static RealmExchangeProceedsState Parse(string value)
        {
            string[] parts = value?.Split('|');
            return parts?.Length == 3 && long.TryParse(parts[0], out long pending) && pending >= 0 &&
                   int.TryParse(parts[1], out int claiming) && long.TryParse(parts[2], out long starting) && starting >= 0
                ? new RealmExchangeProceedsState(pending, claiming == 1, starting)
                : new RealmExchangeProceedsState(0, false, 0);
        }
    }

    private bool IsThisRealmExchangeItem(DbInventoryItem item) => item != null && item.OwnerLot == ExchangeOwnerLot;

    private bool CanUseExchange(GamePlayer player) => player != null && player.Realm == Realm &&
        player.IsAlive && ObjectState == eObjectState.Active && player.IsWithinRadius(this, WorldMgr.INTERACT_DISTANCE);

    internal static bool IsExchangeOwnerLot(ushort ownerLot) => ownerLot is AlbionOwnerLot or MidgardOwnerLot or HiberniaOwnerLot;

    public void AddObserver(GamePlayer player)
    {
        if (player != null)
            _observers[player.Name] = player;
    }

    public void RemoveObserver(GamePlayer player)
    {
        if (player != null)
            _observers.Remove(player.Name);
    }
}

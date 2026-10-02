using System.Collections.Generic;
using System.Threading;
using DOL.Database;

namespace DOL.GS;

public sealed partial class RealmExchangeBroker
{
    // The native client is fixed at 100 visible slots. Human listings use
    // additional pages, while bot listing allocation retains its 100-item cap.
    // Reuse the tested inventory move/split/validation routines with a per-call
    // DB offset, never mutable shared offsets on the capital's single broker.
    private sealed class ListingPageInventory(RealmExchangeBroker broker, int page) : IGameInventoryObject
    {
        public eInventorySlot FirstClientSlot => broker.FirstClientSlot;
        public eInventorySlot LastClientSlot => broker.LastClientSlot;
        public int FirstDbSlot => broker.FirstDbSlot + page * 100;
        public int LastDbSlot => FirstDbSlot + 99;
        public Lock Lock => broker.Lock;
        public string GetOwner(GamePlayer player) => broker.GetOwner(player);
        public IEnumerable<DbInventoryItem> GetDbItems(GamePlayer player) => broker.GetDbItems(player);
        public Dictionary<int, DbInventoryItem> GetClientInventory(GamePlayer player) => broker.GetClientInventory(player);
        public bool CanHandleMove(GamePlayer player, eInventorySlot from, eInventorySlot to) => broker.CanHandleMove(player, from, to);
        public bool MoveItem(GamePlayer player, eInventorySlot from, eInventorySlot to, ushort count) => broker.MoveItem(player, from, to, count);
        public bool OnAddItem(GamePlayer player, DbInventoryItem item, int previousSlot) => broker.OnAddItem(player, item, previousSlot);
        public bool OnRemoveItem(GamePlayer player, DbInventoryItem item, int previousSlot) => broker.OnRemoveItem(player, item, previousSlot);
        public bool OnMoveItem(GamePlayer player, DbInventoryItem first, int firstSlot, DbInventoryItem second, int secondSlot) => broker.OnMoveItem(player, first, firstSlot, second, secondSlot);
        public void OnItemManipulationError(GamePlayer player) => broker.OnItemManipulationError(player);
        public bool SetSellPrice(GamePlayer player, eInventorySlot slot, uint price) => broker.SetSellPrice(player, slot, price);
        public bool SearchInventory(GamePlayer player, MarketSearch.SearchData search) => broker.SearchInventory(player, search);
        public void AddObserver(GamePlayer player) => broker.AddObserver(player);
        public void RemoveObserver(GamePlayer player) => broker.RemoveObserver(player);
    }
}

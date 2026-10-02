using System;
using System.Linq;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.Database.Attributes;
using DOL.Events;
using DOL.GS.GameEvents;

namespace DOL.GS;

/// <summary>
/// Bounded, durable history. One of 50 existing rows is updated in the SAME
/// transaction as item ownership and payment. No polling, per-bot work, or
/// successful-looking entries for purchases that fail to commit.
/// </summary>
public static class RealmExchangeSaleLedger
{
    public const int Capacity = 50;
    private sealed class State
    {
        internal readonly object Gate = new();
        internal RealmExchangeSaleRecord[] Rows;
        internal long NextErrorTick;
    }
    private static readonly ConditionalWeakTable<IObjectDatabase, State> States = new();

    public sealed record Sale(int Realm, string ItemName, int Quantity, long PriceCopper,
        string SellerId, string SellerName, string BuyerId, string BuyerName, DateTime SoldUtc);

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        IObjectDatabase database = GameServer.Database;
        State state = States.GetOrCreateValue(database);
        lock (RealmExchangeBroker.TransactionLock)
        lock (AutonomousBotStatusPersistence.DatabaseWriteLock)
        lock (state.Gate)
        {
            try { Initialize(database, state); }
            catch (Exception error) { ReportError(state, error); }
        }
    }

    // Callers retain their existing transaction/money/database locks and their
    // original item/coin rollback logic. No buying or selling decisions change.
    public static bool CommitSale(IObjectDatabase database, Sale sale, params DataObject[] paymentRows)
    {
        if (database == null || sale == null || sale.Realm is < 1 or > 3 || sale.PriceCopper <= 0 ||
            sale.Quantity <= 0 || string.IsNullOrWhiteSpace(sale.SellerId) ||
            string.IsNullOrWhiteSpace(sale.BuyerId) || sale.SellerId == sale.BuyerId ||
            paymentRows == null || paymentRows.Length == 0)
            return false;
        State state = States.GetOrCreateValue(database);
        lock (state.Gate)
        {
            try
            {
                Initialize(database, state);
                RealmExchangeSaleRecord previous = state.Rows.OrderBy(row => row.SaleSequence).ThenBy(row => row.SlotId).First();
                var next = new RealmExchangeSaleRecord
                {
                    SlotId = previous.SlotId, IsPersisted = true, Dirty = true,
                    SaleSequence = checked(state.Rows.Max(row => row.SaleSequence) + 1),
                    SoldUtc = sale.SoldUtc.ToUniversalTime().ToString("O"), Realm = sale.Realm,
                    ItemName = string.IsNullOrWhiteSpace(sale.ItemName) ? "Unknown item" : sale.ItemName,
                    Quantity = sale.Quantity, PriceCopper = sale.PriceCopper,
                    SellerId = sale.SellerId, SellerName = sale.SellerName ?? "Unknown seller",
                    BuyerId = sale.BuyerId, BuyerName = sale.BuyerName ?? "Unknown buyer",
                };
                DataObject[] rows = paymentRows.Append(next).ToArray();
                bool saved = database is SqlObjectDatabase sql
                    ? sql.SaveObjectsAtomically(rows) : database.SaveObject(rows);
                if (!saved) return false;
                // Replace the cached row only after commit. A failed write must
                // neither erase the old sale nor consume a history position.
                state.Rows[previous.SlotId - 1] = next;
                return true;
            }
            catch (Exception error)
            {
                ReportError(state, error);
                return false;
            }
        }
    }

    private static void Initialize(IObjectDatabase database, State state)
    {
        if (state.Rows != null) return;
        var stored = database.SelectAllObjects<RealmExchangeSaleRecord>().ToDictionary(row => row.SlotId);
        var rows = Enumerable.Range(1, Capacity).Select(slot => stored.TryGetValue(slot, out var row)
            ? row : new RealmExchangeSaleRecord { SlotId = slot }).ToArray();
        DataObject[] missing = rows.Where(row => !stored.ContainsKey(row.SlotId)).Cast<DataObject>().ToArray();
        if (missing.Length > 0 && !database.AddObject(missing))
            throw new InvalidOperationException("Could not initialize the Realm Exchange sale ledger.");
        state.Rows = rows;
    }

    private static void ReportError(State state, Exception error)
    {
        long now = Environment.TickCount64;
        if (now < state.NextErrorTick) return;
        state.NextErrorTick = now + 60_000;
        try { Logging.LoggerManager.Create(typeof(RealmExchangeSaleLedger)).Error("Realm Exchange sale ledger unavailable; purchase not committed.", error); }
        catch { }
    }
}

[DataTable(TableName = "realm_exchange_sales")]
public sealed class RealmExchangeSaleRecord : DataObject
{
    [PrimaryKey] public int SlotId { get; set; }
    [DataElement(AllowDbNull = false)] public long SaleSequence { get; set; }
    [DataElement(AllowDbNull = false)] public string SoldUtc { get; set; } = string.Empty;
    [DataElement(AllowDbNull = false)] public int Realm { get; set; }
    [DataElement(AllowDbNull = false)] public string ItemName { get; set; } = string.Empty;
    [DataElement(AllowDbNull = false)] public int Quantity { get; set; }
    [DataElement(AllowDbNull = false)] public long PriceCopper { get; set; }
    [DataElement(AllowDbNull = false)] public string SellerId { get; set; } = string.Empty;
    [DataElement(AllowDbNull = false)] public string SellerName { get; set; } = string.Empty;
    [DataElement(AllowDbNull = false)] public string BuyerId { get; set; } = string.Empty;
    [DataElement(AllowDbNull = false)] public string BuyerName { get; set; } = string.Empty;
}

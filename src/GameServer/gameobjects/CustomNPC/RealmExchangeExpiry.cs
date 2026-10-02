using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using DOL.Database;

namespace DOL.GS;

public static class RealmExchangeExpiry
{
    public static readonly TimeSpan BotListingLifetime = TimeSpan.FromHours(24);
    private static Timer _timer;
    private static int _running;
    public static DateTime? ExpiresUtc(DbInventoryItem item) =>
        item != null && RealmExchangeBroker.IsExchangeOwnerLot(item.OwnerLot) &&
        AutonomousBotEconomy.TryParseOwnerId(item.OwnerID, out _) &&
        DateTime.TryParse(item.RealmExchangeListedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime listed)
            ? listed.ToUniversalTime().Add(BotListingLifetime) : null;

    public static bool IsExpired(DbInventoryItem item, DateTime utcNow) => ExpiresUtc(item) is DateTime expiry && expiry <= utcNow;

    public static string Describe(DbInventoryItem item, DateTime utcNow)
    {
        if (ExpiresUtc(item) is not DateTime expiry) return "Player listing: no expiration.";
        TimeSpan remaining = expiry - utcNow;
        return remaining <= TimeSpan.Zero ? "Bot listing expired; awaiting removal." :
            $"Bot listing expires in {(int)remaining.TotalHours}h {remaining.Minutes}m ({expiry:yyyy-MM-dd HH:mm} UTC).";
    }

    internal static void Start()
    {
        // Old listings predate the timestamp column: give those bots one full
        // bot-listing lifetime from migration, never expire an item on an unknown age.
        foreach (DbInventoryItem item in MarketCache.SearchItems(new ItemQuery()))
        {
            if (!RealmExchangeBroker.IsExchangeOwnerLot(item.OwnerLot) ||
                !AutonomousBotEconomy.TryParseOwnerId(item.OwnerID, out _) || ExpiresUtc(item) != null) continue;
            item.RealmExchangeListedUtc = DateTime.UtcNow.ToString("O");
            GameServer.Database.SaveObject(item);
        }
        _timer ??= new Timer(_ => Sweep(), null, 60_000, 60_000);
    }

    private static void Sweep()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try { ExpireDue(DateTime.UtcNow); }
        catch (Exception error) { Logging.LoggerManager.Create(typeof(RealmExchangeExpiry)).Error("Realm Exchange expiry sweep failed; retained remaining items.", error); }
        finally { Volatile.Write(ref _running, 0); }
    }

    public static int ExpireDue(DateTime utcNow)
    {
        int removed = 0;
        // Once a minute, bounded writes; nothing runs in individual AI turns.
        foreach (DbInventoryItem item in MarketCache.SearchItems(new ItemQuery()).Where(item => IsExpired(item, utcNow)).Take(200))
        {
            lock (RealmExchangeBroker.TransactionLock)
            lock (AutonomousBotStatusPersistence.DatabaseWriteLock)
            {
                if (!IsExpired(item, utcNow) || !AutonomousBotEconomy.TryParseOwnerId(item.OwnerID, out long id)) continue;
                if (!GameServer.Database.DeleteObject(item)) continue;
                MarketCache.RemoveItem(item);
                AutonomousBotEconomy.MarkEconomyChanged(id);
                removed++;
            }
        }
        return removed;
    }
}

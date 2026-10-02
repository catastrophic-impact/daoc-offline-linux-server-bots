using System;
using System.Linq;
using DOL.Database;

namespace DOL.GS;

public sealed partial class RealmExchangeBroker
{
    // Called under TransactionLock + the bot money lock + DatabaseWriteLock.
    // Prepare validates the seller BEFORE charging the buyer. Only an empty
    // proceeds row may be created beforehand; it carries no money or item.
    internal sealed class SellerPayment
    {
        private readonly OfflineWorldBotRecord _bot;
        private readonly DbCoreCharacterXCustomParam _proceeds;
        private readonly long _originalMoney;
        private readonly string _originalValue;
        private readonly long _price;
        internal DataObject Row => (DataObject)_bot ?? _proceeds;
        internal string SellerName { get; }

        internal SellerPayment(OfflineWorldBotRecord bot, long price)
        {
            _bot = bot;
            SellerName = bot.Name;
            _originalMoney = bot.MoneyCopper;
            _price = price;
        }

        internal SellerPayment(DbCoreCharacterXCustomParam proceeds, long price, string sellerName)
        {
            _proceeds = proceeds;
            SellerName = sellerName;
            _originalValue = proceeds.Value;
            _price = price;
        }

        internal void Apply()
        {
            if (_bot != null)
            {
                _bot.MoneyCopper = _originalMoney + _price;
                _bot.Dirty = true;
            }
            else
            {
                var state = RealmExchangeProceedsState.Parse(_originalValue);
                _proceeds.Value = new RealmExchangeProceedsState(state.PendingCopper + _price, false, 0).Serialize();
            }
        }

        internal void Restore()
        {
            if (_bot != null) { _bot.MoneyCopper = _originalMoney; _bot.Dirty = true; }
            else _proceeds.Value = _originalValue;
        }
    }

    internal static SellerPayment PrepareSellerPayment(string sellerId, long price, eRealm realm)
    {
        if (price <= 0 || string.IsNullOrWhiteSpace(sellerId)) return null;
        if (AutonomousBotEconomy.TryParseOwnerId(sellerId, out long botId))
        {
            OfflineWorldBotRecord bot = AutonomousBotEconomy.FindLiveOrStored(botId);
            return bot != null && bot.Realm == (int)realm && bot.MoneyCopper <= long.MaxValue - price
                ? new SellerPayment(bot, price) : null;
        }
        DbCoreCharacter character = DOLDB<DbCoreCharacter>.SelectObject(DB.Column("DOLCharacters_ID").IsEqualTo(sellerId));
        if (character == null || character.Realm != (int)realm) return null;
        var state = GetReconciledProceedsState(character, realm, out var record);
        if (state.PendingCopper > long.MaxValue - price) return null;
        if (record == null)
        {
            record = new DbCoreCharacterXCustomParam(character.ObjectId, GetProceedsKey(realm), state.Serialize());
            if (!GameServer.Database.AddObject(record)) return null;
        }
        return new SellerPayment(record, price, character.Name);
    }

    internal static bool CommitExchangeRows(params DataObject[] rows) =>
        GameServer.Database is SqlObjectDatabase database
            ? database.SaveObjectsAtomically(rows)
            : GameServer.Database.SaveObject(rows); // Non-SQL test stores implement their own save semantics.

    private static DbAccountXMoney PlayerWallet(GamePlayer player)
    {
        DbAccountXMoney wallet = DOLDB<DbAccountXMoney>.SelectObject(
            DB.Column("AccountID").IsEqualTo(player.Client.Account.ObjectId).And(DB.Column("Realm").IsEqualTo(player.Realm)));
        if (wallet != null) return wallet;
        wallet = new DbAccountXMoney { AccountId = player.Client.Account.ObjectId, Realm = (int)player.Realm };
        SetWallet(wallet, player.GetCurrentMoney());
        return GameServer.Database.AddObject(wallet) ? wallet : null;
    }

    private static void SetWallet(DbAccountXMoney wallet, long money)
    {
        wallet.Mithril = Money.GetMithril(money);
        wallet.Platinum = Money.GetPlatinum(money);
        wallet.Gold = Money.GetGold(money);
        wallet.Silver = Money.GetSilver(money);
        wallet.Copper = Money.GetCopper(money);
    }

    internal static bool IsAvailableListing(DbInventoryItem item, eRealm realm) =>
        item != null && item.IsTradable && item.SellPrice > 0 && item.OwnerLot == GetOwnerLot(realm) &&
        !RealmExchangeExpiry.IsExpired(item, DateTime.UtcNow) &&
        MarketCache.SearchItems(new ItemQuery { Owner = item.OwnerID }).Any(listing => ReferenceEquals(listing, item));
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS
{
    /// <summary>Receives each real drop once, sharing it across the nearby expedition.</summary>
    public sealed class RealmRaidLootOwner : IGameStaticItemOwner
    {
        public sealed class Ledger
        {
            internal readonly Dictionary<long, int> ItemsReceived = new();
        }
        private readonly GameBot[] _members;
        private readonly Ledger _ledger;
        internal IReadOnlyList<GameBot> Members => _members;
        public string Name => "Realm expedition loot";
        public object GameStaticItemOwnerComparand => this;
        public RealmRaidLootOwner(GameBot[] members, Ledger ledger) { _members = members.Where(b => b != null).Distinct().ToArray(); _ledger = ledger; }

        public TryPickUpResult TryAutoPickUpItem(WorldInventoryItem item)
        {
            item.AssertLockAcquisition();
            lock (_ledger)
            {
                foreach (GameBot bot in _members.Where(b => Eligible(b, item))
                    .OrderBy(b => _ledger.ItemsReceived.GetValueOrDefault(b.DatabaseID)).ThenBy(_ => Random.Shared.Next()))
                {
                    TryPickUpResult result = bot.TryAutoPickUpItem(item);
                    if (result != TryPickUpResult.Success) continue;
                    _ledger.ItemsReceived[bot.DatabaseID] = _ledger.ItemsReceived.GetValueOrDefault(bot.DatabaseID) + 1;
                    return result;
                }
            }
            return TryPickUpResult.Blocked;
        }

        public TryPickUpResult TryAutoPickUpMoney(GameMoney money)
        {
            money.AssertLockAcquisition();
            // Use the same lock as normal bot currency transactions, but only live
            // records. Validate every share before mutation; no synchronous DB lookup.
            lock (typeof(AutonomousBotEconomy))
            {
                GameBot[] recipients = _members.Where(b => Eligible(b, money) && b.PersistentRecord != null).ToArray();
                if (!TryAllocateMoney(recipients.Select(b => b.PersistentRecord).ToArray(), money.Value))
                    return TryPickUpResult.Blocked;
                money.Value = 0; // Paid bags can never be paid again, including after a save-queue failure.
                money.RemoveFromWorld();
                foreach (GameBot bot in recipients)
                    AutonomousBotStatusPersistence.Queue(bot);
            }
            return TryPickUpResult.Success;
        }

        // Caller holds the normal economy lock. Validate the entire allocation
        // first so overflow cannot leave a partially paid raid or consume a bag.
        internal static bool TryAllocateMoney(OfflineWorldBotRecord[] records, long copper)
        {
            if (records.Length == 0 || copper < 0 || records.Any(r => r == null) ||
                records.Distinct().Count() != records.Length) return false;
            long quotient = copper / records.Length, remainder = copper % records.Length;
            for (int i = 0; i < records.Length; i++)
                if (records[i].MoneyCopper > long.MaxValue - quotient - (i < remainder ? 1 : 0)) return false;
            string now = DateTime.UtcNow.ToString("O");
            for (int i = 0; i < records.Length; i++)
            {
                records[i].MoneyCopper += quotient + (i < remainder ? 1 : 0);
                records[i].LastUpdateUtc = now;
                records[i].Dirty = true;
            }
            return true;
        }

        private static bool Eligible(GameBot bot, GameObject drop) => bot.IsAutonomousWorldBot && !bot.IsTemporaryGroupHelper &&
            bot.ObjectState == GameObject.eObjectState.Active && bot.CurrentRegionID == drop.CurrentRegionID &&
            bot.IsWithinRadius(drop, WorldMgr.VISIBILITY_DISTANCE) &&
            AutonomousBotRegistry.TryGet(bot.DatabaseID, out var current) && ReferenceEquals(current, bot);
        public TryPickUpResult TryPickUpMoney(GamePlayer source, GameMoney money) => TryPickUpResult.DoesNotWant;
        public TryPickUpResult TryPickUpItem(GamePlayer source, WorldInventoryItem item) => TryPickUpResult.DoesNotWant;
    }
}

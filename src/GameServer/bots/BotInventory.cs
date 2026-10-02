using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DOL.Database;

namespace DOL.GS
{
    public class BotInventory : GameLivingInventory
    {
        private readonly string _persistenceOwnerId;
        private readonly HashSet<DbInventoryItem> _itemsBeingSaved = new(ReferenceEqualityComparer.Instance);

        public BotInventory()
        {
        }

        public BotInventory(string persistenceOwnerId)
        {
            _persistenceOwnerId = persistenceOwnerId;
        }

        public bool IsPersistent => !string.IsNullOrWhiteSpace(_persistenceOwnerId);

        public override Task<IList> StartLoadFromDatabaseTask(string inventoryId)
        {
            string ownerId = string.IsNullOrWhiteSpace(inventoryId) ? _persistenceOwnerId : inventoryId;
            if (string.IsNullOrWhiteSpace(ownerId))
                return Task.FromResult<IList>(Array.Empty<DbInventoryItem>());

            WhereClause personalSlots = DB.Column("SlotPosition").IsLessOrEqualTo((int)eInventorySlot.LastVault)
                .Or(DB.Column("SlotPosition").IsGreaterOrEqualTo(500).And(DB.Column("SlotPosition").IsLessThan(600)));
            return DOLDB<DbInventoryItem>.SelectObjectsAsync(DB.Column("OwnerID").IsEqualTo(ownerId).And(personalSlots))
                .ContinueWith(task => task.Result as IList);
        }

        public override bool LoadFromDatabase(string inventoryId)
        {
            IList items = GameLoopAsyncHelper.GetResult(StartLoadFromDatabaseTask(inventoryId));
            return LoadInventory(inventoryId, items);
        }

        public override bool LoadInventory(string inventoryId, IList items)
        {
            lock (Lock)
            {
                m_items.Clear();
                foreach (DbInventoryItem item in items ?? Array.Empty<DbInventoryItem>())
                {
                    eInventorySlot slot = (eInventorySlot)item.SlotPosition;
                    if (GetValidInventorySlot(slot) == eInventorySlot.Invalid || m_items.ContainsKey(slot))
                        continue;
                    m_items.Add(slot, item);
                }
            }
            return true;
        }

        public override bool SaveIntoDatabase(string inventoryId)
        {
            string ownerId = string.IsNullOrWhiteSpace(inventoryId) ? _persistenceOwnerId : inventoryId;
            if (string.IsNullOrWhiteSpace(ownerId))
                return false;

            return SaveManyIntoDatabase([(this, ownerId)]);
        }

        /// <summary>
        /// Persists several real bot inventories with one bulk delete/save/add
        /// sequence. The old path opened up to three database transactions per
        /// bot while holding the global writer lock. This keeps every physical
        /// item and slot, but dramatically shortens that lock window.
        /// </summary>
        public static bool SaveManyIntoDatabase(IEnumerable<(BotInventory Inventory, string OwnerId)> inventories)
        {
            // Listing, purchases, logout and the rolling writer must not race
            // each other's persistence flags or ownership changes.
            lock (AutonomousBotStatusPersistence.DatabaseWriteLock)
                return SaveManyIntoDatabaseLocked(inventories);
        }

        private static bool SaveManyIntoDatabaseLocked(IEnumerable<(BotInventory Inventory, string OwnerId)> inventories)
        {
            var captures = new List<(BotInventory Inventory, List<DbInventoryItem> Deleted)>();
            List<DbInventoryItem> add = [];
            List<DbInventoryItem> update = [];
            List<DbInventoryItem> remove = [];
            var written = new List<(BotInventory Inventory, DbInventoryItem Live, DbInventoryItem Snapshot)>();
            var deletedCopies = new List<(DbInventoryItem Live, DbInventoryItem Snapshot)>();

            try
            {
            foreach ((BotInventory inventory, string ownerId) in inventories)
            {
                if (inventory == null || string.IsNullOrWhiteSpace(ownerId))
                    continue;

                List<DbInventoryItem> deleted;
                lock (inventory.Lock)
                {
                    deleted = inventory._itemsAwaitingDeletion.Where(item => item.IsPersisted).ToList();
                    foreach (DbInventoryItem item in deleted)
                    {
                        DbInventoryItem snapshot = item.CreatePersistenceSnapshot();
                        remove.Add(snapshot);
                        deletedCopies.Add((item, snapshot));
                    }
                    foreach ((eInventorySlot slot, DbInventoryItem item) in inventory.m_items)
                    {
                        if (item == null || item is GameInventoryRelic)
                            continue;
                        item.OwnerID = ownerId;
                        item.SlotPosition = (int)slot;
                        // The actor may move/sell this item as soon as Lock is
                        // released. Only detached scalar state reaches SQLite.
                        DbInventoryItem snapshot = item.CreatePersistenceSnapshot();
                        inventory._itemsBeingSaved.Add(item);
                        written.Add((inventory, item, snapshot));
                        if (item.IsStackable && item.Count == 0)
                        {
                            remove.Add(snapshot);
                            deletedCopies.Add((item, snapshot));
                        }
                        else if (item.IsPersisted)
                            update.Add(snapshot);
                        else
                            add.Add(snapshot);
                    }
                }
                captures.Add((inventory, deleted));
            }

            // Persist definitions before their new inventory references. The ORM
            // otherwise inserts the parent first, then its relations separately;
            // a failed relation write leaves a persisted parent that may be clean
            // on the next flush. Do not manufacture definitions for legacy orphans.
            var definitions = add.Concat(update).Select(item => item.IUWrapper)
                .Where(template => template != null && !template.IsPersisted)
                .Distinct<DbItemUnique>(ReferenceEqualityComparer.Instance).ToArray();
            if (definitions.Length != 0 && !GameServer.Database.AddObject(definitions))
                return false;

            bool saved = (remove.Count == 0 || GameServer.Database.DeleteObject(remove)) &&
                         (update.Count == 0 || GameServer.Database.SaveObject(update));
            if (saved && add.Count > 0 && !GameServer.Database.AddObject(add))
            {
                // A previous partially successful batch may have written a row
                // before its in-memory persisted flag was acknowledged. Reconcile
                // only those failed inserts, never overwrite a different item.
                saved = false;
                foreach (DbInventoryItem item in add.Where(item => !item.IsPersisted && !string.IsNullOrWhiteSpace(item.ObjectId)))
                {
                    DbInventoryItem stored = GameServer.Database.FindObjectByKey<DbInventoryItem>(item.ObjectId);
                    if (!CanReconcileSavedItem(item, stored)) continue;
                    item.IsPersisted = true;
                    item.Dirty = true; // Persist current slot on the next ordinary flush.
                }
            }
            if (!saved)
                return false;

            // Clear only the exact deletion records covered by the successful
            // batch. A concurrent later removal remains queued for the next run.
            foreach ((BotInventory inventory, List<DbInventoryItem> deleted) in captures)
            {
                lock (inventory.Lock)
                    foreach (DbInventoryItem item in deleted)
                        inventory._itemsAwaitingDeletion.Remove(item);
            }
            return true;
            }
            finally
            {
                // Acknowledge each successful row even if a later row failed.
                // Never clear live dirtiness: a newer slot/count/charge change
                // still belongs to the next regular flush.
                foreach (var entry in written)
                    lock (entry.Inventory.Lock)
                    {
                        if (entry.Snapshot.IsPersisted)
                        {
                            entry.Live.IsPersisted = true;
                            entry.Live.AcceptPersistenceBaseline(entry.Snapshot);
                        }
                        entry.Inventory._itemsBeingSaved.Remove(entry.Live);
                        if (!entry.Snapshot.IsPersisted && !entry.Live.IsPersisted)
                            entry.Inventory._itemsAwaitingDeletion.Remove(entry.Live);
                    }
                foreach (var entry in deletedCopies)
                    if (!entry.Snapshot.IsPersisted || entry.Snapshot.IsDeleted)
                        entry.Live.IsPersisted = false;
            }
        }

        public static bool CanReconcileSavedItem(DbInventoryItem item, DbInventoryItem stored) =>
            item != null && stored != null && !string.IsNullOrWhiteSpace(item.ObjectId) &&
            item.ObjectId == stored.ObjectId && !string.IsNullOrWhiteSpace(item.OwnerID) &&
            item.OwnerID == stored.OwnerID && item.OwnerLot == 0 && stored.OwnerLot == 0 &&
            item.ITemplate_Id == stored.ITemplate_Id && item.UTemplate_Id == stored.UTemplate_Id && item.Count == stored.Count;

        public override bool AddItem(eInventorySlot slot, DbInventoryItem item)
        {
            lock (Lock)
            {
                if (!base.AddItem(slot, item))
                    return false;
                _itemsAwaitingDeletion.Remove(item);
                if (IsPersistent)
                    item.OwnerID = _persistenceOwnerId;
                return true;
            }
        }

        public override bool RemoveItem(DbInventoryItem item)
        {
            return RemoveItem(item, true);
        }

        public override bool RemoveItemWithoutDbDeletion(DbInventoryItem item)
        {
            return RemoveItem(item, false);
        }

        private bool RemoveItem(DbInventoryItem item, bool markForDeletion)
        {
            lock (Lock)
            {
                if (item == null || !base.RemoveItem(item))
                    return false;

                if (markForDeletion && IsPersistent)
                {
                    // A removal can race a not-yet-acknowledged INSERT. Retain
                    // its tombstone so that successful insert is deleted later.
                    if ((item.IsPersisted || _itemsBeingSaved.Contains(item)) && !_itemsAwaitingDeletion.Contains(item))
                        _itemsAwaitingDeletion.Add(item);
                }
                return true;
            }
        }

        protected override eInventorySlot GetValidInventorySlot(eInventorySlot slot)
        {
            switch (slot)
            {
                case eInventorySlot.LastEmptyQuiver:
                    slot = FindLastEmptySlot(eInventorySlot.FirstQuiver, eInventorySlot.FourthQuiver);
                    break;
                case eInventorySlot.FirstEmptyQuiver:
                    slot = FindFirstEmptySlot(eInventorySlot.FirstQuiver, eInventorySlot.FourthQuiver);
                    break;
                case eInventorySlot.LastEmptyVault:
                    slot = FindLastEmptySlot(eInventorySlot.FirstVault, eInventorySlot.LastVault);
                    break;
                case eInventorySlot.FirstEmptyVault:
                    slot = FindFirstEmptySlot(eInventorySlot.FirstVault, eInventorySlot.LastVault);
                    break;
                case eInventorySlot.LastEmptyBackpack:
                    slot = FindLastEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);
                    break;
                case eInventorySlot.FirstEmptyBackpack:
                    slot = FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);
                    break;
                case eInventorySlot.LastEmptyBagHorse:
                    slot = FindLastEmptySlot(eInventorySlot.FirstBagHorse, eInventorySlot.LastBagHorse);
                    break;
                case eInventorySlot.FirstEmptyBagHorse:
                    slot = FindFirstEmptySlot(eInventorySlot.FirstBagHorse, eInventorySlot.LastBagHorse);
                    break;
            }

            if ((slot >= eInventorySlot.FirstBackpack && slot <= eInventorySlot.LastBackpack)
                || (slot >= eInventorySlot.HorseArmor && slot <= eInventorySlot.Horse)
                || (slot >= eInventorySlot.FirstVault && slot <= eInventorySlot.LastVault)
                || (slot >= eInventorySlot.HouseVault_First && slot <= eInventorySlot.HouseVault_Last)
                || (slot >= eInventorySlot.Consignment_First && slot <= eInventorySlot.Consignment_Last)
                || (slot == eInventorySlot.PlayerPaperDoll)
                || (slot == eInventorySlot.Mythical)
                || (slot >= eInventorySlot.FirstBagHorse && slot <= eInventorySlot.LastBagHorse))
                return slot;

            return base.GetValidInventorySlot(slot);
        }
    }
}

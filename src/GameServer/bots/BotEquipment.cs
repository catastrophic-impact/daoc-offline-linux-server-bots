using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Logging;

namespace DOL.GS
{
    public static class BotEquipment
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        private static int MinimumEquipmentLevel(IGamePlayer player, int defaultValue) =>
            player is GameBot { EquipmentLevelFloor: > 0 } bot
                ? Math.Max(defaultValue, bot.EquipmentLevelFloor)
                : defaultValue;

        private static int MaximumEquipmentLevel(IGamePlayer player, int defaultValue) =>
            player is GameBot { EquipmentLevelCap: > 0 } bot
                ? Math.Min(defaultValue, bot.EquipmentLevelCap)
                : defaultValue;

        private static bool ShouldEquipRegularSlot(IGamePlayer player) =>
            player is not GameBot bot || TemporaryCompanionBalance.EquipRegularSlot(bot.IsTemporaryGroupHelper, bot.Level, Random.Shared.NextDouble());

        private static void PrepareEndgameItem(IGamePlayer player, DbItemTemplate item)
        {
            if (player is not GameBot { IsEndgameCompanion: true }) return;
            item.Quality = 99;
            item.IsDropable = false;
            item.IsTradable = false;
        }

        private static DbItemTemplate EndgameItem(IGamePlayer player, eObjectType type, eInventorySlot slot, eWeaponDamageType damage = 0)
            => CreateEndgameCompanionItem(player.Realm, (eCharacterClass)player.CharacterClass.ID, type, slot, damage);

        public static DbItemTemplate CreateEndgameCompanionItem(eRealm realm, eCharacterClass characterClass, eObjectType type, eInventorySlot slot, eWeaponDamageType damage = 0)
            => CreateCompanionItem(realm, characterClass, 50, type, slot, damage);

        public static DbItemTemplate CreateCompanionItem(eRealm realm, eCharacterClass characterClass, byte level,
            eObjectType type, eInventorySlot slot, eWeaponDamageType damage = 0)
        {
            DbItemTemplate item = type == eObjectType.Thrown
                ? new DbItemUnique(BotRangedCombat.CreateThrowingWeapon(realm, characterClass, level))
                : damage == 0
                ? new GeneratedUniqueItem(realm, characterClass, level, type, slot, level == 50 ? 35 : 15)
                : new GeneratedUniqueItem(false, realm, characterClass, level, type, slot, (eDamageType)damage, level == 50 ? 35 : 15);
            // Some ROG weapon models change a one-hand item's template slot.
            // The requested loadout slot, not that cosmetic roll, is authoritative.
            item.Item_Type = (int)slot;
            // Only these newly created temporary items are normalized. Never
            // rewrite a shared template, an instrument subtype, or earned loot.
            if (BotWeaponStats.NeedsCompanionDamageStats(type))
            {
                item.DPS_AF = Math.Max(item.DPS_AF, BotWeaponStats.NormalDps(level));
                item.Quality = Math.Max(item.Quality, 85);
            }
            if (slot == eInventorySlot.TwoHandWeapon) item.Hand = 1;
            else if (slot == eInventorySlot.LeftHandWeapon && type != eObjectType.Shield) item.Hand = 2;
            else if (slot == eInventorySlot.RightHandWeapon) item.Hand = 0;
            if (level == 50) item.Quality = 99;
            item.IsDropable = false;
            item.IsTradable = false;
            return item;
        }

        // Templates marked for this realm but built on another realm's model (a
        // Midgard hat on the Hibernia mesh has no Valkyn or Troll fit). Bots skip them.
        private static IList<DbItemTemplate> WithoutCrossRealmLooks(IList<DbItemTemplate> items) =>
            items.Where(item => !BotCrossRealmGear.IsExcluded(item) &&
                                !DOL.GS.Quests.Hibernia.SluaghbinderEpicArmor.IsSetItem(item)).ToList();

        public static DbItemTemplate SelectCompanionWeapon(IEnumerable<DbItemTemplate> candidates, byte level,
            eRealm realm, eCharacterClass characterClass, eObjectType type, eInventorySlot slot,
            eWeaponDamageType damage = 0, int shieldSize = 0)
        {
            // Select exact level first, then -1, then -2. A failed database lookup
            // generates a temporary item, never an empty hand or an overlevel item.
            var eligible = candidates.Where(item => item != null && !BotCrossRealmGear.IsExcluded(item) &&
                !DOL.GS.Quests.Hibernia.SluaghbinderEpicArmor.IsSetItem(item) && item.MaxCount == 1 &&
                BotWeaponStats.IsNormalCompanionWeapon(item) &&
                item.Level >= Math.Max(1, level - 2) && item.Level <= level &&
                item.Realm == (int)realm && item.Object_Type == (int)type && item.IsPickable &&
                (!BotRangedCombat.IsRangedWeaponType(type) || BotRangedCombat.IsUsableTemplate(item)) &&
                item.LevelRequirement <= level &&
                (string.IsNullOrWhiteSpace(item.AllowedClasses) ||
                 Util.SplitCSV(item.AllowedClasses, true).Contains(((int)characterClass).ToString())) &&
                (slot == eInventorySlot.RightHandWeapon
                    ? (item.Item_Type is Slot.RIGHTHAND or Slot.LEFTHAND) && item.Hand != 1 &&
                      (characterClass != eCharacterClass.Savage || item.Hand != 2)
                    : item.Item_Type == (int)slot) &&
                (damage == 0 || item.Type_Damage == (int)damage) &&
                (shieldSize == 0 || item.Type_Damage == shieldSize)).ToList();
            DbItemTemplate result;
            if (eligible.Count > 0)
            {
                int closestLevel = eligible.Max(item => item.Level);
                var closest = eligible.Where(item => item.Level == closestLevel).ToArray();
                result = new DbItemUnique(closest[Util.Random(closest.Length - 1)]);
                result.IsDropable = false;
                result.IsTradable = false;
            }
            else
                result = CreateCompanionItem(realm, characterClass, level, type, slot, damage);
            if (shieldSize > 0) result.Type_Damage = shieldSize;
            return result;
        }

        private static void EquipCompanionWeapon(GameBot bot, eObjectType type, eInventorySlot slot,
            eWeaponDamageType damage = 0, int shieldSize = 0)
        {
            if (type == 0) return; // Unused secondary weapon type, not a real slot.
            if (bot.Inventory.GetItem(slot) != null)
                return; // Repeated spec requests cannot overwrite dual-wield gear or fail an occupied slot.
            var candidates = bot.IsEndgameCompanion ? Array.Empty<DbItemTemplate>() :
                GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(Math.Max(1, bot.Level - 2))
                    .And(DB.Column("Level").IsLessOrEqualTo(bot.Level))
                    .And(DB.Column("Realm").IsEqualTo((int)bot.Realm))
                    .And(DB.Column("Object_Type").IsEqualTo((int)type))).ToArray();
            var item = SelectCompanionWeapon(candidates, bot.Level, bot.Realm,
                (eCharacterClass)bot.CharacterClass.ID, type, slot, damage, shieldSize);
            if (!bot.Inventory.AddItem(slot, GameInventoryItem.Create(item)))
                throw new InvalidOperationException($"Could not equip temporary companion {bot.Name}: {slot} ({type}).");
        }

        public static void SetWeaponROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType, eInventorySlot slot, eDamageType damageType)
        {
            DbItemTemplate itemToCreate = living is GameBot { IsTemporaryGroupHelper: true }
                ? CreateCompanionItem(realm, charClass, level, objectType, slot, (eWeaponDamageType)damageType)
                : new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot, damageType);
            PrepareEndgameItem(living as IGamePlayer, itemToCreate);
            GameInventoryItem item = GameInventoryItem.Create(itemToCreate);
            living.Inventory.AddItem(slot, item);
        }

        public static void SetArmorROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType)
        {
            for (int i = Slot.HELM; i <= Slot.ARMS; i++)
            {
                if (i == Slot.JEWELRY || i == Slot.CLOAK)
                    continue;

                eInventorySlot slot = (eInventorySlot)i;
                if (!ShouldEquipRegularSlot(living as IGamePlayer))
                    continue;
                DbItemTemplate itemToCreate = living is GameBot { IsEndgameCompanion: true }
                    ? CreateCompanionItem(realm, charClass, level, objectType, slot)
                    : new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot);
                PrepareEndgameItem(living as IGamePlayer, itemToCreate);
                GameInventoryItem item = GameInventoryItem.Create(itemToCreate);
                living.Inventory.AddItem(slot, item);
            }
        }

        public static void SetJewelryROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType)
        {
            for (int i = Slot.JEWELRY; i <= Slot.RIGHTRING; i++)
            {
                if (i is Slot.TORSO or Slot.LEGS or Slot.ARMS or Slot.FOREARMS or Slot.SHIELD)
                    continue;

                eInventorySlot slot = (eInventorySlot)i;
                if (!ShouldEquipRegularSlot(living as IGamePlayer))
                    continue;
                DbItemTemplate itemToCreate = living is GameBot { IsEndgameCompanion: true }
                    ? CreateCompanionItem(realm, charClass, level, objectType, slot)
                    : new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot);
                PrepareEndgameItem(living as IGamePlayer, itemToCreate);
                GameInventoryItem item = GameInventoryItem.Create(itemToCreate);

                if (i == Slot.RIGHTRING || i == Slot.LEFTRING)
                    living.Inventory.AddItem(living.Inventory.FindFirstEmptySlot(eInventorySlot.LeftRing, eInventorySlot.RightRing), item);
                else if (i == Slot.LEFTWRIST || i == Slot.RIGHTWRIST)
                    living.Inventory.AddItem(living.Inventory.FindFirstEmptySlot(eInventorySlot.LeftBracer, eInventorySlot.RightBracer), item);
                else
                    living.Inventory.AddItem(slot, item);
            }
        }

        public static void SetInstrumentROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType, eInventorySlot slot, eInstrumentType instrumentType)
        {
            DbItemTemplate itemToCreate = new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot);
            itemToCreate.DPS_AF = (int)instrumentType;
            itemToCreate.Model = BotStarterInstruments.ModelFor(instrumentType);
            itemToCreate.Name = instrumentType.ToString();
            PrepareEndgameItem(living as IGamePlayer, itemToCreate);
            GameInventoryItem item = GameInventoryItem.Create(itemToCreate);
            living.Inventory.AddItem(slot, item);
        }

        public static void SetMeleeWeapon(IGamePlayer player, eObjectType weapType, eHand hand, eWeaponDamageType damageType = 0)
        {
            if (player is GameBot { IsTemporaryGroupHelper: true } companion)
            {
                if (weapType == 0) return;
                eInventorySlot slot = hand == eHand.twoHand ? eInventorySlot.TwoHandWeapon :
                    hand == eHand.leftHand ? eInventorySlot.LeftHandWeapon : eInventorySlot.RightHandWeapon;
                EquipCompanionWeapon(companion, weapType, slot, damageType);
                return;
            }
            int min = MinimumEquipmentLevel(player, Math.Max(1, player.Level - 6));
            int max = MaximumEquipmentLevel(player, Math.Min(51, player.Level + 4));

            IList<DbItemTemplate> itemList;
            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)weapType).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1)))));
            itemList = WithoutCrossRealmLooks(itemList);

            if (itemList.Count != 0)
            {
                List<DbItemTemplate> itemsToKeep = new List<DbItemTemplate>();

                foreach (DbItemTemplate item in itemList)
                {
                    if (player is GameBot && !BotWeaponStats.HasFunctionalMeleeStats(item))
                        continue;
                    bool shouldAddItem = false;

                    switch (hand)
                    {
                        case eHand.oneHand:
                            shouldAddItem = item.Item_Type == Slot.RIGHTHAND || item.Item_Type == Slot.LEFTHAND;
                            break;
                        case eHand.leftHand:
                            shouldAddItem = item.Item_Type == Slot.LEFTHAND;
                            break;
                        case eHand.twoHand:
                            shouldAddItem = item.Item_Type == Slot.TWOHAND && (damageType == 0 || item.Type_Damage == (int)damageType);
                            break;
                    }

                    if (shouldAddItem)
                        itemsToKeep.Add(item);
                }

                if (itemsToKeep.Count != 0)
                {
                    DbItemTemplate itemTemplate = itemsToKeep[Util.Random(itemsToKeep.Count - 1)];
                    AddItem(player, itemTemplate, hand);
                }
            }
            else
                log.Info("No melee weapon found for " + player.Name);
        }

        public static void SetRangedWeapon(IGamePlayer player, eObjectType weapType)
        {
            if (player.Inventory.GetItem(eInventorySlot.DistanceWeapon) != null) return;
            if (player is GameBot { IsTemporaryGroupHelper: true } companion)
            {
                EquipCompanionWeapon(companion, weapType, eInventorySlot.DistanceWeapon);
                return;
            }
            int min = MinimumEquipmentLevel(player, Math.Max(1, player.Level - 6));
            int max = MaximumEquipmentLevel(player, Math.Min(51, player.Level + 3));

            IList<DbItemTemplate> itemList;
            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)weapType).And(
                                                                       DB.Column("Item_Type").IsEqualTo(13).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1))))));
            itemList = WithoutCrossRealmLooks(itemList);

            itemList = itemList.Where(BotRangedCombat.IsUsableTemplate).ToList();
            if (itemList.Count != 0)
            {
                DbItemTemplate itemTemplate = itemList[Util.Random(itemList.Count - 1)];
                AddItem(player, itemTemplate);
            }
            else
                log.Info("No ranged weapon found for " + player.Name);
        }

        public static void SetShield(IGamePlayer player, int shieldSize)
        {
            if (shieldSize < 1)
                return;

            if (player is GameBot { IsTemporaryGroupHelper: true } companion)
            {
                EquipCompanionWeapon(companion, eObjectType.Shield, eInventorySlot.LeftHandWeapon, shieldSize: shieldSize);
                return;
            }

            int min = MinimumEquipmentLevel(player, Math.Max(1, player.Level - 6));
            int max = MaximumEquipmentLevel(player, Math.Min(51, player.Level + 3));

            IList<DbItemTemplate> itemList;
            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)eObjectType.Shield).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("Type_Damage").IsEqualTo(shieldSize).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1))))));
            itemList = WithoutCrossRealmLooks(itemList);

            if (itemList.Count != 0)
            {
                DbItemTemplate itemTemplate = itemList[Util.Random(itemList.Count - 1)];
                AddItem(player, itemTemplate);
            }
            else
                log.Info("No shield found for " + player.Name);
        }

        public static void SetArmor(IGamePlayer player, eObjectType armorType)
        {
            if (player is GameBot { IsEndgameCompanion: true } bot)
            {
                SetArmorROG(bot, bot.Realm, (eCharacterClass)bot.CharacterClass.ID, 50, armorType);
                return;
            }
            int min = MinimumEquipmentLevel(player, Math.Max(1, player.Level - 6));
            int max = MaximumEquipmentLevel(player, Math.Min(51, player.Level + 3));

            IList<DbItemTemplate> itemList;
            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)armorType).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1)))));
            itemList = WithoutCrossRealmLooks(itemList);

            if (itemList.Count != 0)
            {
                Dictionary<int, List<DbItemTemplate>> armorSlots = new Dictionary<int, List<DbItemTemplate>>();

                foreach (DbItemTemplate template in itemList)
                {
                    if (!armorSlots.TryGetValue(template.Item_Type, out List<DbItemTemplate> slotList))
                    {
                        slotList = new List<DbItemTemplate>();
                        armorSlots[template.Item_Type] = slotList;
                    }

                    slotList.Add(template);
                }

                foreach (var pair in armorSlots)
                {
                    if (pair.Value.Count != 0 && ShouldEquipRegularSlot(player))
                    {
                        DbItemTemplate itemTemplate = pair.Value[Util.Random(pair.Value.Count - 1)];
                        AddItem(player, itemTemplate);
                    }
                }
            }
            else
                log.Info("No armor found for " + player.Name);

            if (player is GameBot { IsTemporaryGroupHelper: true } companion)
            {
                // Sparse low-level template tables must not leave armor holes.
                for (int i = Slot.HELM; i <= Slot.ARMS; i++)
                {
                    if (i is Slot.JEWELRY or Slot.CLOAK) continue;
                    var slot = (eInventorySlot)i;
                    if (companion.Inventory.GetItem(slot) == null)
                        companion.Inventory.AddItem(slot, GameInventoryItem.Create(CreateCompanionItem(
                            companion.Realm, (eCharacterClass)companion.CharacterClass.ID, companion.Level, armorType, slot)));
                }
            }
        }

        public static void SetInstrument(IGamePlayer player, eObjectType weapType, eInventorySlot slot, eInstrumentType instrumentType)
        {
            int min = MinimumEquipmentLevel(player, Math.Max(1, player.Level - 6));
            int max = MaximumEquipmentLevel(player, Math.Min(51, player.Level + 3));

            IList<DbItemTemplate> itemList;
            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)weapType).And(
                                                                       DB.Column("DPS_AF").IsEqualTo((int)instrumentType).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1))))));
            itemList = WithoutCrossRealmLooks(itemList);

            if (itemList.Count != 0)
            {
                DbItemTemplate itemTemplate = itemList[Util.Random(itemList.Count - 1)];
                DbInventoryItem item = GameInventoryItem.Create(itemTemplate);
                player.Inventory.AddItem(slot, item);
            }
            else
                log.Info("No instrument found for " + player.Name);
        }

        public static void SetJewelry(IGamePlayer player)
        {
            int min = MinimumEquipmentLevel(player, Math.Max(1, player.Level - 30));
            int max = MaximumEquipmentLevel(player, Math.Min(51, player.Level + 3));

            IList<DbItemTemplate> itemList;
            List<DbItemTemplate> cloakList = new List<DbItemTemplate>();
            List<DbItemTemplate> jewelryList = new List<DbItemTemplate>();
            List<DbItemTemplate> ringList = new List<DbItemTemplate>();
            List<DbItemTemplate> wristList = new List<DbItemTemplate>();
            List<DbItemTemplate> neckList = new List<DbItemTemplate>();
            List<DbItemTemplate> waistList = new List<DbItemTemplate>();

            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)eObjectType.Magical).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1)))));
            itemList = WithoutCrossRealmLooks(itemList);

            if (itemList.Count != 0)
            {
                foreach (DbItemTemplate template in itemList)
                {
                    if (template.Item_Type == Slot.CLOAK)
                    {
                        template.Color = Util.Random((Enum.GetValues(typeof(eColor)).Length));
                        cloakList.Add(template);
                    }
                    else if (template.Item_Type == Slot.JEWELRY)
                        jewelryList.Add(template);
                    else if (template.Item_Type == Slot.LEFTRING || template.Item_Type == Slot.RIGHTRING)
                        ringList.Add(template);
                    else if (template.Item_Type == Slot.LEFTWRIST || template.Item_Type == Slot.RIGHTWRIST)
                        wristList.Add(template);
                    else if (template.Item_Type == Slot.NECK)
                        neckList.Add(template);
                    else if (template.Item_Type == Slot.WAIST)
                        waistList.Add(template);
                }

                List<List<DbItemTemplate>> masterList = new List<List<DbItemTemplate>>
                {
                    cloakList,
                    jewelryList,
                    neckList,
                    waistList
                };

                foreach (List<DbItemTemplate> list in masterList)
                {
                    if (list.Count != 0)
                    {
                        DbItemTemplate itemTemplate = list[Util.Random(list.Count - 1)];
                        AddItem(player, itemTemplate);
                    }
                }

                for (int i = 0; i < 2; i++)
                {
                    if (ringList.Count != 0)
                    {
                        DbItemTemplate itemTemplate = ringList[Util.Random(ringList.Count - 1)];
                        AddItem(player, itemTemplate);
                    }

                    if (wristList.Count != 0)
                    {
                        DbItemTemplate itemTemplate = wristList[Util.Random(wristList.Count - 1)];
                        AddItem(player, itemTemplate);
                    }
                }

                if (player.Inventory.GetItem(eInventorySlot.Cloak) == null)
                {
                    DbItemTemplate cloak = GameServer.Database.FindObjectByKey<DbItemTemplate>("cloak");

                    if (cloak != null)
                    {
                        cloak.Color = Util.Random((Enum.GetValues(typeof(eColor)).Length));
                        AddItem(player, cloak);
                    }
                }
            }
            else
                log.Info("No jewelry of any kind found for " + player.Name);
        }

        private static void AddItem(IGamePlayer player, DbItemTemplate itemTemplate, eHand hand = eHand.None)
        {
            if (itemTemplate == null)
            {
                log.Info("itemTemplate in AddItem is null");
                return;
            }

            DbInventoryItem item = GameInventoryItem.Create(itemTemplate);

            if (item != null)
            {
                if (item.Item_Type == Slot.LEFTRING || item.Item_Type == Slot.RIGHTRING)
                {
                    player.Inventory.AddItem(player.Inventory.FindFirstEmptySlot(eInventorySlot.LeftRing, eInventorySlot.RightRing), item);
                    return;
                }
                else if (item.Item_Type == Slot.LEFTWRIST || item.Item_Type == Slot.RIGHTWRIST)
                {
                    player.Inventory.AddItem(player.Inventory.FindFirstEmptySlot(eInventorySlot.LeftBracer, eInventorySlot.RightBracer), item);
                    return;
                }
                else if (item.Item_Type == Slot.LEFTHAND && item.Object_Type != (int)eObjectType.Shield && hand == eHand.oneHand)
                {
                    player.Inventory.AddItem(eInventorySlot.RightHandWeapon, item);
                    return;
                }
                else
                {
                    if (item.Object_Type == (int)eObjectType.Shield &&
                        (player.CharacterClass.ID == (int)eCharacterClass.Infiltrator ||
                        player.CharacterClass.ID == (int)eCharacterClass.Mercenary ||
                        player.CharacterClass.ID == (int)eCharacterClass.Nightshade ||
                        player.CharacterClass.ID == (int)eCharacterClass.Ranger ||
                        player.CharacterClass.ID == (int)eCharacterClass.Blademaster ||
                        player.CharacterClass.ID == (int)eCharacterClass.Shadowblade ||
                        player.CharacterClass.ID == (int)eCharacterClass.Berserker ||
                        player.CharacterClass.ID == (int)eCharacterClass.Savage))
                    {
                        player.Inventory.AddItem(player.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack), item);
                    }
                    else
                        player.Inventory.AddItem((eInventorySlot)item.Item_Type, item);
                }
            }
            else
                log.Info("Item failed to be created for " + player.Name);
        }
    }
}

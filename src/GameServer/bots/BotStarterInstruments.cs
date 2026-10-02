using System;
using System.Linq;
using DOL.Database;

namespace DOL.GS
{
    public static class BotStarterInstruments
    {
        public static bool Applies(eCharacterClass characterClass) =>
            characterClass is eCharacterClass.Bard or eCharacterClass.Minstrel;

        public static int ModelFor(eInstrumentType type) => type switch
        {
            eInstrumentType.Drum => 228,
            eInstrumentType.Lute => 227,
            eInstrumentType.Flute => 325,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        public static bool Ensure(GameBot bot)
        {
            if (bot?.Inventory == null || bot.CharacterClass == null ||
                !Applies((eCharacterClass)bot.CharacterClass.ID)) return false;
            bool added = false;
            foreach (eInstrumentType type in new[] { eInstrumentType.Drum, eInstrumentType.Flute, eInstrumentType.Lute })
            {
                if (bot.Inventory.AllItems.Any(item => item.Object_Type == (int)eObjectType.Instrument &&
                    item.DPS_AF == (int)type && item.Model > 0 &&
                    (item.SlotPosition == Slot.RANGED || item.SlotPosition == Slot.TWOHAND ||
                     item.SlotPosition >= (int)eInventorySlot.FirstBackpack && item.SlotPosition <= (int)eInventorySlot.LastBackpack))) continue;

                eInventorySlot preferred = type == eInstrumentType.Drum ? eInventorySlot.DistanceWeapon : eInventorySlot.TwoHandWeapon;
                eInventorySlot alternate = preferred == eInventorySlot.DistanceWeapon ? eInventorySlot.TwoHandWeapon : eInventorySlot.DistanceWeapon;
                eInventorySlot slot = bot.Inventory.GetItem(preferred) == null ? preferred :
                    bot.Inventory.GetItem(alternate) == null ? alternate :
                    bot.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);
                if (slot == eInventorySlot.Invalid) continue; // Never overwrite earned loot in a full bag.
                var template = new DbItemUnique(new DbItemTemplate
                {
                    Id_nb = $"bot_starter_instrument_{bot.Realm}_{type}", Name = $"training {type.ToString().ToLowerInvariant()}",
                    Object_Type = (int)eObjectType.Instrument, Item_Type = Slot.TWOHAND,
                    DPS_AF = (int)type, Model = ModelFor(type), Level = 1, LevelRequirement = 1,
                    Realm = (int)bot.Realm, Quality = 100, Condition = 50000, MaxCondition = 50000,
                    Durability = 50000, MaxDurability = 50000, MaxCount = 1,
                    IsPickable = true, IsDropable = false, IsTradable = false,
                });
                var item = GameInventoryItem.Create(template);
                item.Creator = nameof(BotStarterInstruments);
                added |= bot.Inventory.AddItem(slot, item);
            }
            return added;
        }
    }
}

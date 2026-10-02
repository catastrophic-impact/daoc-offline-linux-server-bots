using System.Collections.Generic;
using DOL.Database;

namespace DOL.GS
{
    // Client presentation only. These items never enter an actor's combat inventory.
    public static class NecromancerPetAppearance
    {
        public const ushort HeroSwordModel = 4808; // Private client asset; never assigned to ordinary items.
        public const ushort ServantMaceModel = 4822; // Private blackened copy of the Bone Patroller hammer (stock 3466).
        public const ushort ServantShieldModel = 4823; // Private rotten-wood copy of the grave shield (stock 1128).
        private static readonly ICollection<DbInventoryItem> ServantEquipment = Build(false);
        private static readonly ICollection<DbInventoryItem> HeroEquipment = Build(true);

        private static ICollection<DbInventoryItem> Build(bool hero)
        {
            var equipment = new GameNpcInventoryTemplate();
            if (hero)
                equipment.AddNPCEquipment(eInventorySlot.TwoHandWeapon, HeroSwordModel, 0, 22); // client 2H orange flames
            else
            {
                equipment.AddNPCEquipment(eInventorySlot.RightHandWeapon, ServantMaceModel);
                equipment.AddNPCEquipment(eInventorySlot.LeftHandWeapon, ServantShieldModel);
            }
            return equipment.VisibleItems;
        }

        private static int Kind(GameObject actor) => actor is NecromancerPet pet ? pet.AppearanceTemplateId : 0;

        public static bool HasEquipment(GameLiving actor) =>
            Kind(actor) is 204 or 206 || actor.Inventory != null;

        public static ICollection<DbInventoryItem> Equipment(GameLiving actor) => Kind(actor) switch
        {
            204 => ServantEquipment,
            206 => HeroEquipment,
            _ => actor.Inventory?.VisibleItems
        };

        public static byte WeaponSlots(GameLiving actor) => Kind(actor) switch
        {
            204 => 0x10, // right hand and shield
            206 => 0x22, // two-handed weapon in both hands
            _ => actor.VisibleActiveWeaponSlots
        };

        public static void CombatModels(GameObject attacker, GameObject defender, byte result,
            ref ushort weapon, ref ushort defenseWeapon)
        {
            switch (Kind(attacker))
            {
                case 204: weapon = ServantMaceModel; break;
                case 206: weapon = HeroSwordModel; break;
            }
            // Only decorate actual parry/block results; never create a defense.
            if (result == 2 && Kind(defender) == 204)
                defenseWeapon = ServantShieldModel;
            else if (result == 1)
            {
                if (Kind(defender) == 204) defenseWeapon = ServantMaceModel;
                else if (Kind(defender) == 206) defenseWeapon = HeroSwordModel;
            }
        }
    }
}

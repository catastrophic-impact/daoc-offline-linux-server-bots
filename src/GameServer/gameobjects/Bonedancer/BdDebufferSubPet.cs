namespace DOL.GS
{
    public class BdDebufferSubPet : BdSubPet
    {
        public BdDebufferSubPet(INpcTemplate npcTemplate) : base(npcTemplate) { }

        public override void InitializeActiveWeaponFromInventory()
        {
            // Same two-handed bone mace a commander can roll. The template's
            // one-handed bone sword is removed so only the mace shows.
            if (Inventory?.GetItem(eInventorySlot.RightHandWeapon) is DOL.Database.DbInventoryItem oneHand)
                Inventory.RemoveItem(oneHand);
            MinionGetWeapon(CommanderPet.eWeaponType.TwoHandHammer);
        }
    }
}

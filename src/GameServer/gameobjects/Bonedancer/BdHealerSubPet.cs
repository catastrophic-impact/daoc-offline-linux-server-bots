namespace DOL.GS
{
    public class BdHealerSubPet : BdSubPet
    {
        public BdHealerSubPet(INpcTemplate npcTemplate) : base(npcTemplate) { }

        public override void InitializeActiveWeaponFromInventory()
        {
            // Bone healers are unarmed spellcasters in the classic/SI models.
        }
    }
}

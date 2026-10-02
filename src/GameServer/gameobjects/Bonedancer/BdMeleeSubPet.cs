namespace DOL.GS
{
    public class BdMeleeSubPet : BdSubPet
    {
        public BdMeleeSubPet(INpcTemplate npcTemplate) : base(npcTemplate) { }

        public override void InitializeActiveWeaponFromInventory()
        {
            MinionGetWeapon((CommanderPet.eWeaponType)Util.Random(
                (int)CommanderPet.eWeaponType.OneHandAxe,
                (int)CommanderPet.eWeaponType.OneHandSword));
        }
    }
}

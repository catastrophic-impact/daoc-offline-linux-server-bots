namespace DOL.GS
{
    public class BdBufferSubPet : BdSubPet
    {
        public BdBufferSubPet(INpcTemplate npcTemplate) : base(npcTemplate) { }

        public override void InitializeActiveWeaponFromInventory()
        {
            MinionGetShield(CommanderPet.eWeaponType.GuardianBuckler);
            MinionGetWeapon(CommanderPet.eWeaponType.OneHandHammer);
        }

        public override bool AddToWorld()
        {
            // Some summon templates do not have a database equipment template,
            // so the normal inventory callback may be skipped. Build the fixed
            // Bone Patroller loadout before its initial create packet instead of
            // relying on that optional callback.
            InitializeActiveWeaponFromInventory();
            return base.AddToWorld();
        }
    }
}

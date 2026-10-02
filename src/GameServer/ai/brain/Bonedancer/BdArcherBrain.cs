using DOL.GS;

namespace DOL.AI.Brain
{
    /// <summary>
    /// A brain that can be controlled
    /// </summary>
    public class BdArcherBrain : BdPetBrain
    {
        /// <summary>
        /// Constructs new controlled npc brain
        /// </summary>
        /// <param name="owner"></param>
        public BdArcherBrain(GameLiving owner) : base(owner) { }

        #region AI

        /// <summary>
        /// No Abilities or spells
        /// </summary>
        public override void CheckAbilities() { }
        public override bool CheckSpells(eCheckSpellType type) { return false; }

        public override void Attack(GameObject target)
        {
            SelectWeapon(target as GameLiving);

            base.Attack(target);
        }

        public override void Think()
        {
            SelectWeapon(m_orderAttackTarget as GameLiving ?? Body.TargetObject as GameLiving);
            base.Think();
        }

        private void SelectWeapon(GameLiving target)
        {
            if (target == null || !target.IsAlive)
                return;

            int meleeThreshold = Body.MeleeAttackRange + 64;
            eActiveWeaponSlot desired = Body.GetDistanceTo(target) <= meleeThreshold
                ? eActiveWeaponSlot.Standard
                : eActiveWeaponSlot.Distance;
            if (Body.ActiveWeaponSlot != desired)
                Body.SwitchWeapon(desired);
        }

        #endregion
    }
}

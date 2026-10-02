using DOL.AI.Brain;
using DOL.Database;

namespace DOL.GS
{
    public class TurretPet : GameSummonedPet
    {
        // Requested server balance: all Animist shrooms, regardless of owner.
        // Other summoned pets and the owner's own health are unchanged.
        public const double HealthMultiplier = 1.33;
        public override double MaxHealthScalingFactor => base.MaxHealthScalingFactor * HealthMultiplier;

        public Spell TurretSpell;

        public TurretPet(INpcTemplate template) : base(template) { }

        protected override void BuildAmbientTexts()
        {
            base.BuildAmbientTexts();

            if (ambientTexts.Count>0)
            {
                foreach (DbMobXAmbientBehavior ambientText in ambientTexts)
                    ambientText.Chance /= 5;
            }
        }

        public override void StartAttack(GameObject attackTarget)
        {
            if (attackTarget == null)
                return;

            if (attackTarget is GameLiving livingTarget && !GameServer.ServerRules.IsAllowedToAttack(this, livingTarget, true))
                return;

            if (Brain is IControlledBrain brain)
            {
                if (brain.AggressionState is eAggressionState.Passive)
                    return;
            }

            TargetObject = attackTarget;

            if (Brain is TurretMainPetTankBrain)
                attackComponent.RequestStartAttack();
        }

        public override void StartInterruptTimer(int duration, AttackData.eAttackType attackType, GameLiving attacker)
        {
            // Don't interrupt turrets (1.90 EU).
            return;
        }

        public override void OnCastSpellLosCheckFail(GameObject target)
        {
            base.OnCastSpellLosCheckFail(target);

            // This is where FnF turrets with LoS check on aggro enabled clear their list of hidden targets.
            // This can create a delay between attacks in some cases because turrets only ask one target at a time.
            if (Brain is TurretFNFBrain fnfBrain)
                fnfBrain.RemoveFromAggroList(target as GameLiving);
        }
    }
}

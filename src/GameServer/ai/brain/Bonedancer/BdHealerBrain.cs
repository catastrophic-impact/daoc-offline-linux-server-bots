using DOL.GS;
using DOL.GS.ServerProperties;
using System.Linq;

namespace DOL.AI.Brain
{
    public class BdHealerBrain : BdPetBrain
    {
        public BdHealerBrain(GameLiving owner) : base(owner)
        {
            AggroLevel = 0;
            AggroRange = 0;
        }

        public override eAggressionState AggressionState
        {
            get => eAggressionState.Passive;
            set { }
        }

        public override void Attack(GameObject target) { }

        public override void CheckAbilities() { }

        protected override GameLiving FindTargetForDefensiveSpell(Spell spell)
        {
            GameLiving target = null;
            int healThreshold = Properties.BONEDANCER_HEALER_PET_HEAL_THRESHOLD;

            switch (spell.SpellType)
            {
                #region Heals

                case eSpellType.Heal:
                {
                    // The complete companion/playerbot group is eligible, then
                    // the commander and party pet tree. Lowest health wins, so
                    // an injured party member cannot be starved by minor pet
                    // damage. Range/realm/region safety comes from the shared
                    // Bonedancer support roster.
                    target = BonedancerSupportTargets(spell, true)
                        .Where(living => living.HealthPercent < healThreshold)
                        .OrderBy(living => living.HealthPercent)
                        .ThenBy(living => living is GamePlayer or GameBot ? 0 : 1)
                        .FirstOrDefault();

                    break;
                }

                #endregion

                #region Buffs

                case eSpellType.HealthRegenBuff:
                {
                    // Starting a Realm buff on self lets the spell handler
                    // apply it to every reachable group member and pet at once.
                    if (!LivingHasEffect(Body, spell))
                    {
                        target = Body;
                        break;
                    }

                    target = BonedancerSupportTargets(spell, true)
                        .FirstOrDefault(living => !LivingHasEffect(living, spell));

                    break;
                }

                #endregion
            }

            return target;
        }

        public override void AddToAggroList(GameLiving living, long aggroAmount) { }

        public override void RemoveFromAggroList(GameLiving living) { }

        protected override GameLiving CalculateNextAttackTarget()
        {
            return null;
        }

        public override void AttackMostWanted() { }

        public override void OnOwnerAttacked(AttackData ad) { }
    }
}

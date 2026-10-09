using System.Collections.Generic;
using System.Linq;
using DOL.GS;

namespace DOL.AI.Brain
{
    /// <summary>
    /// Controlled brain used only by player-owned Sluaghbinder pets in the
    /// isolated new-class build.
    /// </summary>
    public sealed class SluaghbinderPetBrain : ControlledMobBrain
    {
        /// <summary>The priest's heal-over-time goes to allies below this health.</summary>
        public const int PriestHealOverTimeThreshold = 75;

        /// <summary>The priest's direct heal goes to allies below this health.</summary>
        public const int PriestDirectHealThreshold = 50;

        public SluaghbinderPetBrain(GameLiving owner) : base(owner) { }

        private bool IsZombiePriest =>
            DOL.GS.SluaghbinderPet.IsHealerRole(Body?.NPCTemplate?.Name);

        /// <summary>
        /// The priest remains a normal controlled melee/spell pet, but checks
        /// its defensive spell list before committing to another attack tick.
        /// A valid heal or upkeep cast therefore interrupts melee naturally;
        /// when nobody needs help, the base pet brain attacks as usual.
        /// </summary>
        public override void AttackMostWanted()
        {
            if (IsZombiePriest && Body.IsAlive && CheckSpells(eCheckSpellType.Defensive))
            {
                Body.StopAttack();
                return;
            }

            base.AttackMostWanted();
        }

        /// <summary>
        /// Like the Bonedancer support minions: while the priest's heal is
        /// queued, waiting for LoS or on its cast bar, report "casting" so the
        /// brain neither resubmits it every Think tick nor resumes melee.
        /// </summary>
        public override bool CheckSpells(eCheckSpellType type)
        {
            if (IsZombiePriest &&
                (Body?.IsCasting == true || Body?.castingComponent?.HasPendingSkillRequests == true ||
                 Body?.castingComponent is NpcCastingComponent { HasPendingLosCheckRequests: true }))
                return true;

            return base.CheckSpells(type);
        }

        /// <summary>
        /// Priest support casts are already limited to living allies in range.
        /// An autonomous gamebot owner has no client to answer an NPC LoS
        /// request, so (as for Bonedancer support minions) they use the
        /// server-side target and range checks only.
        /// </summary>
        protected override bool ShouldCheckLosForDefensiveSpell(Spell spell, GameLiving target) => !IsZombiePriest;

        /// <summary>
        /// Zombie Priest heal targeting. Both heals are single target: the
        /// heal-over-time goes to the most injured ally below 75% health, the
        /// direct heal to the most injured ally below 50%. Allies are the owner,
        /// the priest, every party member and every party member's pets.
        ///
        /// The base brain picks at random among every castable defensive spell,
        /// so priority is enforced here: while anyone is below 50% only the
        /// direct heal is offered; otherwise the heal-over-time; her buffs are
        /// offered only when no heal is needed. The buffs themselves are unchanged.
        /// </summary>
        protected override GameLiving FindTargetForDefensiveSpell(Spell spell)
        {
            if (!IsZombiePriest)
                return base.FindTargetForDefensiveSpell(spell);

            Spell directHeal = ReadyHeal(eSpellType.Heal);
            Spell healOverTime = ReadyHeal(eSpellType.HealOverTime);
            GameLiving directHealTarget = directHeal == null ? null : DirectHealTarget(directHeal);

            // Compare by ID: below owner level 50 the castable lists hold
            // level-scaled copies of the template spells.
            switch (spell.SpellType)
            {
                case eSpellType.Heal:
                    return spell.ID == directHeal?.ID ? directHealTarget : null;
                case eSpellType.HealOverTime:
                    if (directHealTarget != null || spell.ID != healOverTime?.ID)
                        return null;
                    return HealOverTimeTarget(spell);
                default:
                    if (directHealTarget != null || (healOverTime != null && HealOverTimeTarget(healOverTime) != null))
                        return null;
                    return base.FindTargetForDefensiveSpell(spell);
            }
        }

        private GameLiving DirectHealTarget(Spell heal) =>
            MostInjured(heal, PriestDirectHealThreshold, _ => true);

        private GameLiving HealOverTimeTarget(Spell hot) =>
            MostInjured(hot, PriestHealOverTimeThreshold, living => !LivingHasEffect(living, hot));

        /// <summary>The priest's castable (level-scaled) single-target heal of this type, if off cooldown.</summary>
        private Spell ReadyHeal(eSpellType type) =>
            (Body.InstantHealSpells ?? []).Concat(Body.HealSpells ?? [])
                .FirstOrDefault(s => s != null && s.SpellType == type && s.Target is not eSpellTarget.SELF &&
                                     !(s.HasRecastDelay && Body.GetSkillDisabledDuration(s) > 0));

        private GameLiving MostInjured(Spell spell, int belowPercent, System.Func<GameLiving, bool> eligible)
        {
            int range = spell.CalculateEffectiveRange(Body);
            return HealCandidates()
                .Where(living => living.IsAlive && living.ObjectState == GameObject.eObjectState.Active &&
                                 living.HealthPercent < belowPercent &&
                                 (living == Body || Body.IsWithinRadius(living, range)) &&
                                 eligible(living))
                .OrderBy(living => living.HealthPercent)
                .FirstOrDefault();
        }

        private IEnumerable<GameLiving> HealCandidates()
        {
            var seen = new HashSet<GameLiving>();
            GameLiving owner = (this as IControlledBrain).Owner;

            IEnumerable<GameLiving> members = owner?.Group != null
                ? owner.Group.GetMembersInTheGroup()
                : owner != null ? [owner] : [];

            foreach (GameLiving member in members.Append(Body))
            {
                if (member == null || !seen.Add(member))
                    continue;
                yield return member;

                foreach (GameLiving pet in PetsOf(member))
                {
                    if (seen.Add(pet))
                        yield return pet;
                }
            }
        }

        private static IEnumerable<GameLiving> PetsOf(GameLiving living)
        {
            if (living?.ControlledBrain?.Body is GameLiving pet)
                yield return pet;

            // Commanders (Bonedancer and similar) keep their minions in a list.
            if (living?.ControlledBrain?.Body is GameNPC commander && commander.ControlledNpcList != null)
            {
                foreach (IControlledBrain minion in commander.ControlledNpcList)
                {
                    if (minion?.Body != null)
                        yield return minion.Body;
                }
            }
        }
    }
}

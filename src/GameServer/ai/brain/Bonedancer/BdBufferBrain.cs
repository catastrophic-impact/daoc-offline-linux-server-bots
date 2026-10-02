using DOL.GS;

namespace DOL.AI.Brain
{
    public class BdBufferBrain : BdPetBrain
    {
        public BdBufferBrain(GameLiving owner) : base(owner) { }

        public override void Think()
        {
            // A queued request and an active cast are both complete AI turns.
            // Calling StopAttack after every such turn used to repeatedly
            // disturb the Patroller while its cast animation was in progress.
            if (Body?.IsCasting == true || Body?.castingComponent?.HasPendingSkillRequests == true ||
                Body?.castingComponent is NpcCastingComponent { HasPendingLosCheckRequests: true })
                return;

            // Leave melee before submitting the support cast, never after the
            // asynchronous request has started.
            if (Body?.attackComponent?.AttackState == true)
                Body.StopAttack();

            if (!base.CheckSpells(eCheckSpellType.Defensive))
                base.Think();
        }
        public override void CheckAbilities() { }

        protected override GameLiving FindTargetForDefensiveSpell(Spell spell)
        {
            switch (spell.SpellType)
            {
                case eSpellType.CombatSpeedBuff:
                case eSpellType.Bladeturn:
                {
                    // Realm buffs begin on the subpet so the spell handler can
                    // expand the cast to the Bonedancer's local party. Self-only
                    // bladeturn never leaves the buffer.
                    if (!LivingHasEffect(Body, spell))
                        return Body;

                    if (spell.Target == eSpellTarget.SELF)
                        return null;

                    // Only reachable, same-region targets are considered. This
                    // removes the old infinite retry/follow loop when an owner
                    // or group member was outside the 1500-unit buff range.
                    foreach (GameLiving living in BonedancerSupportTargets(spell, true))
                        if (!LivingHasEffect(living, spell))
                            return living;
                    return null;
                }
                case eSpellType.DamageShield:
                {
                    // Shards of Bone is deliberately narrower than the realm
                    // speed buff. Buff only this Patroller, its commander and
                    // the commander's sibling subpets.
                    foreach (GameLiving living in BonedancerCommanderTreeTargets(spell))
                        if (!LivingHasEffect(living, spell))
                            return living;

                    return null;
                }
            }

            return null;
        }
    }
}

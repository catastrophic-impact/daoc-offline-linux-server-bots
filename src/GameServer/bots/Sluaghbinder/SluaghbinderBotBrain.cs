using DOL.GS;

namespace DOL.AI.Brain
{
    /// <summary>
    /// Dedicated autonomous/companion combat brain for Sluaghbinders.  It
    /// inherits the stable BotBrain movement, recovery, group support and
    /// spell-casting machinery, but owns the Sluaghbinder-specific pet handoff
    /// and service-spell exclusion locally rather than sharing a class brain.
    /// </summary>
    public sealed class SluaghbinderBotBrain : BotBrain
    {
        public override void Think()
        {
            SluaghbinderBotPolicy.RemovePlayerOnlyServiceSpells(BotBody);
            base.Think();
        }

        public override void AttackMostWanted()
        {
            SluaghbinderBotPolicy.RemovePlayerOnlyServiceSpells(BotBody);

            // Keep the raised dead on the same valid target as its owner.  The
            // normal AutonomousPetSupport handoff still controls buffs/heals,
            // but this direct order closes the gap for newly acquired targets
            // and for player-led /pull or pet-led combat.
            if (BotBody?.ControlledBrain is ControlledMobBrain pet &&
                SluaghbinderBotPolicy.IsValidCombatTarget(BotBody, BotBody.TargetObject as GameLiving))
            {
                GameLiving target = BotBody.TargetObject as GameLiving;
                if (pet.OrderedAttackTarget != target)
                    pet.Attack(target);
            }

            base.AttackMostWanted();
        }
    }
}

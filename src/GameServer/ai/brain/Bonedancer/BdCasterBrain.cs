using System.Linq;
using DOL.GS;

namespace DOL.AI.Brain
{
    public class BdCasterBrain : BdPetBrain
    {
        public BdCasterBrain(GameLiving owner) : base(owner) { }

        public override void CheckAbilities() { }

        public override bool CheckSpells(eCheckSpellType type)
        {
            // Hand off cleanly from attack-follow movement to a stationary cast.
            // SpellHandler also enforces stationary casting, but waiting until its
            // next service tick lets the commander reissue formation movement in
            // the request-to-cast gap. Stop here before enqueueing the cast.
            if (type == eCheckSpellType.Offensive && Body?.IsMoving == true &&
                Body.TargetObject is GameLiving target &&
                Body.HarmfulSpells?.Any(spell =>
                    target.IsAlive && Body.IsWithinRadius(target, spell.CalculateEffectiveRange(Body))) == true)
                Body.StopMoving();

            return base.CheckSpells(type);
        }

        // Autonomous and companion Bone Mages cannot depend on a nearby human
        // client to answer pet LoS packets. Range/target legality is still checked
        // by StandardMobBrain before every cast; real combat interrupts remain.
        protected override bool ShouldCheckLosForOffensiveSpell(Spell spell, GameLiving target) => false;
    }
}

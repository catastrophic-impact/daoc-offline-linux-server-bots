using DOL.AI.Brain;
using DOL.GS.PacketHandler;
using DOL.Language;

namespace DOL.GS.Spells
{
    /// <summary>
    /// Handler for spells that are issued by the player, but cast
    /// by his pet.
    /// </summary>
    [SpellHandler(eSpellType.PetSpell)]
    class PetSpellHandler : SpellHandler
    {
        public override string ShortDescription => "Servant spell:";

        public PetSpellHandler(GameLiving caster, Spell spell, SpellLine spellLine) : base(caster, spell, spellLine) { }

        /// <summary>
        /// Check if we have a pet to start with.
        /// </summary>
        /// <param name="selectedTarget"></param>
        /// <returns></returns>
        public override bool CheckBeginCast(GameLiving selectedTarget)
        {
            if (!base.CheckBeginCast(selectedTarget))
                return false;

            if (Caster.ControlledBrain == null)
            {
                if (Caster is GamePlayer playerCaster)
                    MessageToCaster(LanguageMgr.GetTranslation(playerCaster.Client, "PetSpellHandler.CheckBeginCast.NoControlledBrainForCast"), eChatType.CT_SpellResisted);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Called when spell has finished casting.
        /// </summary>
        /// <param name="target"></param>
        public override void FinishSpellCast(GameLiving target)
        {
            // IGamePlayer is the bot adapter, not an interface on GamePlayer.
            // Human shade commands must reach the same servant queue as bots.
            if (Caster is not (GamePlayer or IGamePlayer) || Caster.ControlledBrain == null)
                return;

            int powerCost = PowerCost(Caster);

            if (powerCost > 0)
                Caster.ChangeMana(Caster, eManaChangeType.Spell, -powerCost);

            if (Caster.ControlledBrain is NecromancerPetBrain petBrain && Spell.SubSpellID > 0)
            {
                Spell spell = SkillBase.GetSpellByID(Spell.SubSpellID);

                if (spell != null && spell.SubSpellID == 0)
                {
                    spell.Level = Spell.Level;
                    petBrain.OnOwnerFinishPetSpellCast(spell, SpellLine, target);
                }
            }

            if (Spell.RecastDelay > 0 && m_startReuseTimer)
            {
                foreach (Spell spell in SkillBase.GetSpellList(SpellLine.KeyName))
                {
                    if (spell.SpellType == Spell.SpellType && spell.RecastDelay == Spell.RecastDelay && spell.Group == Spell.Group)
                        Caster.DisableSkill(spell, spell.RecastDelay);
                }
            }
        }
    }
}

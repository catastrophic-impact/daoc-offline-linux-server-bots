using System;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.Quests.Hibernia;

namespace DOL.GS.Spells;

/// <summary>
/// The five Sluaghbinder epic rewards are service NPCs, not pets.  Keeping
/// this handler separate from SummonSpellHandler is intentional: it means the
/// NPCs never enter the controlled-pet list, never inherit a pet brain, and
/// never displace a combat summon.
/// </summary>
[SpellHandler(eSpellType.SluaghbinderEpicSummon)]
public sealed class SluaghbinderEpicSummon : SpellHandler
{
    public SluaghbinderEpicSummon(GameLiving caster, Spell spell, SpellLine line)
        : base(caster, spell, line)
    {
    }

    public override bool CheckBeginCast(GameLiving selectedTarget)
    {
        if (Caster is not GamePlayer player ||
            player.CharacterClass?.ID != (int)eCharacterClass.Sluaghbinder ||
            !SluaghbinderEpicQuestState.HasReward(player, Spell.ID))
        {
            MessageToCaster("This epic service has not been earned yet.", eChatType.CT_SpellResisted);
            return false;
        }

        return base.CheckBeginCast(selectedTarget);
    }

    public override void ApplyEffectOnTarget(GameLiving target)
    {
        if (Caster is GamePlayer player)
            SluaghbinderEpicServices.Spawn(player, Spell.ID);
    }
}


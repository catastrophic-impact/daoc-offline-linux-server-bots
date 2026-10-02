using DOL.GS.PacketHandler;
using DOL.Language;

namespace DOL.GS
{
    public class BerserkECSGameEffect : ECSGameAbilityEffect
    {
        public BerserkECSGameEffect(in ECSGameEffectInitParams initParams)
            : base(initParams)
        {
            EffectType = eEffect.Berserk;
        }

        protected ushort m_startModel = 0;

        public override ushort Icon { get { return 479; } }
        public override string Name => OwnerPlayer != null
            ? LanguageMgr.GetTranslation(OwnerPlayer.Client, "Effects.BerserkEffect.Name")
            : "Berserk";
        public override bool HasPositiveEffect { get { return true; } }

        public override void OnStartEffect()
        {
            m_startModel = Owner.Model;

            if (OwnerPlayer != null)
            {
                // "You go into a berserker frenzy!"
                OwnerPlayer.Out.SendMessage(LanguageMgr.GetTranslation(OwnerPlayer.Client, "Effects.BerserkEffect.StartFrenzy"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                // "{0} goes into a berserker frenzy!"
                Message.SystemToArea(OwnerPlayer, LanguageMgr.GetTranslation(OwnerPlayer.Client, "Effects.BerserkEffect.AreaStartFrenzy",OwnerPlayer.GetName(0, true)), eChatType.CT_System, OwnerPlayer);
            }

            // GameBot is player-like but is not a GamePlayer, so player-only
            // messaging must remain guarded while the frenzy animation and Vendo
            // model apply to both real Berserkers and bot Berserkers.
            int race = Owner switch
            {
                GamePlayer player => player.Race,
                GameBot bot => bot.Race,
                _ => (int)eRace.Unknown,
            };

            if (Owner is GamePlayer or GameBot)
            {
                Owner.Emote(eEmote.MidgardFrenzy);
                // Classic client model: dwarven Vendo differs from other races.
                Owner.Model = race == (int)eRace.Dwarf ? (ushort)12 : (ushort)3;
            }
        }
        
        public override void OnStopEffect()
        {
            Owner.Model = m_startModel;

            // there is no animation on end of the effect
            if (OwnerPlayer != null)
            {
                // "Your berserker frenzy ends."
                OwnerPlayer.Out.SendMessage(LanguageMgr.GetTranslation(OwnerPlayer.Client, "Effects.BerserkEffect.EndFrenzy"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                // "{0}'s berserker frenzy ends."
                Message.SystemToArea(OwnerPlayer, LanguageMgr.GetTranslation(OwnerPlayer.Client, "Effects.BerserkEffect.AreaEndFrenzy", OwnerPlayer.GetName(0, true)), eChatType.CT_System, OwnerPlayer);
            }

        }
    }
}

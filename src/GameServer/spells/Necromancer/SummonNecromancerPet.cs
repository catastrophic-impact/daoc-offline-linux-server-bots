/*
 * DAWN OF LIGHT - The first free open source DAoC server emulator
 * 
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
 *
 */

using System;
using System.Collections.Generic;
using System.Threading;
using DOL.AI.Brain;
using DOL.GS.Effects;
using DOL.GS.PacketHandler;
using DOL.Language;
using DOL.Logging;

namespace DOL.GS.Spells
{
    /// <summary>
    /// Spell handler to summon a necromancer pet.
    /// </summary>
    [SpellHandler(eSpellType.SummonNecroPet)]
    public class SummonNecromancerPet : SummonSpellHandler
    {
        private static readonly Logger log = LoggerManager.Create(typeof(SummonNecromancerPet));
        private int _releaseLogged;

        public SummonNecromancerPet(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) { }

        /// <summary>
        /// Check if caster is already in shade form.
        /// </summary>
        /// <param name="selectedTarget"></param>
        /// <returns></returns>
        public override bool CheckBeginCast(GameLiving selectedTarget)
        {
            // Do not depend on the shade effect having arrived yet: queued
            // summons and temporary companions can observe that transition.
            if (HasLivingBotServant())
                return false;

            if (EffectListService.GetAbilityEffectOnTarget(Caster, eEffect.Shade) != null)
            {
                if (Caster is GamePlayer playerCaster)
                    MessageToCaster(LanguageMgr.GetTranslation(playerCaster.Client, "SummonNecromancerPet.CheckBeginCast.ShadeEffectIsNotNull"), eChatType.CT_System);
                return false;
            }

            if (Caster is GamePlayer && Caster.ControlledBrain != null)
            {
                MessageToCaster(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "Summon.CheckBeginCast.AlreadyHaveaPet"), eChatType.CT_SpellResisted);
                return false;
            }

            return base.CheckBeginCast(selectedTarget);
        }

        /// <summary>
        /// Necromancer RR5 ability: Call of Darkness
        /// When active, the necromancer can summon a pet with only a 3 second cast time. 
        /// The effect remains active for 15 minutes, or until a pet is summoned.
        /// </summary>
        /// <returns></returns>
        public override int CalculateCastingTime()
        {
            return Caster.EffectList.GetOfType<CallOfDarknessEffect>() != null ? 3000 : base.CalculateCastingTime();
        }

        public override void ApplyEffectOnTarget(GameLiving target)
        {
            // Recheck at completion in case a servant appeared after the cast
            // began. Never replace or duplicate a bot's living summon.
            if (HasLivingBotServant())
                return;

            base.ApplyEffectOnTarget(target);

            if (Pet?.IsAlive == true && Caster.ControlledBrain?.Body == Pet)
            {
                if (Caster is GamePlayer playerCaster)
                    playerCaster.Shade(true);
                else if (Caster is IGamePlayer botCaster)
                    botCaster.Shade(true);
            }

            // Cancel RR5 Call of Darkness if on caster.
            FindStaticEffectOnTarget(Caster, typeof(CallOfDarknessEffect))?.Cancel(false);
        }

        private bool HasLivingBotServant() =>
            Caster is GameBot && Caster.ControlledBrain?.Body is { IsAlive: true };

        public override void OnPetReleased()
        {
            // One line at actual removal distinguishes death from explicit
            // travel/teardown cleanup without adding any per-tick logging.
            if (Pet is NecromancerPet servant && servant.Owner is GameBot bot &&
                Interlocked.Exchange(ref _releaseLogged, 1) == 0 && log.IsInfoEnabled)
            {
                string reason = servant.BotReleaseReason ?? (servant.IsAlive ? "summon-effect-ended" : "death");
                log.Info($"AUTONOMOUS_NECRO_PET_RELEASE bot=\"{bot.Name}\" id={bot.DatabaseID} level={bot.Level} " +
                    $"pet=\"{servant.Name}\" petObjectId={servant.ObjectID} petLevel={servant.Level} " +
                    $"hp={servant.Health}/{servant.MaxHealth} reason={reason} region={servant.CurrentRegionID} " +
                    $"distance={servant.GetDistanceTo(bot)} spellId={Spell.ID}");
            }
            base.OnPetReleased();
        }

        /// <summary>
        /// Delve info string.
        /// </summary>
        public override IList<string> DelveInfo
        {
            get
            {
                List<string> delve = new()
                {
                    LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "SummonNecromancerPet.DelveInfo.Function"),
                    "",
                    LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "SummonNecromancerPet.DelveInfo.Description"),
                    "",
                    LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "SummonNecromancerPet.DelveInfo.Target", Spell.Target),
                    LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "SummonNecromancerPet.DelveInfo.Power", Math.Abs(Spell.Power)),
                    LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "SummonNecromancerPet.DelveInfo.CastingTime", (Spell.CastTime / 1000).ToString("0.0## " + LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "SpellHandler.DelveInfo.Sec")))
                };

                return delve;
            }
        }

        protected override IControlledBrain GetPetBrain(GameLiving owner)
        {
            return new NecromancerPetBrain(owner);
        }

        protected override GameSummonedPet GetGamePet(INpcTemplate template)
        {
            return new NecromancerPet(template);
        }
    }
}

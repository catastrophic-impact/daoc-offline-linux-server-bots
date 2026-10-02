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
using System.Text;
using DOL.GS.Effects;
using DOL.AI.Brain;
using DOL.GS.PacketHandler;
using DOL.Events;
using DOL.GS.PropertyCalc;
using System.Collections;
using DOL.Language;
using DOL.Database;

namespace DOL.GS.Spells
{
	/// <summary>
	/// Spell handler to summon a bonedancer pet.
	/// </summary>
	/// <author>IST</author>
	[SpellHandler(eSpellType.SummonDruidPet)]
	public class SummonDruidPet : SummonSpellHandler
	{
		public SummonDruidPet(GameLiving caster, Spell spell, SpellLine line)
			: base(caster, spell, line) { }

		protected override IControlledBrain GetPetBrain(GameLiving owner)
		{
			// Real players do not implement the bot-only IGamePlayer interface.
			// Both must reach the priest's heal-before-melee brain. Keep the
			// pet body/stat factory unchanged: this repair is healing AI only.
			int? classId = owner switch
			{
				GamePlayer human => human.CharacterClass?.ID,
				IGamePlayer bot => bot.CharacterClass?.ID,
				_ => null
			};
			if (classId == (int)eCharacterClass.Sluaghbinder ||
				 (owner is GamePlayer player && classId == (int)eCharacterClass.Acolyte &&
				  player.Realm == eRealm.Hibernia && player.Level < 5))
			{
				return new SluaghbinderPetBrain(owner);
			}

			return base.GetPetBrain(owner);
		}

		protected override GameSummonedPet GetGamePet(INpcTemplate template)
		{
			// The dedicated role stats apply to real players, persistent GameBots,
			// and temporary companions on the isolated Sluaghbinder path only.
			if (Caster is IGamePlayer playerLike && playerLike.CharacterClass != null &&
				(playerLike.CharacterClass.ID == (int)eCharacterClass.Sluaghbinder ||
				 (Caster is GamePlayer player && player.CharacterClass.ID == (int)eCharacterClass.Acolyte &&
				  player.Realm == eRealm.Hibernia && player.Level < 5)))
			{
				return new SluaghbinderPet(template);
			}

			return base.GetGamePet(template);
		}

		public override bool CheckEndCast(GameLiving selectedTarget)
		{
			if (Caster is GamePlayer && ((GamePlayer)Caster).ControlledBrain != null)
			{
                MessageToCaster(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "Summon.CheckBeginCast.AlreadyHaveaPet"), eChatType.CT_SpellResisted);
                return false;
			}
			return base.CheckEndCast(selectedTarget);
		}
	}
}

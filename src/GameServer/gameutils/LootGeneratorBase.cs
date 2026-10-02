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
using System.Collections;
using DOL.AI.Brain;
using DOL.Database;

namespace DOL.GS
{
	/// <summary>
	/// Base for all loot generators
	/// </summary>
	public class LootGeneratorBase : ILootGenerator
	{
		private static readonly Logging.Logger log = Logging.LoggerManager.Create(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

		protected int m_exclusivePriority = 0;

		public LootGeneratorBase()
		{
		}

		public int ExclusivePriority
		{
			get{ return m_exclusivePriority; }
			set{ m_exclusivePriority = value; }
		}

		public virtual void Refresh(GameNPC mob)
		{
		}

		/// <summary>
		/// Generate loot for given mob
		/// </summary>
		/// <param name="mob"></param>
		/// <param name="killer"></param>
		/// <returns></returns>
		public virtual LootList GenerateLoot(GameNPC mob, GameObject killer)
		{
			LootList loot = new LootList();
			return loot;
		}

		/// <summary>Resolve a real player, autonomous bot, or either one's controlled pet.</summary>
		protected static GameLiving ResolveLootOwner(GameObject killer)
		{
			if (killer is GamePlayer || killer is GameBot)
				return killer as GameLiving;

			if (killer is GameNPC npc && npc.Brain is IControlledBrain controlled)
				return controlled.GetLivingOwner() ?? controlled.GetPlayerOwner();

			return null;
		}

		protected static bool CanReceiveRealmDrop(GameLiving owner, DbItemTemplate drop)
		{
			if (owner == null || drop == null)
				return false;

			return drop.Realm == 0 || drop.Realm == (int)owner.Realm ||
			       owner is GamePlayer player && player.CanUseCrossRealmItems ||
			       owner is GameBot && ServerProperties.Properties.ALLOW_CROSS_REALM_ITEMS;
		}
	}
}

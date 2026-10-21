using DOL.GS;

namespace DOL.AI.Brain
{
	public class GuardBrain : StandardMobBrain
	{
		public override int ThinkInterval => 2000;
		public override int AggroLevel => 90;
		public override int AggroRange => 750;

		public GuardBrain() : base()
		{
			FSM.Add(new GuardState_RETURN_TO_SPAWN(this));
		}

		protected override void CheckPlayerAggro()
		{
			foreach (GamePlayer player in Body.GetPlayersInRadius((ushort)AggroRange))
			{
				if (!CanAggroTarget(player))
					continue;

				if (player.IsStealthed || player.Steed != null)
					continue;

				if (player.effectListComponent.ContainsEffectForEffectType(eEffect.Shade))
					continue;

				SendAggroLosCheck(player, player);
				// We don't know if the LoS check will be positive, so we have to ask other players
			}
		}

		protected override void CheckNpcAggro()
		{
			foreach (GameNPC npc in Body.GetNPCsInRadius((ushort)AggroRange))
			{
				if (!CanAggroTarget(npc))
					continue;
				if ((npc.Flags & GameNPC.eFlags.FLYING) != 0)
					continue;
				if (LeavesToPlayer(DOL.GS.Quests.ClassicQuests.IsQuestMonster(npc), npc.GetDistanceTo(npc.SpawnPoint)))
					continue;

				AddToAggroList(npc);
				// No LoS check, we just attack what's in range
				return;
			}
		}

		/// <summary>A classic quest monster this close to its own spawn is at home.</summary>
		public const int QuestMonsterHomeRadius = 3000;

		/// <summary>
		/// A classic quest's monster at home is the player's fight (the guards who stole Camelot's battle plans in
		/// List of Denial appear inside the city): the town guards do not take it from them. One far from home is
		/// fought, as the Vindsaul gate guards fought Fjall and his friends on their runs from the fenrir fort.
		/// </summary>
		public static bool LeavesToPlayer(bool isQuestMonster, int distanceFromItsSpawn) =>
			isQuestMonster && distanceFromItsSpawn <= QuestMonsterHomeRadius;

		/// <summary>
		/// We override this because we want guards to attack even gray npcs
		/// </summary>
		/// <param name="target"></param>
		/// <returns></returns>
		public override bool CanAggroTarget(GameLiving target)
		{
			return AggroLevel > 0 && GameServer.ServerRules.IsAllowedToAttack(Body, target, true);
		}
	}
}

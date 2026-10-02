/*
 * Experimental Sluaghbinder trainer for the isolated new-class test copy.
 */
using DOL.GS.PacketHandler;

namespace DOL.GS.Trainer
{
	[NPCGuildScript("Sluaghbinder Trainer", eRealm.Hibernia)]
	public class SluaghbinderTrainer : GameTrainer
	{
		public override eCharacterClass TrainedClass => eCharacterClass.Sluaghbinder;

		public override eQuestIndicator GetQuestIndicator(GamePlayer player)
		{
			eQuestIndicator indicator = base.GetQuestIndicator(player);
			if (indicator != eQuestIndicator.None)
				return indicator;

			return DOL.GS.Quests.Hibernia.SluaghbinderEpicQuestRuntime.GetTrainerQuestIndicator(player, this);
		}

		/// <summary>
		/// Explicitly gate the novice promotion.  The client uses the old
		/// Hibernian Mauler slot, so the generic base-class resolver is not a
		/// safe authority here; this path must never promote to MaulerHib.
		/// </summary>
		public override bool CanPromotePlayer(GamePlayer player)
		{
			if (player == null || player.Realm != eRealm.Hibernia ||
				player.Level < 5 ||
				player.CharacterClass?.ID != (int)eCharacterClass.Acolyte)
				return false;

			var targetClass = ScriptMgr.FindCharacterClass((int)TrainedClass);
			return targetClass != null &&
				targetClass.EligibleRaces.Exists(r => r.ID == (eRace)player.Race) &&
				(!GlobalConstants.CLASS_GENDER_CONSTRAINTS_DICT.TryGetValue(TrainedClass, out eGender gender) ||
					gender == player.Gender);
		}

		// Levels 1-4 are stored as the experimental Hibernian Acolyte base
		// class.  Let this trainer provide the normal novice training window;
		// at level 5 CanPromotePlayer takes over and offers Sluaghbinder.
		public override bool CanTrain(GamePlayer player)
		{
			if (player?.CharacterClass != null &&
				player.Realm == eRealm.Hibernia &&
				player.CharacterClass.ID == (int)eCharacterClass.Acolyte &&
				player.Level < 5)
			{
				return true;
			}

			return base.CanTrain(player);
		}

		public override bool Interact(GamePlayer player)
		{
			if (!base.Interact(player))
				return false;

			if (player.CharacterClass.ID == (int)TrainedClass)
			{
				player.Out.SendMessage(Name + " says, \"The Sluagh Host, Abhartach's Rot, and Cairn Oath are the core paths. Train Dullahan's Bulwark, Abhartach's Bane, or Sluagh Covenant to shape your calling.\"", eChatType.CT_Say, eChatLoc.CL_ChatWindow);
			}
			else if (CanPromotePlayer(player))
			{
				player.Out.SendMessage(Name + " says, \"Do you seek the [Path of the Sluaghbinder], binding the restless dead to Hibernia's defense?\"", eChatType.CT_System, eChatLoc.CL_PopupWindow);
				if (!player.IsLevelRespecUsed)
					OfferRespecialize(player);
			}
			else
			{
				CheckChampionTraining(player);
			}

			return true;
		}

		public override bool WhisperReceive(GameLiving source, string text)
		{
			if (!base.WhisperReceive(source, text))
				return false;

			GamePlayer player = source as GamePlayer;
			if (player == null)
				return false;

			if (text == "Path of the Sluaghbinder" || text == "Path of the Sluaghbinder".ToLowerInvariant())
			{
				if (CanPromotePlayer(player))
					PromotePlayer(player, (int)TrainedClass, "The barrow-bond is yours. Welcome, Sluaghbinder.", null);
			}

			return true;
		}
	}
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Events;
using DOL.GS.ServerProperties;

namespace DOL.GS.GameEvents
{
	/// <summary>
	/// Give some Default Startup Equipment to newly created Character based on StarterEquipment Table.
	/// </summary>
	public static class CreationStartupEquipment
	{
		#region Properties
		/// <summary>
		/// Enable the Free Starter Equipment Gift.
		/// </summary>
		[ServerProperty("startup", "enable_free_starter_equipment", "Enable Startup Free Equipment gifts imported from StarterEquipment Table", true)]
		public static bool ENABLE_FREE_STARTER_EQUIPMENT;
		#endregion

		/// <summary>
		/// Declare a logger for this class.
		/// </summary>
		private static readonly Logging.Logger log = Logging.LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
		
		/// <summary>
		/// Table Cache
		/// </summary>
		private static readonly Dictionary<eCharacterClass, List<DbItemTemplate>> m_cachedClassEquipment = new Dictionary<eCharacterClass, List<DbItemTemplate>>();
		
		/// <summary>
		/// Register Character Creation Events
		/// </summary>
		/// <param name="e"></param>
		/// <param name="sender"></param>
		/// <param name="args"></param>
		[ScriptLoadedEvent]
		public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
		{
			InitStarterEquipment();
			GameEventMgr.AddHandler(DatabaseEvent.CharacterCreated, new DOLEventHandler(OnCharacterCreation));
		}
		
		/// <summary>
		/// Init (Or Refresh) Starter Equipment Cache
		/// </summary>
		[RefreshCommand]
		public static void InitStarterEquipment()
		{
			m_cachedClassEquipment.Clear();
			
			// Init Startup Collection.
			foreach (var equipclass in GameServer.Database.SelectAllObjects<StarterEquipment>())
			{
				if (equipclass.Template != null)
				{
					foreach(var classID in Util.SplitCSV(equipclass.Class, true))
					{
						int cId;
						if (int.TryParse(classID, out cId))
						{
							try
							{
								eCharacterClass gameClass = (eCharacterClass)cId;
								if (!m_cachedClassEquipment.ContainsKey(gameClass))
									m_cachedClassEquipment.Add(gameClass, new List<DbItemTemplate>());
								
								m_cachedClassEquipment[gameClass].Add(equipclass.Template);
							}
							catch (Exception e)
							{
								if (log.IsWarnEnabled)
									log.WarnFormat("Could not Add Starter Equipement for Record - ID: {0}, ClassID(s): {1}, Itemtemplate: {2}, while parsing {3}\n{4}",
									               equipclass.StarterEquipmentID, equipclass.Class, equipclass.TemplateID, classID, e);
							}
						}
					}
				}
				else
				{
					if (log.IsWarnEnabled)
						log.WarnFormat("Cannot Find Item Template for Record - ID: {0}, ClassID(s): {1}, Itemtemplate: {2}", equipclass.StarterEquipmentID, equipclass.Class, equipclass.TemplateID);
				}
			}
		}

		/// <summary>
		/// Returns the same first usable weapon template that normal character
		/// creation would place on a character of this class.  Autonomous
		/// playerbots use this instead of rolling equipment from the wider item
		/// database, keeping their level-one loadout identical to a real player.
		/// </summary>
		public static DbItemTemplate GetStarterWeaponTemplate(eCharacterClass characterClass)
		{
			if (!m_cachedClassEquipment.ContainsKey(characterClass))
				InitStarterEquipment();

			return m_cachedClassEquipment
				.Where(entry => entry.Key == 0 || entry.Key == characterClass)
				.SelectMany(entry => entry.Value)
				.Where(item => item != null &&
					item.Item_Type is >= Slot.RIGHTHAND and <= Slot.RANGED &&
					(eObjectType)item.Object_Type != eObjectType.Shield &&
					(eObjectType)item.Object_Type != eObjectType.Instrument)
				.OrderBy(item => item.Item_Type)
				.ThenBy(item => item.Id_nb, StringComparer.Ordinal)
				.FirstOrDefault();
		}
		
		/// <summary>
		/// Unregister Character Creation Events
		/// </summary>
		/// <param name="e"></param>
		/// <param name="sender"></param>
		/// <param name="args"></param>
		[ScriptUnloadedEvent]
		public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
		{
			GameEventMgr.RemoveHandler(DatabaseEvent.CharacterCreated, new DOLEventHandler(OnCharacterCreation));
		}
		
		/// <summary>
		/// On Character Creation set up equipment from StarterEquipment Table.
		/// </summary>
		/// <param name="e"></param>
		/// <param name="sender"></param>
		/// <param name="args"></param>
		public static void OnCharacterCreation(DOLEvent e, object sender, EventArgs args)
		{
			if (!ENABLE_FREE_STARTER_EQUIPMENT)
				return;
			
			// Check Args
			var chArgs = args as CharacterEventArgs;
			
			if (chArgs == null)
				return;
			
			DbCoreCharacter ch = chArgs.Character;
			
			try
			{
				var usedSlots = new Dictionary<eInventorySlot, bool>();
				
				if (m_cachedClassEquipment.ContainsKey((eCharacterClass)ch.Class))
				{
					// sort for filling righ hand first...
					foreach (var cachedItem in m_cachedClassEquipment.Where(k => k.Key == 0 || k.Key == (eCharacterClass)ch.Class).SelectMany(kv => kv.Value).OrderBy(it => it.Item_Type))
					{
						// Sluaghbinder characters are stored as the Hibernian Acolyte
						// base class until level five.  The generic Acolyte starter rows
						// are Albion leather (Roman models), which gives a Firbolg the
						// wrong helmet and wrong realm visuals.  Resolve only this
						// experimental Hibernian base-class loadout to the existing
						// Hibernian reinforced starter templates; all other classes keep
						// the database's normal starter equipment unchanged.
						var item = GetCharacterStarterTemplate(cachedItem, ch);

						// create Inventory item and set to owner.
						GameInventoryItem inventoryItem = GameInventoryItem.Create(item.IsStackable ? item : new DbItemUnique(item));
						inventoryItem.OwnerID = ch.ObjectId;
						inventoryItem.Creator = $"{nameof(CreationStartupEquipment)}";
						inventoryItem.Realm = ch.Realm;

						bool itemChoosen = false;
		
						// if equipable item, find equippable slot
						foreach (eInventorySlot currentSlot in GameLivingInventory.EQUIP_SLOTS)
						{
							if ((eInventorySlot)inventoryItem.Item_Type == currentSlot)
							{
								eInventorySlot chosenSlot;
		
								// try to set Left Hand in Right Hand slot if not already used.
								if (currentSlot == eInventorySlot.LeftHandWeapon && (eObjectType)inventoryItem.Object_Type != eObjectType.Shield && !usedSlots.ContainsKey(eInventorySlot.RightHandWeapon))
								{
									chosenSlot = eInventorySlot.RightHandWeapon;
								}
								else
								{
									chosenSlot = currentSlot;
								}
		
								// Slot is occupied, add this to backpack.
								if (usedSlots.ContainsKey(chosenSlot))
								{
									if (log.IsWarnEnabled)
										log.WarnFormat("Cannot add Starter Equipment item {0} to class {1} an item is already assigned to this slot! (Added to Backpack...)", item.Id_nb, ch.Class);
									break;
								}
		
								inventoryItem.SlotPosition = (int)chosenSlot;
								usedSlots[chosenSlot] = true;
								if (ch.ActiveWeaponSlot == 0)
								{
									switch (inventoryItem.SlotPosition)
									{
										case Slot.RIGHTHAND:
											ch.ActiveWeaponSlot = (byte)eActiveWeaponSlot.Standard;
											break;
										case Slot.TWOHAND:
											ch.ActiveWeaponSlot = (byte)eActiveWeaponSlot.TwoHanded;
											break;
										case Slot.RANGED:
											ch.ActiveWeaponSlot = (byte)eActiveWeaponSlot.Distance;
											break;
									}
									
									// Save char to DB if Active Slot changed...
									if (ch.ActiveWeaponSlot != 0)
										GameServer.Database.SaveObject(ch);
								}
								
								itemChoosen = true;
								break;
							}
						}
						
						if (!itemChoosen)
						{
							//otherwise stick the item in the backpack
							for (int i = (int)eInventorySlot.FirstBackpack; i < (int)eInventorySlot.LastBackpack; i++)
							{
								if (!usedSlots.ContainsKey((eInventorySlot)i))
								{
									inventoryItem.SlotPosition = i;
									usedSlots[(eInventorySlot)i] = true;
									break;
								}
							}
						}
						
						GameServer.Database.AddObject(inventoryItem);
					}
				}
				
			}
			catch (Exception err)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("Error while adding Startup Equipment to {0} - Exception: {1}", ch.Name, err);
			}
		}

		private static DbItemTemplate GetCharacterStarterTemplate(DbItemTemplate template, DbCoreCharacter character)
		{
			if (template == null || character == null || character.Realm != (int)eRealm.Hibernia ||
				character.Class != (int)eCharacterClass.Acolyte)
				return template;

			string replacementId = template.Id_nb switch
			{
				// The novice follows the Sluaghbinder's Blunt line.  Use the
				// isolated Hibernian blunt starter template rather than the
				// copied Hibernian sword template; the latter leaves a level-one
				// Acolyte holding a weapon it cannot train with.
				"training_mace" => "training_mace_hib",
				"small_training_shield" => "training_shield",
				"rawhide_roman_leather_helm" => "tacuil_helm",
				"rawhide_roman_leather_boots" => "tacuil_boots",
				"rawhide_roman_leather_gloves" => "tacuil_gauntlets",
				"rawhide_roman_leather_jerkin" => "tacuil_vest",
				"rawhide_roman_leather_leggings" => "tacuil_leggings",
				"rawhide_roman_leather_sleeves" => "tacuil_sleeves",
				_ => null,
			};

			if (replacementId == null)
				return template;

			return GameServer.Database.FindObjectByKey<DbItemTemplate>(replacementId) ?? template;
		}
	}
}

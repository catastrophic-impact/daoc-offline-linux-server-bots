using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Events;

namespace DOL.GS.GameEvents
{
	/// <summary>
	/// Moves new created Characters to the starting location based on region, class and race
	/// </summary>
	public static class StartupLocations
	{
		/// <summary>
		/// Declare a logger for this class.
		/// </summary>
		private static readonly Logging.Logger log = Logging.LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

		/// <summary>
		/// Cached DB Startup Location
		/// </summary>
		private static readonly List<StartupLocation> m_cachedLocations = new List<StartupLocation>();

		/// <summary>
		/// Current Game Request Tutorial Region ID.
		/// </summary>
		private const int TUTORIAL_REGIONID = 27;
		
		[ScriptLoadedEvent]
		public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
		{
			GameEventMgr.AddHandler(DatabaseEvent.CharacterCreated, new DOLEventHandler(CharacterCreation));
			GameEventMgr.AddHandler(DatabaseEvent.CharacterSelected, new DOLEventHandler(CharacterSelection));
			
			InitStartupLocation();
			
			if (log.IsInfoEnabled)
				log.Info("StartupLocations initialized");
		}

		[ScriptUnloadedEvent]
		public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
		{
			GameEventMgr.RemoveHandler(DatabaseEvent.CharacterCreated, new DOLEventHandler(CharacterCreation));
			GameEventMgr.RemoveHandler(DatabaseEvent.CharacterSelected, new DOLEventHandler(CharacterSelection));
		}
		
		/// <summary>
		/// Init Startup Location Static Cache
		/// </summary>
		[RefreshCommand]
		public static void InitStartupLocation()
		{
			m_cachedLocations.Clear();
			
			foreach (var obj in GameServer.Database.SelectAllObjects<StartupLocation>())
				m_cachedLocations.Add(obj);
		}

		/// <summary>
		/// Change location on character creation
		/// </summary>
		public static void CharacterCreation(DOLEvent ev, object sender, EventArgs args)
		{
			// Check Args
			var chArgs = args as CharacterEventArgs;
			
			if (chArgs == null)
				return;
			
			DbCoreCharacter ch = chArgs.Character;
			
			try
			{
				
				var availableLocation = GetAllStartupLocationForCharacter(ch, chArgs.GameClient.Version);

				StartupLocation dbStartupLocation = null;
				
				// get the first entry according to Tutorial Enabling.
				foreach (var location in availableLocation)
				{
					dbStartupLocation = location;
					break;
				}

				if (dbStartupLocation == null)
				{
					log.WarnFormat("startup location not found: account={0}; char name={1}; region={2}; realm={3}; class={4} ({5}); race={6} ({7}); version={8}",
						ch.AccountName, ch.Name, ch.Region, ch.Realm, ch.Class, (eCharacterClass) ch.Class, ch.Race, (eRace)ch.Race, chArgs.GameClient.Version);
				}
				else
				{
					ch.Xpos = dbStartupLocation.XPos;
					ch.Ypos = dbStartupLocation.YPos;
					ch.Zpos = dbStartupLocation.ZPos;
					ch.Region = dbStartupLocation.Region;
					ch.Direction = dbStartupLocation.Heading;
					BindCharacter(ch);
				}
			}
			catch (Exception e)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("StartupLocations script: error changing location. account={0}; char name={1}; region={2}; realm={3}; class={4} ({5}); race={6} ({7}); version={8}; {9}",
						ch.AccountName, ch.Name, ch.Region, ch.Realm, ch.Class, (eCharacterClass) ch.Class, ch.Race, (eRace)ch.Race, chArgs.GameClient.Version, e);
			}
		}

		/// <summary>
		/// Change location on character selection if it has any wrong values...
		/// </summary>
		public static void CharacterSelection(DOLEvent ev, object sender, EventArgs args)
		{
			// Check Args
			var chArgs = args as CharacterEventArgs;
			
			if (chArgs == null)
				return;
			
			DbCoreCharacter ch = chArgs.Character;
			
			// check if location looks ok.
			if (ch.Xpos == 0 && ch.Ypos == 0 && ch.Zpos == 0)
			{
				// This character needs to be fixed !
				CharacterCreation(ev, sender, args);
				GameServer.Database.SaveObject(ch);
				return;
			}
			
			// check if bind looks ok.
			if (ch.BindXpos == 0 && ch.BindYpos == 0 && ch.BindZpos == 0)
			{
				// This Bind needs to be fixed !
				BindCharacter(ch);
				GameServer.Database.SaveObject(ch);
			}
		}
		
		public static IList<StartupLocation> GetAllStartupLocationForCharacter(DbCoreCharacter ch, GameClient.eClientVersion cli)
		{
			var locations = m_cachedLocations.Where(sl => sl.MinVersion <= (int)cli)
				.Where(sl => sl.ClassID == 0 || sl.ClassID == ch.Class)
				.Where(sl => sl.RaceID == 0 || sl.RaceID == ch.Race)
				.Where(sl => sl.RealmID == 0 || sl.RealmID == ch.Realm)
				.Where(sl => sl.ClientRegionID == 0 || sl.ClientRegionID == ch.Region)
				.OrderByDescending(sl => sl.MinVersion).ThenByDescending(sl => sl.ClientRegionID)
				.ThenByDescending(sl => sl.RealmID).ThenByDescending(sl => sl.ClassID)
				.ThenByDescending(sl => sl.RaceID).ToList();

			// The experimental Hibernian Sluaghbinder is stored as Acolyte until
			// its level-five promotion.  The stock database has no generic Hibernian
			// Acolyte startup row, so a fresh character otherwise keeps region and
			// coordinates at zero and cannot enter the world.  Reuse an existing
			// Hibernian starting point instead of inventing another coordinate set.
			// This fallback is deliberately limited to the isolated class's base
			// record and cannot change startup placement for any normal class.
			if (locations.Count == 0 && ch.Realm == (int)eRealm.Hibernia && ch.Class == (int)eCharacterClass.Acolyte)
			{
				locations = m_cachedLocations
					.Where(sl => sl.MinVersion <= (int)cli)
					.Where(sl => sl.RealmID == 0 || sl.RealmID == (int)eRealm.Hibernia)
					.Where(sl => sl.RaceID == 0 || sl.RaceID == ch.Race)
					.Where(sl => sl.ClientRegionID == 0 || sl.ClientRegionID == ch.Region)
					.Where(sl => sl.ClientRegionID != TUTORIAL_REGIONID)
					.Where(sl => IsClassicOrSiStartRegion(eRealm.Hibernia, sl.Region))
					.Where(sl => sl.XPos != 0 || sl.YPos != 0 || sl.ZPos != 0)
					.OrderByDescending(sl => sl.MinVersion).ThenByDescending(sl => sl.ClientRegionID)
					.ThenByDescending(sl => sl.RealmID).ThenByDescending(sl => sl.ClassID)
					.ThenByDescending(sl => sl.RaceID).ToList();

				if (locations.Count > 0 && log.IsInfoEnabled)
					log.InfoFormat("Using Hibernian Acolyte startup fallback for experimental Sluaghbinder (race={0})", ch.Race);
			}

			return locations;
		}

		/// <summary>
		/// Returns distinct, non-tutorial Classic and Shrouded Isles starting points
		/// applicable to an autonomous character.  This deliberately consumes the
		/// same StartupLocation records used by normal character creation instead of
		/// maintaining a second set of world coordinates.
		/// </summary>
		public static IReadOnlyList<StartupLocation> GetClassicSiLocationsForAutonomous(
			eRealm realm, int raceId, int classId) =>
			FilterClassicSiLocationsForAutonomous(m_cachedLocations, realm, raceId, classId);

		/// <summary>
		/// One equiprobable selection from already-authoritative startup rows.
		/// Kept explicit so callers cannot accidentally turn row order, a capital,
		/// or class/race duplicate count into a placement preference.
		/// </summary>
		public static StartupLocation ChooseUniformAutonomousLocation(
			IEnumerable<StartupLocation> locations, Random random = null)
		{
			StartupLocation[] choices = locations?.Where(location => location != null).ToArray() ?? Array.Empty<StartupLocation>();
			if (choices.Length == 0)
				return null;
			random ??= Random.Shared;
			return choices[random.Next(choices.Length)];
		}

		/// <summary>
		/// Filters a supplied StartupLocation collection for the autonomous-spawn
		/// policy.  Kept separate from the cache lookup so the policy remains
		/// deterministic and independently testable.
		/// </summary>
		public static IReadOnlyList<StartupLocation> FilterClassicSiLocationsForAutonomous(
			IEnumerable<StartupLocation> locations, eRealm realm, int raceId, int classId)
		{
			if (locations == null || !IsPlayerRealm(realm))
				return Array.Empty<StartupLocation>();

			return locations
				.Where(location => location != null && location.MinVersion <= (int)GameClient.eClientVersion.Version168)
				.Where(location => location.RealmID == 0 || location.RealmID == (int)realm)
				.Where(location => location.RaceID == 0 || location.RaceID == raceId)
				.Where(location => location.ClassID == 0 || location.ClassID == classId)
				.Where(location => location.ClientRegionID != TUTORIAL_REGIONID)
				.Where(location => IsClassicOrSiStartRegion(realm, location.Region))
				.Where(location => location.XPos != 0 || location.YPos != 0 || location.ZPos != 0)
				// Multiple class/race rows often represent one physical spawn point.
				// Deduplicate before the later random choice so each valid location has
				// equal probability rather than being weighted by data-row count.
				.GroupBy(location => (location.Region, location.XPos, location.YPos, location.ZPos, location.Heading))
				.Select(group => group.OrderByDescending(location => location.ClassID != 0)
					.ThenByDescending(location => location.RaceID != 0)
					.ThenByDescending(location => location.RealmID != 0)
					.First())
				.ToArray();
		}

		private static bool IsPlayerRealm(eRealm realm) =>
			realm is eRealm.Albion or eRealm.Midgard or eRealm.Hibernia;

		private static bool IsClassicOrSiStartRegion(eRealm realm, int regionId) => realm switch
		{
			eRealm.Albion => regionId is 1 or 51,
			eRealm.Midgard => regionId is 100 or 151,
			eRealm.Hibernia => regionId is 200 or 181,
			_ => false,
		};
		
		public static StartupLocation GetNonTutorialLocation(GamePlayer player)
		{
			try
			{
				return GetAllStartupLocationForCharacter(player.Client.Account.Characters[player.Client.ActiveCharIndex], player.Client.Version).First(sl => sl.ClientRegionID != TUTORIAL_REGIONID);
			}
			catch
			{
				return null;
			}
				
		}

		/// <summary>
		/// Binds character to current location
		/// </summary>
		/// <param name="ch"></param>
		public static void BindCharacter(DbCoreCharacter ch)
		{
			ch.BindRegion = ch.Region;
			ch.BindHeading = ch.Direction;
			ch.BindXpos = ch.Xpos;
			ch.BindYpos = ch.Ypos;
			ch.BindZpos = ch.Zpos;
		}
	}
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Logging;
using DOL.Events;
using DOL.GS.PacketHandler;

namespace DOL.GS
{
    public class BotManager
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly ConcurrentDictionary<string, GameBot> ActiveBots = new();

        [GameServerStartedEvent]
        public static void OnServerStart(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, new DOLEventHandler(OnPlayerLogin));
        }

        [GameServerStoppedEvent]
        public static void OnServerStop(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, new DOLEventHandler(OnPlayerLogin));
        }

        private static void OnPlayerLogin(DOLEvent e, object sender, EventArgs args)
        {
            if (sender is GamePlayer player)
                RespawnBotsOnLogin(player);
        }

        public const int MAX_BOTS_PER_PLAYER = 15;
        public const int FOLLOW_DISTANCE = 150;
        public const int MAX_FOLLOW_DISTANCE = 400;

        /// <summary>
        /// Create a new bot with specified parameters
        /// </summary>
        public static GameBot CreateBot(GamePlayer owner, string name, byte classId, byte raceId, byte genderId)
        {
            if (owner == null) return null;

            var currentBots = GetBotsForOwner(owner).Count();
            if (currentBots >= MAX_BOTS_PER_PLAYER)
            {
                owner.Out.SendMessage($"You can only control {MAX_BOTS_PER_PLAYER} bots at a time.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return null;
            }

            GameBot bot;

            try
            {
                bot = new GameBot(owner, classId, name, raceId, genderId);
            }
            catch (Exception ex)
            {
                log.Error($"Failed to create bot for player {owner.Name} with class {classId}: {ex.Message}", ex);
                return null;
            }

            ActiveBots[bot.InternalID] = bot;

            log.InfoFormat("Bot {0} created for player {1}", bot.InternalID, owner.Name);
            return bot;
        }

        public static string GetClassNameById(byte classId)
        {
            return ((eCharacterClass)classId) switch
            {
                eCharacterClass.Paladin => "Paladin",
                eCharacterClass.Armsman => "Armsman",
                eCharacterClass.Scout => "Scout",
                eCharacterClass.Minstrel => "Minstrel",
                eCharacterClass.Theurgist => "Theurgist",
                eCharacterClass.Cleric => "Cleric",
                eCharacterClass.Wizard => "Wizard",
                eCharacterClass.Sorcerer => "Sorcerer",
                eCharacterClass.Infiltrator => "Infiltrator",
                eCharacterClass.Friar => "Friar",
                eCharacterClass.Mercenary => "Mercenary",
                eCharacterClass.Necromancer => "Necromancer",
                eCharacterClass.Cabalist => "Cabalist",
                eCharacterClass.Reaver => "Reaver",

                eCharacterClass.Eldritch => "Eldritch",
                eCharacterClass.Enchanter => "Enchanter",
                eCharacterClass.Mentalist => "Mentalist",
                eCharacterClass.Blademaster => "Blademaster",
                eCharacterClass.Hero => "Hero",
                eCharacterClass.Champion => "Champion",
                eCharacterClass.Warden => "Warden",
                eCharacterClass.Druid => "Druid",
                eCharacterClass.Bard => "Bard",
                eCharacterClass.Nightshade => "Nightshade",
                eCharacterClass.Ranger => "Ranger",
                eCharacterClass.Animist => "Animist",
                eCharacterClass.Valewalker => "Valewalker",

                eCharacterClass.Thane => "Thane",
                eCharacterClass.Warrior => "Warrior",
                eCharacterClass.Shadowblade => "Shadowblade",
                eCharacterClass.Skald => "Skald",
                eCharacterClass.Hunter => "Hunter",
                eCharacterClass.Healer => "Healer",
                eCharacterClass.Spiritmaster => "Spiritmaster",
                eCharacterClass.Shaman => "Shaman",
                eCharacterClass.Runemaster => "Runemaster",
                eCharacterClass.Bonedancer => "Bonedancer",
                eCharacterClass.Berserker => "Berserker",
                eCharacterClass.Savage => "Savage",
                eCharacterClass.Sluaghbinder => "Sluaghbinder",

                _ => "Fighter"
            };
        }

        public static bool RemoveBot(GameBot bot)
        {
            if (bot == null) return false;

            if (ActiveBots.TryRemove(bot.InternalID, out _))
            {
                bot.RemoveFromWorld();
                bot.Delete();
                log.InfoFormat("Bot {0} removed", bot.InternalID);
                return true;
            }
            return false;
        }

        public static IEnumerable<GameBot> GetBotsForOwner(GamePlayer owner)
        {
            if (owner == null) return Enumerable.Empty<GameBot>();
            return ActiveBots.Values.Where(b => b.Owner == owner);
        }

        public static void CleanupPlayerBots(GamePlayer owner)
        {
            if (owner == null) return;
            var bots = GetBotsForOwner(owner).ToList();
            foreach (var bot in bots) RemoveBot(bot);
        }

        /// <summary>
        /// Load a bot from database by name for the owner
        /// </summary>
        public static GameBot LoadBotByName(GamePlayer owner, string botName)
        {
            return BotDatabase.LoadBotByName(owner, botName);
        }

        /// <summary>
        /// Spawn a bot into the world
        /// </summary>
        public static bool SpawnBot(GameBot bot)
        {
            if (bot == null) return false;

            // Copy owner's position so AddToWorld has a valid region
            bot.X = bot.Owner.X;
            bot.Y = bot.Owner.Y;
            bot.Z = bot.Owner.Z;
            bot.Heading = bot.Owner.Heading;
            bot.CurrentRegionID = bot.Owner.CurrentRegionID;

            if (!bot.AddToWorld())
            {
                log.ErrorFormat("Failed to spawn bot {0} — AddToWorld returned false", bot.InternalID);
                return false;
            }

            ActiveBots[bot.InternalID] = bot;
            BotDatabase.SetBotActive(bot.DatabaseID, true);
            log.InfoFormat("Bot {0} spawned", bot.InternalID);
            return true;
        }

        /// <summary>
        /// Respawn all active bots for a player on login
        /// </summary>
        public static void RespawnBotsOnLogin(GamePlayer owner)
        {
            if (owner == null) return;

            var activeProfiles = BotDatabase.GetActiveBots(owner);
            foreach (var profile in activeProfiles)
            {
                try
                {
                    var bot = BotDatabase.LoadBot(owner, profile.BotId);
                    if (bot == null) continue;

                    if (!SpawnBot(bot)) continue;

                    // Auto-invite to owner's group
                    if (owner.Group == null)
                    {
                        var group = new Group(owner);
                        GroupMgr.AddGroup(group);
                        group.AddMember(owner);
                    }
                    owner.Group.AddMember(bot);

                    // Auto-follow owner
                    bot.Follow(owner, FOLLOW_DISTANCE, MAX_FOLLOW_DISTANCE);
                    if (bot.Brain is DOL.AI.Brain.BotBrain brain)
                        brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);

                    log.InfoFormat("Bot {0} respawned on login for {1}", bot.Name, owner.Name);
                }
                catch (Exception ex)
                {
                    log.Error($"Error respawning bot {profile.BotId} for {owner.Name}: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Get saved bots for owner from database
        /// </summary>
        public static IEnumerable<GameBot> GetSavedBotsForOwner(GamePlayer owner)
        {
            var profiles = BotDatabase.GetSavedBots(owner);
            return profiles.Select(p => BotDatabase.LoadBot(owner, p.BotId)).Where(b => b != null);
        }

        /// <summary>
        /// Delete a bot permanently from database
        /// </summary>
        public static void DeleteBot(GameBot bot)
        {
            if (bot != null)
            {
                RemoveBot(bot);
                BotDatabase.DeleteBot(bot);
                log.InfoFormat("Bot {0} deleted permanently", bot.InternalID);
            }
        }

        /// <summary>
        /// Despawn a bot (remove from world but keep in database)
        /// </summary>
        public static void DespawnBot(GameBot bot)
        {
            if (bot != null)
            {
                bot.Group?.RemoveMember(bot);
                bot.RemoveFromWorld();
                ActiveBots.TryRemove(bot.InternalID, out _);
                BotDatabase.SetBotActive(bot.DatabaseID, false);
                log.InfoFormat("Bot {0} despawned", bot.InternalID);
            }
        }

        /// <summary>
        /// Get bot by name for owner (active bots only)
        /// </summary>
        public static GameBot GetBotByName(GamePlayer owner, string botName)
        {
            if (owner == null || string.IsNullOrEmpty(botName)) return null;
            
            return GetBotsForOwner(owner).FirstOrDefault(b => 
                string.Equals(b.Name, botName, StringComparison.OrdinalIgnoreCase));
        }

        public static GameBot GetActiveBotByName(string botName, eRealm realm)
        {
            if (string.IsNullOrWhiteSpace(botName))
                return null;
            return ActiveBots.Values.FirstOrDefault(bot => bot.Realm == realm && string.Equals(bot.Name, botName, StringComparison.OrdinalIgnoreCase));
        }
    }
}

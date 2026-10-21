using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Database.Attributes;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS
{
    /// <summary>
    /// A gamebot's guild membership (owner 2026-10-16/17): a player invites a gamebot into the player's guild; it shows
    /// in the guild list and social window, carries the guild name under its name (own realm only, enemies keep seeing
    /// its realm rank title), wears the guild emblem on cloak and shield (everyone sees the emblem), and talks in guild
    /// chat. /spawn companions never join: they only show their owner's guild name and emblem while with the owner.
    /// Membership is kept here, not on the bot's IGamePlayer Guild, so guild realm points, merit, keep claims and guild
    /// buffs never see a bot. A bot removed from the launcher's roster leaves its guild.
    /// </summary>
    public static class AutonomousBotGuilds
    {
        private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        public const string ViewPrefix = "bot:";
        public const int MemberRank = 9;

        private static readonly ConcurrentDictionary<long, OfflineWorldBotGuildRecord> Members = new();

        public static string ViewId(long botId) => ViewPrefix + botId;

        public static bool IsBotView(string internalId) => internalId?.StartsWith(ViewPrefix, StringComparison.Ordinal) == true;

        /// <summary>The guild a gamebot belongs to, or null.</summary>
        public static Guild GuildOf(long botId) =>
            Members.TryGetValue(botId, out var record) ? GuildMgr.GetGuildByGuildID(record.GuildID) : null;

        /// <summary>The guild whose name and emblem a bot shows: its own guild (gamebot) or its owner's (companion).</summary>
        public static Guild ShownGuild(GameBot bot)
        {
            if (bot == null) return null;
            if (bot.IsAutonomousWorldBot) return GuildOf(bot.DatabaseID);
            return bot.Owner?.Guild;
        }

        /// <summary>The cloak or shield texture a bot shows: its shown guild's emblem, otherwise the item's own.</summary>
        public static int Texture(GameLiving living, DbInventoryItem item)
        {
            int own = item.Emblem != 0 ? item.Emblem : item.Color;
            if (living is not GameBot bot || !CarriesEmblem(item)) return own;
            int emblem = ShownGuild(bot)?.Emblem ?? 0;
            return emblem != 0 ? emblem : own;
        }

        /// <summary>A cloak, or a real shield in the left hand (never a weapon).</summary>
        public static bool CarriesEmblem(DbInventoryItem item) =>
            item != null && (item.SlotPosition == (int)eInventorySlot.Cloak ||
                             item.SlotPosition == (int)eInventorySlot.LeftHandWeapon && item.Object_Type == (int)eObjectType.Shield);

        public static IEnumerable<long> MembersOf(Guild guild) =>
            guild == null ? [] : Members.Values.Where(m => m.GuildID == guild.GuildID).Select(m => m.BotId).ToArray();

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                Load();
            }
            catch (Exception exception)
            {
                Log.Error("Could not load gamebot guild memberships", exception);
            }
            _chatTimer?.Dispose();
            _chatTimer = new System.Threading.Timer(_ => NpcService.Instance.Post<int>(_ => AmbientGuildChat(), 0), null, 60_000, 60_000);
        }

        [GameServerStoppedEvent]
        public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
        {
            _chatTimer?.Dispose();
            _chatTimer = null;
        }

        private static void Load()
        {
            Members.Clear();
            var records = DOLDB<OfflineWorldBotGuildRecord>.SelectAllObjects();
            var stale = new List<OfflineWorldBotGuildRecord>();
            foreach (OfflineWorldBotGuildRecord record in records)
            {
                OfflineWorldBotRecord bot = DOLDB<OfflineWorldBotRecord>.SelectObject(DB.Column("BotId").IsEqualTo(record.BotId));
                Guild guild = GuildMgr.GetGuildByGuildID(record.GuildID);
                if (bot == null || bot.IsRetired || guild == null || (int)guild.Realm != bot.Realm)
                {
                    stale.Add(record);
                    continue;
                }
                record.BotName = bot.Name;
                Members[record.BotId] = record;
                GuildMgr.AddBotToGuildMemberViews(guild, ViewFor(record, bot, null));
            }
            if (stale.Count > 0)
                GameServer.Database.DeleteObject(stale);
            Log.Info($"BOT_GUILDS loaded members={Members.Count} removedStale={stale.Count}");
        }

        private static GuildMgr.GuildMemberView ViewFor(OfflineWorldBotGuildRecord record, OfflineWorldBotRecord stored, GameBot live)
        {
            var view = new GuildMgr.GuildMemberView(ViewId(record.BotId), live?.Name ?? stored?.Name ?? record.BotName,
                (live?.Level ?? stored?.Level ?? 1).ToString(), (live?.CharacterClass?.ID ?? stored?.ClassId ?? 0).ToString(),
                MemberRank.ToString(), "0", stored?.LastSavedUtc is { Length: >= 10 } saved ? saved[..10] : "Offline",
                string.Empty, GuildMgr.GetGuildByGuildID(record.GuildID)?.Name);
            if (live != null) Refresh(view, live);
            return view;
        }

        /// <summary>Brings a bot's social-window entry up to date (online: level, group, zone; offline: kept).</summary>
        public static void Refresh(GuildMgr.GuildMemberView view)
        {
            if (view == null || !IsBotView(view.InternalID)) return;
            if (long.TryParse(view.InternalID[ViewPrefix.Length..], out long botId) &&
                AutonomousBotRegistry.TryGet(botId, out GameBot bot) && bot.ObjectState == GameObject.eObjectState.Active)
                Refresh(view, bot);
            else
                view.GroupSize = "0";
        }

        private static void Refresh(GuildMgr.GuildMemberView view, GameBot bot)
        {
            view.Level = bot.Level.ToString();
            view.ClassID = (bot.CharacterClass?.ID ?? 0).ToString();
            view.Rank = MemberRank.ToString();
            view.GroupSize = bot.Group == null ? "1" : bot.Group.MemberCount.ToString();
            view.ZoneOrOnline = bot.CurrentZone?.Description ?? "Online";
        }

        /// <summary>The live gamebot members of these guilds (social window, online-only list).</summary>
        public static IEnumerable<GameBot> OnlineMembers(Guild guild)
        {
            foreach (long botId in MembersOf(guild))
                if (AutonomousBotRegistry.TryGet(botId, out GameBot bot) && bot.ObjectState == GameObject.eObjectState.Active)
                    yield return bot;
        }

        /// <summary>A member of <paramref name="guild"/> by name (for /gc remove).</summary>
        public static OfflineWorldBotGuildRecord FindMember(Guild guild, string name) =>
            guild == null || string.IsNullOrWhiteSpace(name) ? null :
            Members.Values.FirstOrDefault(m => m.GuildID == guild.GuildID && string.Equals(m.BotName, name.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>Why <paramref name="bot"/> cannot join the inviter's guild, or null when it can.</summary>
        public static string JoinRefusal(GamePlayer inviter, GameBot bot)
        {
            Guild guild = inviter?.Guild;
            if (guild == null) return "You are not in a guild.";
            if (bot == null || !bot.IsAlive) return "That character cannot be invited.";
            if (!bot.IsAutonomousWorldBot)
                return $"{bot.Name} is a temporary companion and cannot join a guild; companions already wear your guild's name and emblem while they are with you.";
            if (bot.Realm != guild.Realm) return "You can only invite characters of your own realm.";
            if (Members.TryGetValue(bot.DatabaseID, out var record))
                return record.GuildID == guild.GuildID ? $"{bot.Name} is already in your guild." : $"{bot.Name} is already in a guild.";
            return null;
        }

        public static bool Join(GamePlayer inviter, GameBot bot)
        {
            Guild guild = inviter?.Guild;
            if (JoinRefusal(inviter, bot) != null) return false;
            var record = new OfflineWorldBotGuildRecord
            {
                BotId = bot.DatabaseID,
                GuildID = guild.GuildID,
                BotName = bot.Name,
                JoinedUtc = DateTime.UtcNow.ToString("O"),
                InvitedBy = inviter.Name,
            };
            if (!GameServer.Database.AddObject(record)) return false;
            Members[bot.DatabaseID] = record;
            GuildMgr.AddBotToGuildMemberViews(guild, ViewFor(record, null, bot));
            Present(bot);
            guild.SendMessageToGuildMembers($"{bot.Name} has joined the guild!", eChatType.CT_Group, eChatLoc.CL_SystemWindow);
            Log.Info($"BOT_GUILD_JOIN bot={bot.Name} id={bot.DatabaseID} guild=\"{guild.Name}\" invitedBy={inviter.Name}");
            new ECSGameTimer(bot, _ =>
            {
                if (GuildOf(bot.DatabaseID) == guild && bot.IsAlive)
                    SpeakInGuild(bot, guild, Pick(JoinLines));
                return 0;
            }, 2_500);
            return true;
        }

        /// <summary>Removes a gamebot from its guild (booted, roster deletion, retirement, guild disbanded).</summary>
        public static bool Remove(long botId, string by, bool announce = true)
        {
            if (!Members.TryRemove(botId, out var record)) return false;
            Guild guild = GuildMgr.GetGuildByGuildID(record.GuildID);
            try
            {
                GameServer.Database.DeleteObject(record);
            }
            catch (Exception exception)
            {
                Log.Error($"Could not delete the guild membership of bot {botId}", exception);
            }
            if (guild != null)
            {
                GuildMgr.RemoveBotFromGuildMemberViews(guild, ViewId(botId));
                if (announce)
                    guild.SendMessageToGuildMembers(string.IsNullOrEmpty(by)
                        ? $"{record.BotName} has left the guild."
                        : $"{by} removed {record.BotName} from the guild.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
            if (AutonomousBotRegistry.TryGet(botId, out GameBot bot))
                Present(bot);
            Log.Info($"BOT_GUILD_REMOVE bot={record.BotName} id={botId} guild=\"{guild?.Name}\" by={by}");
            return true;
        }

        /// <summary>Removes every member whose bot is no longer on the roster (launcher deletion or retirement).</summary>
        public static int Reconcile(IReadOnlyCollection<long> rosterBotIds)
        {
            if (rosterBotIds == null || Members.IsEmpty) return 0;
            var keep = rosterBotIds as ISet<long> ?? new HashSet<long>(rosterBotIds);
            int removed = 0;
            foreach (long botId in Members.Keys.ToArray())
                if (!keep.Contains(botId) && Remove(botId, null))
                    removed++;
            return removed;
        }

        /// <summary>Every member of a disbanded guild leaves it.</summary>
        public static void OnGuildDeleted(Guild guild)
        {
            foreach (long botId in MembersOf(guild))
                Remove(botId, null, announce: false);
        }

        /// <summary>Name or emblem of a guild changed: its gamebots and its online players' companions show it.</summary>
        public static void OnGuildAppearanceChanged(Guild guild)
        {
            if (guild == null) return;
            foreach (GameBot bot in OnlineMembers(guild))
                Present(bot);
            foreach (GamePlayer player in guild.GetListOfOnlineMembers())
                PresentCompanions(player);
        }

        /// <summary>The owner joined, left or was removed from a guild: its companions follow.</summary>
        public static void PresentCompanions(GamePlayer owner)
        {
            if (owner == null) return;
            foreach (GameBot companion in BotManager.GetBotsForOwner(owner).ToArray())
                if (!companion.IsAutonomousWorldBot)
                    Present(companion);
        }

        /// <summary>Applies a bot's shown guild name and emblem; nearby players see it at once.</summary>
        public static void Present(GameBot bot)
        {
            if (bot == null) return;
            string name = ShownGuild(bot)?.Name ?? string.Empty;
            if (!string.Equals(bot.GuildName ?? string.Empty, name, StringComparison.Ordinal))
                bot.GuildName = name; // re-sends the bot to nearby players when it is in the world
            if (bot.ObjectState == GameObject.eObjectState.Active)
                bot.BroadcastLivingEquipmentUpdate();
        }

        /// <summary>The guild name before the bot enters the world (no broadcast needed).</summary>
        public static string GuildNameFor(GameBot bot) => ShownGuild(bot)?.Name ?? string.Empty;

        #region Guild chat

        private static System.Threading.Timer _chatTimer;
        private static readonly ConcurrentDictionary<string, long> LastGuildChat = new();
        private static readonly ConcurrentDictionary<string, long> LastGuildReply = new();

        private static readonly string[] JoinLines =
        [
            "Glad to be here. Thanks for the invite!", "Happy to ride with this guild.", "Thanks for having me, everyone.",
            "Proud to wear the colors. Call on me anytime.",
        ];

        private static readonly string[] GuildOpeners =
        [
            "Evening, guild. Anyone out hunting?", "Good hunting to all of you today.", "Does anyone need a hand with anything?",
            "Anyone want to group up later?", "Just checking in. All well with everyone?", "The roads have been busy today.",
            "Anyone heading to the frontier tonight?", "If anyone needs company for a dungeon, let me know.",
            "Nice to see the guild active today.", "Remember to train when you level, everyone.", "Stay safe out there, guild.",
            "Found a decent camp earlier. Anyone hunting nearby?",
        ];

        private static readonly string[] GuildReplies =
        [
            "Doing well, thanks.", "Out hunting myself right now.", "I could join a bit later.", "Same here, all quiet.",
            "Good to hear from you.", "Count me in if a group forms.", "Busy for now, but I will check back.",
            "Safe travels.", "Sounds good to me.", "Let me know where you end up.",
        ];

        private static string Pick(string[] lines) => lines[Random.Shared.Next(lines.Length)];

        /// <summary>A guild message in the same format players use (/gu).</summary>
        public static void SpeakInGuild(GameBot bot, Guild guild, string message)
        {
            if (bot == null || guild == null || string.IsNullOrWhiteSpace(message)) return;
            guild.SendMessageToGuildMembers($"[Guild] {bot.Name}: \"{message.Trim()}\"", eChatType.CT_Guild, eChatLoc.CL_ChatWindow);
        }

        private static bool CanTalk(GameBot bot) =>
            bot is { IsAlive: true, InCombat: false, IsCasting: false } && bot.ObjectState == GameObject.eObjectState.Active;

        /// <summary>Now and then a gamebot member opens a line in its guild's chat when a player of the guild is online.</summary>
        private static void AmbientGuildChat()
        {
            long now = GameLoop.GameLoopTime;
            foreach (string guildId in Members.Values.Select(m => m.GuildID).Distinct().ToArray())
            {
                Guild guild = GuildMgr.GetGuildByGuildID(guildId);
                if (guild == null || guild.GetListOfOnlineMembers().Count == 0) continue;
                if (LastGuildChat.TryGetValue(guildId, out long last) && now - last < 240_000) continue;
                if (Random.Shared.NextDouble() > 0.35) continue;
                GameBot[] talkers = OnlineMembers(guild).Where(CanTalk).ToArray();
                if (talkers.Length == 0) continue;
                LastGuildChat[guildId] = now;
                GameBot starter = talkers[Random.Shared.Next(talkers.Length)];
                SpeakInGuild(starter, guild, Pick(GuildOpeners));
                ScheduleReplies(guild, starter, null, Random.Shared.Next(0, 3));
            }
        }

        /// <summary>A player spoke in guild chat: gamebot members may answer (one directed answer when named).</summary>
        public static void OnPlayerGuildChat(GamePlayer player, string message)
        {
            Guild guild = player?.Guild;
            if (guild == null || string.IsNullOrWhiteSpace(message) || !Members.Values.Any(m => m.GuildID == guild.GuildID)) return;
            long now = GameLoop.GameLoopTime;
            if (LastGuildReply.TryGetValue(guild.GuildID, out long last) && now - last < 10_000) return;
            LastGuildReply[guild.GuildID] = now;
            GameBot[] talkers = OnlineMembers(guild).Where(CanTalk).ToArray();
            GameBot named = talkers.FirstOrDefault(bot => AutonomousBotChatCoordinator.ContainsMention(message, bot.Name));
            if (named != null)
            {
                ScheduleReply(named, guild, AutonomousBotChatCoordinator.GenerateFactionReply(message), 1_400);
                return;
            }
            ScheduleReplies(guild, null, message, AutonomousBotChatCoordinator.RollResponseCount(Math.Min(2, talkers.Length)));
        }

        private static void ScheduleReplies(Guild guild, GameBot starter, string incoming, int count)
        {
            GameBot[] talkers = OnlineMembers(guild).Where(bot => bot != starter && CanTalk(bot))
                .OrderBy(_ => Random.Shared.Next()).Take(count).ToArray();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < talkers.Length; index++)
            {
                string reply = incoming != null ? AutonomousBotChatCoordinator.GenerateFactionReply(incoming) : Pick(GuildReplies);
                if (!used.Add(reply)) continue;
                ScheduleReply(talkers[index], guild, reply, 1_800 + index * 2_000 + Random.Shared.Next(1_200));
            }
        }

        private static void ScheduleReply(GameBot bot, Guild guild, string reply, int delay)
        {
            new ECSGameTimer(bot, _ =>
            {
                if (CanTalk(bot) && GuildOf(bot.DatabaseID) == guild)
                    SpeakInGuild(bot, guild, reply);
                return 0;
            }, delay);
        }

        #endregion
    }

    /// <summary>One gamebot's guild membership (<see cref="AutonomousBotGuilds"/>). A separate table so the launcher's
    /// roster table is untouched; a bot that leaves the roster is removed from here.</summary>
    [DataTable(TableName = "offline_world_bot_guild")]
    public sealed class OfflineWorldBotGuildRecord : DataObject
    {
        [PrimaryKey] public long BotId { get; set; }
        [DataElement(AllowDbNull = false, Index = true)] public string GuildID { get; set; } = string.Empty;
        [DataElement(AllowDbNull = false)] public string BotName { get; set; } = string.Empty;
        [DataElement(AllowDbNull = false)] public string JoinedUtc { get; set; } = string.Empty;
        [DataElement(AllowDbNull = false)] public string InvitedBy { get; set; } = string.Empty;
    }
}

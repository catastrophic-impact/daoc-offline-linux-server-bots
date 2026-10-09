using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DOL.Events;
using DOL.Database;
using DOL.AI.Brain;
using DOL.Logging;

namespace DOL.GS.Quests
{
    /// <summary>
    /// Runtime support for the classic (1.65 and Shrouded Isles) quests added as data quests (goal 10).
    /// Their DbDataQuest rows name <see cref="ClassicQuestStep"/> as ClassType and their IDs are listed in
    /// classic-quests.json; everything else they need comes from that file next to the server, written by the quest generator:
    /// <list type="bullet">
    /// <item>the red map marker for the player's current step (the same relic-marker packet as bounties,
    /// reputation quests and the Sluaghbinder epic, in its own reserved ID so all can show at once);</item>
    /// <item>event monsters (shades, summoned bosses) that appear only when a player on that step reaches the
    /// spot, and leave again after the encounter;</item>
    /// <item>the quest monsters gamebots must leave alone (owner: "i dont want gamebots killing a quest npc and
    /// then it not being there for the player").</item>
    /// </list>
    /// Quest givers keep the normal yellow quest indicator over their heads. Without the file nothing happens.
    /// </summary>
    public static class ClassicQuests
    {
        public const uint MarkerId = 0xFFFE0004;
        public const string EventSpawnProperty = "ClassicQuestEventFor";
        private const string FileName = "classic-quests.json";
        private const int RefreshMilliseconds = 5000;
        private static readonly Logger Log = LoggerManager.Create(typeof(ClassicQuests));
        /// <summary>Markers on each player's map: quest id -> (step, point). Each quest has its own marker id.</summary>
        private static readonly ConcurrentDictionary<GamePlayer, ConcurrentDictionary<int, (int Step, Point Marker)>> Shown = new();
        private static readonly ConcurrentDictionary<(GamePlayer, int, int), List<GameNPC>> EventSpawns = new();
        private static readonly ConcurrentDictionary<(GamePlayer, int, int), long> EventCooldown = new();
        private static readonly ConcurrentDictionary<(GamePlayer, int, int), long> GiveCooldown = new();
        /// <summary>After an event monster dies or leaves without the step advancing, it can appear again after this.</summary>
        private const long EventRespawnMilliseconds = 30_000;
        private static Timer _timer;
        private static Config _config = new();
        private static DateTime _loadedWrite;

        public sealed record Point(ushort Region, int X, int Y, int Z);

        public sealed class EventSpawn
        {
            public string Name { get; set; }
            public int TemplateId { get; set; }
            public byte Level { get; set; }
            public ushort Model { get; set; }
            public ushort Region { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
            /// <summary>How close the player must come to the point before it appears; 0 = wherever the player is
            /// (monsters that come for the player when it talks to an NPC or kills the step before).</summary>
            public int TriggerRadius { get; set; } = 1500;
            /// <summary>How many appear together (two villainous youths come for Mandra).</summary>
            public int Count { get; set; } = 1;
            /// <summary>It leaves again after this long if nobody kills it.</summary>
            public int DespawnSeconds { get; set; } = 600;
            /// <summary>An NPC summoned for the player to talk to (the Enchantress calls up Lucan in Morven's Return):
            /// it appears at the point instead of beside the player, is of the player's realm and never attacks.</summary>
            public bool Peaceful { get; set; }
        }

        /// <summary>
        /// A step the walkthrough describes that DataQuest has no step type for (goal 10): it shows as a journal step and
        /// ClassicQuests completes it on the trigger. Kind: use_item (use Item within Radius of the point; Consume removes it),
        /// travel (reach the point), has_item (have Item: bought, traded or found), interact (interact with Target),
        /// quest (have finished Count of the "|"-separated quests in Quest; all when Count is 0), group (be in a group of
        /// two or more, companions count), die_to (be slain by Target). Radius 0 means anywhere in the region.
        /// trade (hand Target one of the Trades inputs; it is taken and that input's output, if any, given).
        /// Give: the step's Item is handed to a player on the step who does not have it (the quest provides it).
        /// Drops: monsters that drop an item for a player on this step (the trade inputs of kill tasks).
        /// </summary>
        public sealed class CustomStep
        {
            public string Kind { get; set; }
            public string Item { get; set; }
            public bool Consume { get; set; }
            public string Target { get; set; }
            public string Quest { get; set; }
            public ushort Region { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
            public int Radius { get; set; } = 600;
            public int Count { get; set; }
            public bool Give { get; set; }
            public Dictionary<string, string> Trades { get; set; }
            /// <summary>choose: reward name (the keyword the NPC offers) -> item Id_nb; whispering one to Target gives
            /// it and completes the step (classic "choose your reward" endings).</summary>
            public Dictionary<string, string> Choices { get; set; }
            public List<StepDrop> Drops { get; set; }
            /// <summary>say: the words of power spoken aloud (/say) on the step's point; NightOnly when the walkthrough
            /// has them work only at night.</summary>
            public string Words { get; set; }
            public bool NightOnly { get; set; }
        }

        public sealed class StepDrop
        {
            public string Mob { get; set; }
            public string Item { get; set; }
            public int Chance { get; set; } = 100;
        }

        public sealed class StepInfo
        {
            public CustomStep Custom { get; set; }
            /// <summary>Where the red marker points while this step is current (null: no marker).</summary>
            public Point Marker { get; set; }
            public EventSpawn Spawn { get; set; }
            /// <summary>Item (Id_nb) this step needs; if the player has lost it, <see cref="Issuer"/> hands out another.</summary>
            public string NeedsItem { get; set; }
            public string Issuer { get; set; }
            /// <summary>Items (Id_nb) the step before handed over (an NPC's gift in conversation, a named monster's drop):
            /// a player on this step who does not have one receives it, from <see cref="GrantFrom"/>.</summary>
            public List<string> Grant { get; set; }
            public string GrantFrom { get; set; }
        }

        public sealed class QuestInfo
        {
            /// <summary>1-based steps, index 0 unused.</summary>
            public List<StepInfo> Steps { get; set; } = new();
            /// <summary>Races the quest is offered to (empty: any). DataQuest has class limits but no race limits;
            /// Information is the Key (Celt) went to every Hibernian race, Sluaghbinders included (owner check 2026-10-07).</summary>
            public List<string> Races { get; set; } = new();
        }

        /// <summary>Whether the player's race may take this classic quest.</summary>
        public static bool RaceAllowed(int questId, GamePlayer player)
        {
            if (player == null || !_config.Quests.TryGetValue(questId, out QuestInfo info) || info.Races == null || info.Races.Count == 0)
                return true;
            string race = ((eRace)player.Race).ToString().Replace("_", "");
            return info.Races.Any(r => string.Equals(r.Replace(" ", ""), race, StringComparison.OrdinalIgnoreCase));
        }

        public sealed class Config
        {
            public Dictionary<int, QuestInfo> Quests { get; set; } = new();
            /// <summary>Mob_IDs of named quest monsters living in the world (gamebots leave them alone).</summary>
            public HashSet<string> QuestMonsterIds { get; set; } = new(StringComparer.Ordinal);
            /// <summary>The walkthroughs' NPC conversation: NPC name -> clicked/whispered keyword -> what the NPC answers
            /// (owner 2026-10-07: the quests had only a bare offer; the classic [keyword] chains were missing).</summary>
            public Dictionary<string, Dictionary<string, string>> Chat { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            Reload();
            _timer?.Dispose();
            _timer = new Timer(Refresh, null, RefreshMilliseconds, RefreshMilliseconds);
            GameEventMgr.AddHandler(GamePlayerEvent.InteractWith, new DOLEventHandler(OnInteract));
            GameEventMgr.AddHandler(GamePlayerEvent.Whisper, new DOLEventHandler(OnWhisper));
            GameEventMgr.AddHandler(GameLivingEvent.Say, new DOLEventHandler(OnSay));
            GameEventMgr.AddHandler(GamePlayerEvent.UseSlot, new DOLEventHandler(OnUseSlot));
            GameEventMgr.AddHandler(GameLivingEvent.Dying, new DOLEventHandler(OnDying));
            GameEventMgr.AddHandler(GamePlayerEvent.GiveItem, new DOLEventHandler(OnGiveItem));
        }

        /// <summary>
        /// A player on a classic quest step who no longer has the step's item (dropped, destroyed, sold) gets a new
        /// one from the step's NPC, so no quest can get stuck on a lost item.
        /// </summary>
        /// <summary>A keyword clicked or whispered to a classic quest NPC gets the walkthrough's next line. The quest's own
        /// accept keyword (its name) is left to DataQuest.</summary>
        private static void OnWhisper(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                if (sender is not GamePlayer player || args is not WhisperEventArgs whisper || whisper.Target is not GameNPC npc) return;
                string key = (whisper.Text ?? string.Empty).Trim().TrimEnd('.', '!', '?');
                if (TryChooseReward(player, npc, key)) return;
                if (!_config.Chat.TryGetValue(npc.Name, out var lines)) return;
                if (lines.TryGetValue(key, out string reply) && !string.IsNullOrWhiteSpace(reply))
                    npc.SayTo(player, PacketHandler.eChatLoc.CL_PopupWindow, DOL.GS.Behaviour.BehaviourUtils.GetPersonalizedMessage(reply, player));
            }
            catch (Exception ex)
            {
                Log.Error("Classic quest conversation failed", ex);
            }
        }

        /// <summary>A say step completes when the player speaks its words aloud on the step's point ("at night, speak
        /// the words at the summoning circle"). Owner 2026-10-07: La Morti Parla and Legione Perso were blocked on it.</summary>
        private static void OnSay(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                if (sender is not GamePlayer player || args is not SayEventArgs said || args is WhisperEventArgs) return;
                string spoken = WordsOnly(said.Text);
                if (spoken.Length == 0) return;
                foreach (DataQuest quest in player.QuestList.Keys.OfType<DataQuest>().ToArray())
                {
                    if (!_config.Quests.TryGetValue(quest.ID, out QuestInfo info) || quest.Step <= 0 || quest.Step >= info.Steps.Count) continue;
                    CustomStep custom = info.Steps[quest.Step]?.Custom;
                    if (custom?.Kind != "say" || string.IsNullOrWhiteSpace(custom.Words) || !spoken.Contains(WordsOnly(custom.Words))) continue;
                    if (!InPlace(player, custom))
                    {
                        player.Out.SendMessage("Nothing answers. The words must be spoken at the right place.", PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_SystemWindow);
                        continue;
                    }
                    if (custom.NightOnly && player.CurrentRegion?.IsNightTime == false)
                    {
                        player.Out.SendMessage("Nothing answers. The words of power work only at night.", PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_SystemWindow);
                        continue;
                    }
                    quest.AdvanceByScript(player);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Classic quest words of power failed", ex);
            }
        }

        /// <summary>Lower-case words separated by single spaces (punctuation and spacing do not matter when speaking).</summary>
        public static string WordsOnly(string text) =>
            string.Join(' ', System.Text.RegularExpressions.Regex.Matches((text ?? string.Empty).ToLowerInvariant(), "[a-z0-9']+").Select(m => m.Value));

        private static bool TryChooseReward(GamePlayer player, GameNPC npc, string key)
        {
            foreach (DataQuest quest in player.QuestList.Keys.OfType<DataQuest>().ToArray())
            {
                if (!_config.Quests.TryGetValue(quest.ID, out QuestInfo info) || quest.Step <= 0 || quest.Step >= info.Steps.Count) continue;
                CustomStep custom = info.Steps[quest.Step]?.Custom;
                if (custom?.Kind != "choose" || custom.Choices == null || !string.Equals(custom.Target, npc.Name, StringComparison.OrdinalIgnoreCase)) continue;
                string chosen = custom.Choices.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (chosen == null) continue;
                DbItemTemplate template = GameServer.Database.FindObjectByKey<DbItemTemplate>(custom.Choices[chosen]);
                if (template == null) continue;
                if (!player.Inventory.IsSlotsFree(1, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack))
                {
                    player.Out.SendMessage("Your backpack is full.", PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_SystemWindow);
                    return true;
                }
                if (player.Inventory.AddTemplate(GameInventoryItem.Create(template), 1, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack))
                    player.Out.SendMessage($"{npc.GetName(0, true)} gives you {template.GetName(1, false)}.", PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_SystemWindow);
                quest.AdvanceByScript(npc);
                return true;
            }
            return false;
        }

        private static void GrantItems(GamePlayer player, DataQuest quest, StepInfo step)
        {
            for (int i = 0; i < step.Grant.Count; i++)
            {
                string id = step.Grant[i];
                if (string.IsNullOrEmpty(id) || player.Inventory.GetFirstItemByID(id, eInventorySlot.Min_Inv, eInventorySlot.Max_Inv) != null) continue;
                var key = (player, quest.ID, quest.Step * 100 + i + 1);
                long now = GameLoop.GameLoopTime;
                if (GiveCooldown.TryGetValue(key, out long until) && now < until) continue;
                GiveCooldown[key] = now + 60_000;
                DbItemTemplate template = GameServer.Database.FindObjectByKey<DbItemTemplate>(id);
                if (template == null || !player.Inventory.AddTemplate(GameInventoryItem.Create(template), 1, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack)) continue;
                string from = string.IsNullOrEmpty(step.GrantFrom) ? "You receive" : step.GrantFrom + " gives you";
                player.Out.SendMessage($"{from} {template.GetName(1, false)}.", PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_SystemWindow);
            }
        }

        /// <summary>Passive custom triggers, checked on the refresh tick: a place reached, an item held, a quest finished.</summary>
        private static void CheckCustomStep(GamePlayer player, DataQuest quest, CustomStep custom)
        {
            if (custom.Give && !string.IsNullOrEmpty(custom.Item) &&
                player.Inventory.GetFirstItemByID(custom.Item, eInventorySlot.Min_Inv, eInventorySlot.Max_Inv) == null)
            {
                long now = GameLoop.GameLoopTime;
                var key = (player, quest.ID, quest.Step);
                if (!GiveCooldown.TryGetValue(key, out long until) || now >= until)
                {
                    GiveCooldown[key] = now + 60_000;
                    DbItemTemplate template = GameServer.Database.FindObjectByKey<DbItemTemplate>(custom.Item);
                    if (template != null && player.Inventory.AddTemplate(GameInventoryItem.Create(template), 1, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack))
                        player.Out.SendMessage($"You have the {template.Name} for {quest.Name}.", PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_SystemWindow);
                }
            }
            bool done = custom.Kind switch
            {
                "travel" => InPlace(player, custom),
                "group" => player.Group != null && player.Group.MemberCount >= 2,
                "has_item" => !string.IsNullOrEmpty(custom.Item) &&
                              player.Inventory.GetFirstItemByID(custom.Item, eInventorySlot.Min_Inv, eInventorySlot.Max_Inv) != null,
                "quest" => !string.IsNullOrEmpty(custom.Quest) && FinishedCount(player, custom.Quest) >= QuestsNeeded(custom.Quest, custom.Count),
                _ => false,
            };
            if (done) quest.AdvanceByScript(player);
        }

        /// <summary>On the map point (flat distance: walkthrough locations carry no height), or anywhere in the region.</summary>
        private static bool InPlace(GamePlayer player, CustomStep custom)
        {
            if (custom.Region == 0) return true;
            if (player.CurrentRegionID != custom.Region) return false;
            if (custom.Radius <= 0) return true;
            long dx = player.X - custom.X, dy = player.Y - custom.Y;
            return dx * dx + dy * dy <= (long)custom.Radius * custom.Radius;
        }

        private static int QuestsNeeded(string quests, int count) => count > 0 ? count : quests.Split('|', StringSplitOptions.RemoveEmptyEntries).Length;

        private static int FinishedCount(GamePlayer player, string quests)
        {
            var finished = new HashSet<string>(player.GetFinishedQuests().Select(q => q.Name), StringComparer.OrdinalIgnoreCase);
            return quests.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Count(finished.Contains);
        }

        /// <summary>A die_to step completes when the player is slain by the step's NPC (a willing sacrifice).</summary>
        private static void OnDying(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                if (sender is GameNPC mob && args is DyingEventArgs slain)
                {
                    DropForSteps(mob, slain);
                    return;
                }
                if (sender is not GamePlayer player || args is not DyingEventArgs dying || dying.Killer == null) return;
                foreach (DataQuest quest in player.QuestList.Keys.OfType<DataQuest>().ToArray())
                {
                    if (!_config.Quests.TryGetValue(quest.ID, out QuestInfo info) || quest.Step <= 0 || quest.Step >= info.Steps.Count) continue;
                    CustomStep custom = info.Steps[quest.Step]?.Custom;
                    if (custom?.Kind == "die_to" && string.Equals(custom.Target, dying.Killer.Name, StringComparison.OrdinalIgnoreCase))
                        quest.AdvanceByScript(dying.Killer);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Classic quest death step failed", ex);
            }
        }

        /// <summary>Kill-task drops: a monster slain by a player (or the player's pet) on a step that lists it drops that
        /// step's item into the player's backpack, like DataQuest's own kill-step drops.</summary>
        private static void DropForSteps(GameNPC mob, DyingEventArgs slain)
        {
            var killers = new HashSet<GamePlayer>();
            if (slain.PlayerKillers != null) killers.UnionWith(slain.PlayerKillers);
            if (slain.Killer is GamePlayer direct) killers.Add(direct);
            else if (slain.Killer is GameNPC pet && pet.Brain is IControlledBrain controlled && controlled.GetPlayerOwner() is GamePlayer owner) killers.Add(owner);
            foreach (GamePlayer player in killers)
            {
                if (!player.IsWithinRadius(mob, WorldMgr.VISIBILITY_DISTANCE)) continue;
                foreach (DataQuest quest in player.QuestList.Keys.OfType<DataQuest>().ToArray())
                {
                    if (!_config.Quests.TryGetValue(quest.ID, out QuestInfo info) || quest.Step <= 0 || quest.Step >= info.Steps.Count) continue;
                    List<StepDrop> drops = info.Steps[quest.Step]?.Custom?.Drops;
                    if (drops == null) continue;
                    foreach (StepDrop drop in drops)
                    {
                        if (!string.Equals(drop.Mob, mob.Name, StringComparison.OrdinalIgnoreCase) || !Util.Chance(drop.Chance)) continue;
                        DbItemTemplate template = GameServer.Database.FindObjectByKey<DbItemTemplate>(drop.Item);
                        if (template != null && player.Inventory.AddTemplate(GameInventoryItem.Create(template), 1, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack))
                            player.Out.SendMessage($"You take the {template.Name} from {mob.GetName(0, false)}.", PacketHandler.eChatType.CT_Loot, PacketHandler.eChatLoc.CL_SystemWindow);
                    }
                }
            }
        }

        /// <summary>A trade step: handing its NPC one of the wanted items takes it, gives what it is traded for, and
        /// completes the step (a kill task's turn-in).</summary>
        private static void OnGiveItem(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                if (args is not GiveItemEventArgs give || give.Source is not GamePlayer player || give.Target is not GameNPC npc || give.Item == null) return;
                foreach (DataQuest quest in player.QuestList.Keys.OfType<DataQuest>().ToArray())
                {
                    if (!_config.Quests.TryGetValue(quest.ID, out QuestInfo info) || quest.Step <= 0 || quest.Step >= info.Steps.Count) continue;
                    CustomStep custom = info.Steps[quest.Step]?.Custom;
                    if (custom?.Kind != "trade" || custom.Trades == null || !string.Equals(custom.Target, npc.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    string wanted = custom.Trades.Keys.FirstOrDefault(k => string.Equals(k, give.Item.Id_nb, StringComparison.OrdinalIgnoreCase));
                    if (wanted == null) continue;
                    string output = custom.Trades[wanted];
                    DbItemTemplate reward = string.IsNullOrEmpty(output) ? null : GameServer.Database.FindObjectByKey<DbItemTemplate>(output);
                    if (reward != null && !player.Inventory.IsSlotsFree(1, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack))
                    {
                        player.Out.SendMessage("Your backpack is full.", PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_SystemWindow);
                        return;
                    }
                    player.Inventory.RemoveCountFromStack(give.Item, 1);
                    if (reward != null && player.Inventory.AddTemplate(GameInventoryItem.Create(reward), 1, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack))
                        player.Out.SendMessage($"{npc.GetName(0, true)} gives you {reward.GetName(1, false)}.", PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_SystemWindow);
                    quest.AdvanceByScript(npc);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Classic quest trade failed", ex);
            }
        }

        /// <summary>An item used (clicked or /use) where a step wants it used: the step completes.</summary>
        private static void OnUseSlot(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                if (sender is not GamePlayer player || args is not UseSlotEventArgs use) return;
                DbInventoryItem item = player.Inventory.GetItem((eInventorySlot)use.Slot);
                if (item == null) return;
                foreach (DataQuest quest in player.QuestList.Keys.OfType<DataQuest>().ToArray())
                {
                    if (!_config.Quests.TryGetValue(quest.ID, out QuestInfo info) || quest.Step <= 0 || quest.Step >= info.Steps.Count) continue;
                    CustomStep custom = info.Steps[quest.Step]?.Custom;
                    if (custom?.Kind != "use_item" || !string.Equals(custom.Item, item.Id_nb, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!InPlace(player, custom))
                    {
                        player.Out.SendMessage($"Nothing happens. This is not the place to use the {item.Name}.", PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_SystemWindow);
                        continue;
                    }
                    if (custom.Consume) player.Inventory.RemoveCountFromStack(item, 1);
                    quest.AdvanceByScript(player);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Classic quest item use failed", ex);
            }
        }

        private static void OnInteract(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                if (sender is GamePlayer who && args is InteractWithEventArgs touched && touched.Target != null)
                    foreach (DataQuest quest in who.QuestList.Keys.OfType<DataQuest>().ToArray())
                    {
                        if (!_config.Quests.TryGetValue(quest.ID, out QuestInfo qi) || quest.Step <= 0 || quest.Step >= qi.Steps.Count) continue;
                        CustomStep custom = qi.Steps[quest.Step]?.Custom;
                        if (custom?.Kind == "interact" && string.Equals(custom.Target, touched.Target.Name, StringComparison.OrdinalIgnoreCase))
                            quest.AdvanceByScript(touched.Target);
                    }
                if (sender is not GamePlayer player || args is not InteractWithEventArgs interact || interact.Target is not GameNPC npc) return;
                foreach (DataQuest quest in player.QuestList.Keys.OfType<DataQuest>())
                {
                    if (!_config.Quests.TryGetValue(quest.ID, out QuestInfo info) || quest.Step <= 0 || quest.Step >= info.Steps.Count) continue;
                    StepInfo step = info.Steps[quest.Step];
                    if (string.IsNullOrEmpty(step?.NeedsItem) || !string.Equals(step.Issuer, npc.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (player.Inventory.GetFirstItemByID(step.NeedsItem, eInventorySlot.Min_Inv, eInventorySlot.Max_Inv) != null) continue;
                    DbItemTemplate template = GameServer.Database.FindObjectByKey<DbItemTemplate>(step.NeedsItem);
                    if (template == null) continue;
                    if (player.Inventory.AddTemplate(GameInventoryItem.Create(template), 1, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack))
                        player.Out.SendMessage($"{npc.GetName(0, true)} gives you another {template.Name}.", PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_SystemWindow);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Classic quest item re-issue failed", ex);
            }
        }

        [GameServerStoppedEvent]
        public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
        {
            _timer?.Dispose();
            _timer = null;
            GameEventMgr.RemoveHandler(GamePlayerEvent.InteractWith, new DOLEventHandler(OnInteract));
            GameEventMgr.RemoveHandler(GamePlayerEvent.Whisper, new DOLEventHandler(OnWhisper));
            GameEventMgr.RemoveHandler(GamePlayerEvent.UseSlot, new DOLEventHandler(OnUseSlot));
            GameEventMgr.RemoveHandler(GameLivingEvent.Dying, new DOLEventHandler(OnDying));
            GameEventMgr.RemoveHandler(GamePlayerEvent.GiveItem, new DOLEventHandler(OnGiveItem));
            Shown.Clear();
            foreach (GameNPC npc in EventSpawns.Values.SelectMany(list => list)) npc?.Delete();
            EventSpawns.Clear();
        }

        /// <summary>
        /// A classic quest monster: a named quest monster from the generator's list or a live event spawn.
        /// Gamebot camp goals, bounty and charm pools skip these, and gamebots only fight them in self-defense.
        /// </summary>
        public static bool IsQuestMonster(GameNPC npc) =>
            npc != null && (npc.TempProperties.GetProperty<object>(EventSpawnProperty) != null ||
                            (!string.IsNullOrEmpty(npc.InternalID) && _config.QuestMonsterIds.Contains(npc.InternalID)));

        public static bool IsQuestMonsterId(string mobId) =>
            !string.IsNullOrEmpty(mobId) && _config.QuestMonsterIds.Contains(mobId);

        public static void Reload()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, FileName);
                if (!File.Exists(path)) return;
                DateTime write = File.GetLastWriteTimeUtc(path);
                if (write == _loadedWrite) return;
                Config config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (config == null) return;
                config.QuestMonsterIds = new HashSet<string>(config.QuestMonsterIds ?? new(), StringComparer.Ordinal);
                _config = config;
                _loadedWrite = write;
                if (Log.IsInfoEnabled)
                    Log.Info($"CLASSIC_QUESTS loaded quests={config.Quests.Count} questMonsters={config.QuestMonsterIds.Count}");
            }
            catch (Exception ex)
            {
                Log.Error("Could not load classic-quests.json; classic quest markers and event spawns are off", ex);
            }
        }

        private static void Refresh(object state)
        {
            try
            {
                Reload();
                foreach (GamePlayer player in ClientService.Instance.GetPlayers())
                {
                    if (player?.ObjectState != GameObject.eObjectState.Active) continue;
                    DataQuest[] active = player.QuestList.Keys.OfType<DataQuest>()
                        .Where(dq => _config.Quests.ContainsKey(dq.ID)).ToArray();
                    // A quest given up (or finished) takes its monsters with it and forgets the wait, so taking it
                    // again spawns them fresh (owner 2026-10-07).
                    foreach (var key in EventSpawns.Keys.Where(k => k.Item1 == player && active.All(q => q.ID != k.Item2)).ToArray())
                        if (EventSpawns.TryRemove(key, out var gone))
                            foreach (GameNPC npc in gone) if (npc?.ObjectState == GameObject.eObjectState.Active) npc.Delete();
                    foreach (var key in EventCooldown.Keys.Where(k => k.Item1 == player && active.All(q => q.ID != k.Item2)).ToArray())
                        EventCooldown.TryRemove(key, out _);
                    // Every active classic quest shows its current step on the map, each with its own marker
                    // (owner 2026-10-07: one shared marker hid every quest behind the first one), and every active
                    // quest's step can spawn its monster.
                    foreach (int gone in (Shown.TryGetValue(player, out var onMap) ? onMap.Keys : Enumerable.Empty<int>())
                                 .Where(id => active.All(q => q.ID != id)).ToArray())
                        ShowMarker(player, gone, 0, null);
                    foreach (DataQuest quest in active)
                    {
                        QuestInfo info = _config.Quests[quest.ID];
                        StepInfo step = quest.Step > 0 && quest.Step < info.Steps.Count ? info.Steps[quest.Step] : null;
                        ShowMarker(player, quest.ID, quest.Step, step?.Marker);
                        if (step?.Spawn != null) TrySpawnEvent(player, quest.ID, quest.Step, step.Spawn);
                        if (step?.Grant != null) GrantItems(player, quest, step);
                        if (step?.Custom != null) CheckCustomStep(player, quest, step.Custom);
                    }
                }
                foreach (var entry in EventSpawns.ToArray())
                {
                    lock (entry.Value) entry.Value.RemoveAll(npc => npc == null || npc.ObjectState != GameObject.eObjectState.Active || !npc.IsAlive);
                    if (entry.Value.Count > 0) continue;
                    EventSpawns.TryRemove(entry.Key, out _);
                    EventCooldown[entry.Key] = GameLoop.GameLoopTime + EventRespawnMilliseconds;
                }
                foreach (GamePlayer gone in Shown.Keys.Where(p => p.ObjectState != GameObject.eObjectState.Active).ToArray())
                    Shown.TryRemove(gone, out _);
            }
            catch (Exception ex)
            {
                Log.Error("Classic quest refresh failed", ex);
            }
        }

        /// <summary>A classic quest's own marker id: MarkerId for the first quest id, one more per id after it (the
        /// bounty, reputation and Sluaghbinder markers sit below MarkerId).</summary>
        public static uint QuestMarkerId(int questId) => MarkerId + (uint)Math.Max(0, questId - 20000);

        private static void ShowMarker(GamePlayer player, int quest, int step, Point marker)
        {
            var onMap = Shown.GetOrAdd(player, _ => new ConcurrentDictionary<int, (int Step, Point Marker)>());
            uint id = QuestMarkerId(quest);
            if (marker == null)
            {
                if (onMap.TryRemove(quest, out _)) player.Out.SendMinotaurRelicMapRemove(id);
                return;
            }
            if (onMap.TryGetValue(quest, out var shown) && (shown.Step != step || shown.Marker != marker))
                player.Out.SendMinotaurRelicMapRemove(id);
            onMap[quest] = (step, marker);
            player.Out.SendMinotaurRelicMapUpdate(id, marker.Region, marker.X, marker.Y, marker.Z);
        }

        private static void ClearMarker(GamePlayer player)
        {
            if (!Shown.TryRemove(player, out var onMap)) return;
            foreach (int quest in onMap.Keys) player.Out.SendMinotaurRelicMapRemove(QuestMarkerId(quest));
        }

        /// <summary>
        /// Spawns this step's monster(s) beside the player, at the walkthrough's level, attacking it (owner 2026-10-07).
        /// Kills count for the step: the kill target is matched by name. Nothing spawns while one of this quest's monsters
        /// of that name is still alive (the second of "two youths" is already there when its step comes up).
        /// </summary>
        /// <summary>A walkable point 600-900 units from the player (behind it first), else nearer, else the player's spot.</summary>
        private static System.Numerics.Vector3 ApproachPoint(GamePlayer player, int index)
        {
            var origin = new System.Numerics.Vector3(player.X, player.Y, player.Z);
            Zone zone = player.CurrentZone;
            var nav = PathfindingProvider.Instance;
            bool meshed = zone != null && nav.IsAvailable && nav.HasNavmesh(zone);
            foreach (float distance in new[] { 750f, 600f, 900f, 400f })
                for (int turn = 0; turn < 8; turn++)
                {
                    double angle = player.Heading * 2 * Math.PI / 4096 + Math.PI + (index * 0.7) + turn * (Math.PI / 4);
                    var want = origin + new System.Numerics.Vector3((float)(-Math.Sin(angle) * distance), (float)(Math.Cos(angle) * distance), 0);
                    if (!meshed) return want;
                    var floor = nav.GetClosestPoint(zone, want, 160, 160, 400, nav.DefaultFilters);
                    if (floor.HasValue && Math.Abs(floor.Value.Z - origin.Z) < 500 &&
                        nav.HasLineOfSight(zone, floor.Value + new System.Numerics.Vector3(0, 0, 60), origin + new System.Numerics.Vector3(0, 0, 60), nav.DefaultFilters))
                        return floor.Value;
                }
            return origin;
        }

        private static void TrySpawnEvent(GamePlayer player, int quest, int step, EventSpawn spawn)
        {
            var key = (player, quest, step);
            if (!player.IsAlive || EventCooldown.TryGetValue(key, out long until) && GameLoop.GameLoopTime < until) return;
            if (EventSpawns.ContainsKey(key) || EventSpawns.Any(e => e.Key.Item1 == player && e.Key.Item2 == quest &&
                    e.Value.Any(n => n?.IsAlive == true && string.Equals(n.Name, spawn.Name, StringComparison.OrdinalIgnoreCase)))) return;
            if (spawn.TriggerRadius > 0 && (player.CurrentRegionID != spawn.Region ||
                !player.IsWithinRadius(new Point3D(spawn.X, spawn.Y, spawn.Z), spawn.TriggerRadius))) return;
            var spawned = new List<GameNPC>();
            for (int i = 0; i < Math.Max(1, spawn.Count); i++)
            {
                var npc = new GameNPC();
                if (spawn.TemplateId > 0 && NpcTemplateMgr.GetTemplate(spawn.TemplateId) is INpcTemplate template)
                    npc.LoadTemplate(template);
                npc.Name = spawn.Name;
                if (spawn.Level > 0) npc.Level = spawn.Level;
                if (spawn.Model > 0) npc.Model = spawn.Model;
                npc.Realm = spawn.Peaceful ? player.Realm : eRealm.None;
                // A short way off on walkable ground, then they come at the player (owner 2026-10-07: spawning on top
                // of the player looked wrong). A summoned NPC stands at its point, beside the NPC that summons it.
                var at = spawn.Peaceful ? new System.Numerics.Vector3(spawn.X + 90 + 60 * i, spawn.Y, spawn.Z) : ApproachPoint(player, i);
                npc.CurrentRegionID = spawn.Peaceful ? spawn.Region : player.CurrentRegionID;
                npc.X = (int)at.X; npc.Y = (int)at.Y; npc.Z = (int)at.Z;
                npc.Heading = npc.GetHeading(player);
                npc.RespawnInterval = -1; // never respawns: one encounter for this player's step
                npc.TempProperties.SetProperty(EventSpawnProperty, player.Name);
                if (spawn.Peaceful)
                {
                    npc.Flags |= GameNPC.eFlags.PEACE;
                    if (npc.Brain is DOL.AI.Brain.StandardMobBrain calm) calm.AggroLevel = 0;
                }
                if (!npc.AddToWorld()) continue;
                if (!spawn.Peaceful && npc.Brain is DOL.AI.Brain.StandardMobBrain brain) brain.AddToAggroList(player, 1);
                spawned.Add(npc);
                new ECSGameTimer(npc, _ => { if (npc.ObjectState == GameObject.eObjectState.Active) npc.Delete(); return 0; },
                    Math.Max(60, spawn.DespawnSeconds) * 1000);
            }
            if (spawned.Count == 0) return;
            EventSpawns[key] = spawned;
            if (Log.IsInfoEnabled)
                Log.Info($"CLASSIC_QUEST_EVENT_SPAWN quest={quest} step={step} for={player.Name} npc=\"{spawn.Name}\" count={spawned.Count} " +
                         $"level={spawned[0].Level} region={player.CurrentRegionID} at={player.X},{player.Y},{player.Z}");
        }
    }

    /// <summary>The ClassType hook named by classic data quests; markers and spawns live in <see cref="ClassicQuests"/>.</summary>
    public class ClassicQuestStep : IDataQuestStep
    {
        public bool Execute(DataQuest dataQuest, GamePlayer player, int step, eStepCheckType stepCheckType) =>
            stepCheckType != eStepCheckType.Qualification || ClassicQuests.RaceAllowed(dataQuest.ID, player);
    }
}

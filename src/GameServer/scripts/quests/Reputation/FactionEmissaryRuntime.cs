using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.Quests;

namespace DOL.GS
{
    /// <summary>One realm's Shrouded Isles neutral faction and where its emissary stands.</summary>
    public sealed record FactionEmissaryDefinition(
        eRealm Realm, int FactionId, string FactionName, ushort HuntRegionId,
        string Name, ushort RegionId, int X, int Y, int Z, ushort Model, byte Size, eGender Gender,
        string HostStableMaster, string TownName, string MountName, ushort? Heading = null);

    /// <summary>Quest marker for a reputation emissary.</summary>
    public sealed class FactionEmissaryNPC : GameNPC
    {
        public override eQuestIndicator GetQuestIndicator(GamePlayer player)
        {
            if (player == null || player.Realm != Realm)
                return eQuestIndicator.None;

            if (player.IsDoingQuest(typeof(FactionReputationQuest)) is FactionReputationQuest active)
                return active.IsReady ? eQuestIndicator.Finish : eQuestIndicator.None;

            return CanGiveQuest(typeof(FactionReputationQuest), player) > 0
                ? eQuestIndicator.Available
                : eQuestIndicator.None;
        }
    }

    /// <summary>
    /// Three script-owned emissaries, one beside the first reputation-gated
    /// stable master each realm meets in the Shrouded Isles. They give the
    /// repeatable reputation hunt, and every gated stable master that refuses
    /// a player points them here.
    /// </summary>
    public static class FactionEmissaryRuntime
    {
        public const int RequiredAggroForStables = 50;

        // Each spot is on open navmesh in line of sight of its stable master
        // (300 units from Zrrazk, 250 from Vilmalin, 150 from Korlis), with
        // no client fixture within 1,100 units and no other NPC within 500
        // (zone186/zone056/zone154 nav and fixtures, 2026-09-30). Models are
        // client monsters.csv: 684 Praying Mantis, 829 Lammia, 187 Dwarf Male.
        public static readonly FactionEmissaryDefinition[] Definitions =
        {
            new(eRealm.Hibernia, 89, "Krrzck", 181,
                "Kzzirrak", 181, 427497, 316775, 3417, 684, 45, eGender.Neutral,
                "Zrrazk", "Necht", "wyverns"),
            new(eRealm.Albion, 16, "Cryptos Mythicos", 51,
                "Ysslith", 51, 400261, 503487, 4665, 829, 51, eGender.Female,
                "Vilmalin", "Caer Diogel", "dragonflies"),
            new(eRealm.Midgard, 172, "The Remnants", 151,
                "Hrodvar Deepvow", 151, 380304, 383359, 7754, 187, 50, eGender.Male,
                // Korlis stands against the Hagall wall, so facing him means
                // facing the wall. Look out over the open ground (headings
                // 1280-3328 on zone154.nav), turned about 25 degrees toward him.
                "Korlis", "Hagall", "gryphons", Heading: 2590),
        };

        private static readonly Dictionary<eRealm, FactionEmissaryNPC> Emissaries = new();

        public static FactionEmissaryDefinition GetDefinition(eRealm realm) =>
            Definitions.FirstOrDefault(definition => definition.Realm == realm);

        public static FactionEmissaryDefinition GetDefinitionForFaction(int factionId) =>
            Definitions.FirstOrDefault(definition => definition.FactionId == factionId);

        public static FactionEmissaryNPC GetEmissary(eRealm realm) =>
            Emissaries.TryGetValue(realm, out FactionEmissaryNPC npc) ? npc : null;

        [ScriptLoadedEvent]
        public static void ScriptLoaded(DOLEvent e, object sender, EventArgs args)
        {
            if (!ServerProperties.Properties.LOAD_QUESTS)
                return;

            foreach (FactionEmissaryDefinition definition in Definitions)
                AddEmissary(definition);

            GameEventMgr.AddHandler(GamePlayerEvent.AcceptQuest, AcceptQuest);
            GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, PlayerEntered);
            GameEventMgr.AddHandler(GamePlayerEvent.Quit, PlayerQuit);
        }

        [ScriptUnloadedEvent]
        public static void ScriptUnloaded(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.RemoveHandler(GamePlayerEvent.AcceptQuest, AcceptQuest);
            GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, PlayerEntered);
            GameEventMgr.RemoveHandler(GamePlayerEvent.Quit, PlayerQuit);

            foreach (FactionEmissaryNPC emissary in Emissaries.Values)
            {
                GameEventMgr.RemoveHandler(emissary, GameObjectEvent.Interact, TalkToEmissary);
                GameEventMgr.RemoveHandler(emissary, GameLivingEvent.WhisperReceive, TalkToEmissary);
                emissary.RemoveQuestToGive(typeof(FactionReputationQuest));
                if (emissary.ObjectState == GameObject.eObjectState.Active)
                    emissary.Delete();
            }

            Emissaries.Clear();
        }

        public static void UpdateIndicator(GamePlayer player)
        {
            FactionEmissaryNPC emissary = player == null ? null : GetEmissary(player.Realm);
            if (emissary != null)
                player.Out.SendNPCsQuestEffect(emissary, emissary.GetQuestIndicator(player));
        }

        /// <summary>Reputation is shown as trust: the negative of faction hostility, -100 to 100.</summary>
        public static string DescribeStanding(int aggro)
        {
            string standing = Faction.StandingForAggro(aggro) switch
            {
                Faction.Standing.AGGRESIVE => "Aggressive",
                Faction.Standing.HOSTILE => "Hostile",
                Faction.Standing.NEUTRAL => "Neutral",
                _ => "Friendly"
            };
            return $"{-Faction.ClampAggro(aggro)}/100 ({standing})";
        }

        /// <summary>
        /// When a no-realm stable master refuses a player over reputation,
        /// explains why and sends them to that faction's emissary.
        /// </summary>
        public static bool TryGetStableRefusal(GameStableMaster master, GamePlayer player, out string message)
        {
            message = null;
            Faction faction = master?.Faction;
            if (faction == null || player == null || GameServer.ServerRules.IsSameRealm(master, player, true) ||
                faction.GetStandingToFaction(player) < Faction.Standing.HOSTILE)
                return false;

            string standing = DescribeStanding(faction.GetAggroLevel(player));
            FactionEmissaryDefinition definition = GetDefinitionForFaction(faction.Id);
            if (definition == null || definition.Realm != player.Realm)
            {
                message = $"{master.Name} will not deal with you. The {faction.Name} do not trust you ({standing}).";
                return true;
            }

            string beside = string.Equals(master.Name, definition.HostStableMaster, StringComparison.OrdinalIgnoreCase)
                ? $"right here beside {definition.HostStableMaster}"
                : $"beside {definition.HostStableMaster} in {definition.TownName}";
            string refusal = definition.Realm switch
            {
                eRealm.Hibernia => $"{master.Name} rattles its mandibles and turns away.",
                eRealm.Albion => $"{master.Name} hisses softly and will not deal with you.",
                _ => $"{master.Name} crosses their arms and will not saddle a gryphon for you."
            };
            message = $"{refusal} The {faction.Name} do not trust you yet: reputation {standing}. " +
                $"The {definition.MountName} need -{RequiredAggroForStables} or better. " +
                $"Speak with {definition.Name}, the {definition.FactionName} Emissary, {beside}.";
            return true;
        }

        public static string Story(FactionEmissaryDefinition definition) => definition.Realm switch
        {
            eRealm.Hibernia => "The Krrzck Hive guards the wyvern roads to Krrzck and Mantid Town. Strangers ride only when the Hive trusts them, and the Hive trusts only those who kill its enemies.",
            eRealm.Albion => "The lammia of the Cryptos Mythicos keep the dragonfly roads between Clifton, the Lammia Camp and Caer Diogel. They carry only those who have bled their enemies.",
            _ => "The Remnants, last of the free iarn dwarf clans, keep the gryphon roads to their camp in Faraheim. They fly only friends, and friends are made by killing the clans' foes."
        };

        public static string PaidLine(FactionEmissaryDefinition definition) => definition.Realm switch
        {
            eRealm.Hibernia => "Click... click. The Hive remembers softskin's hunt. Come again. There is always more rot to cut.",
            eRealm.Albion => "Well done, warm-blood. The Cryptos Mythicos remember. Come back when you hunger for more.",
            _ => "Good work. The Remnants don't forget an honest axe. Come back when you're ready for more."
        };

        /// <summary>
        /// Only no-realm stable masters of the faction check reputation. Realm
        /// stable masters (Korlis) and faction-less ones (Vilmalin) serve every
        /// player of their realm, so each emissary says exactly who checks.
        /// </summary>
        public static string GatedStablesLine(FactionEmissaryDefinition definition) => definition.Realm switch
        {
            eRealm.Hibernia => "Zrrazk here, Dalniver at Krrzck and Calvine in Mantid Town carry only softskins the Hive trusts.",
            eRealm.Albion => "Vilmalin carriesss anyone to Clifton, but Nimea there and Callisa at the Lammia Camp ssserve only those we trust.",
            _ => "Korlis flies any Midgardian to our camp, but Minerva there flies only friends of the clans, and the camp's dwarves strike at strangers."
        };

        public static string StandingLine(int aggro) =>
            $"Your reputation: {DescribeStanding(aggro)}. " +
            (Faction.StandingForAggro(aggro) < Faction.Standing.HOSTILE
                ? "That is enough for the trusted stable masters."
                : $"The trusted stable masters need -{RequiredAggroForStables} or better.");

        public static string Greeting(FactionEmissaryDefinition definition, int aggro) => definition.Realm switch
        {
            eRealm.Hibernia => $"Click-click. Softskin stands before Kzzirrak, voice of the Krrzck Hive. {GatedStablesLine(definition)} Softskin wants trust? Then softskin [hunts for the Hive], or asks how [trust grows]. {StandingLine(aggro)}",
            eRealm.Albion => $"Ssso... a warm-blood comes to Ysslith. {GatedStablesLine(definition)} Earn our favor: [hunt for the Cryptos], or ask how [favor is earned]. {StandingLine(aggro)}",
            _ => $"Hmph. Surface-folk. {GatedStablesLine(definition)} Want our trust? [Hunt for the Remnants], or ask how [trust is forged]. {StandingLine(aggro)}"
        };

        public static string HowItWorks(FactionEmissaryDefinition definition) => definition.Realm switch
        {
            eRealm.Hibernia => "The Hive remembers deeds, not words. Ten enemies of the Hive fall, softskin returns, Hive trust grows ten. Rot-growth botonids and Olcasgean's twisted ones, the Hive hates them all. At -50 or better, the Hive's wyverns carry softskin.",
            eRealm.Albion => "We remember what is done for us, little warm-blood. Ssslay ten of our enemies, the Kulaclan orcs, the thrawn ogres, Morgana's restless dead, and return. Each hunt earns ten favor. At -50 or better, Nimea and Callisa will ssserve you.",
            _ => "Trust is forged, not bought. Bring down ten of our foes, the morvalt raiders, the Servants of Iarnvidiur, the Skogsra shade seekers, and come back. Ten trust each time. At -50 or better, Minerva will fly you and the camp will let you be."
        };

        private static string Offer(FactionEmissaryDefinition definition) =>
            $"Hunt ten enemies of the {definition.FactionName} near your strength? Return to {definition.Name} for +{FactionReputationQuest.FullReputationGain} {definition.FactionName} reputation. The red dot marks the hunting ground on that zone's map.";

        private static string Progress(FactionEmissaryDefinition definition, FactionReputationQuest quest)
        {
            string hunt = $"{quest.TargetName} in {quest.ZoneName}: {quest.Progress}/{quest.RequiredKills}";
            string half = quest.WasRerolled ? $" This hunt grants +{FactionReputationQuest.RerolledReputationGain}." : string.Empty;
            return definition.Realm switch
            {
                eRealm.Hibernia => $"Zzt. Softskin hunts {hunt}. The Hive waits. Ask to [show location], or [reroll] for other prey; the Hive gives only half trust for chosen prey.{half}",
                eRealm.Albion => $"Ssstill hunting? {hunt}. Ask to [show location], or [reroll] if the prey displeases you; chosen prey earns only half favor.{half}",
                _ => $"Still at it? {hunt}. Ask me to [show location], or [reroll] for other quarry; picky hunters get half the trust.{half}"
            };
        }

        private static string Ready(FactionEmissaryDefinition definition, FactionReputationQuest quest) => definition.Realm switch
        {
            eRealm.Hibernia => $"Kzzirrak tastes the air. Ten {quest.TargetName} are dead. [Claim reward] and the Hive remembers.",
            eRealm.Albion => $"Mmm, I can ssmell their blood on you. Ten {quest.TargetName}. [Claim reward] and I will whisper your name to the others.",
            _ => $"Ha! Ten {quest.TargetName} down. [Claim reward] and I'll carve your name on the clan stone."
        };

        private static void AddEmissary(FactionEmissaryDefinition definition)
        {
            Region region = WorldMgr.GetRegion(definition.RegionId);
            Faction faction = FactionMgr.GetFactionByID(definition.FactionId);
            if (region == null || region.IsDisabled || faction == null)
                return;

            FactionEmissaryNPC emissary = new()
            {
                Name = definition.Name,
                GuildName = $"{faction.Name} Emissary",
                Realm = definition.Realm,
                Gender = definition.Gender,
                Model = definition.Model,
                Size = definition.Size,
                Level = 50,
                CurrentRegionID = definition.RegionId,
                CurrentRegion = region,
                X = definition.X,
                Y = definition.Y,
                Z = definition.Z,
                LoadedFromScript = true,
                MaxSpeedBase = 0,
            };

            // Face the stable master the emissary serves, unless a fixed view is set.
            GameStableMaster host = region.Objects.OfType<GameStableMaster>().FirstOrDefault(master =>
                string.Equals(master.Name, definition.HostStableMaster, StringComparison.OrdinalIgnoreCase));
            emissary.Heading = definition.Heading ?? (host != null ? emissary.GetHeading(host) : (ushort)0);
            emissary.Flags |= GameNPC.eFlags.PEACE;
            if (!emissary.AddToWorld())
                return;

            Emissaries[definition.Realm] = emissary;
            emissary.AddQuestToGive(typeof(FactionReputationQuest));
            GameEventMgr.AddHandler(emissary, GameObjectEvent.Interact, TalkToEmissary);
            GameEventMgr.AddHandler(emissary, GameLivingEvent.WhisperReceive, TalkToEmissary);
        }

        private static void TalkToEmissary(DOLEvent e, object sender, EventArgs args)
        {
            if (sender is not FactionEmissaryNPC emissary || args is not SourceEventArgs source ||
                source.Source is not GamePlayer player || player.Realm != emissary.Realm ||
                !emissary.IsWithinRadius(player, 600))
                return;

            FactionEmissaryDefinition definition = GetDefinition(player.Realm);
            Faction faction = definition == null ? null : FactionMgr.GetFactionByID(definition.FactionId);
            if (faction == null)
                return;

            FactionReputationQuest active = player.IsDoingQuest(typeof(FactionReputationQuest)) as FactionReputationQuest;
            if (e == GameObjectEvent.Interact)
            {
                if (active == null)
                    emissary.SayTo(player, Greeting(definition, faction.GetAggroLevel(player)));
                else if (active.IsReady)
                    emissary.SayTo(player, Ready(definition, active));
                else
                    emissary.SayTo(player, Progress(definition, active));
                return;
            }

            if (e != GameLivingEvent.WhisperReceive || args is not WhisperReceiveEventArgs whisper)
                return;

            switch (whisper.Text.Trim().ToLowerInvariant())
            {
                case "hunts for the hive":
                case "hunt for the cryptos":
                case "hunt for the remnants":
                case "hunt":
                    if (active == null && emissary.CanGiveQuest(typeof(FactionReputationQuest), player) > 0)
                        player.Out.SendQuestSubscribeCommand(emissary,
                            QuestMgr.GetIDForQuestType(typeof(FactionReputationQuest)), Offer(definition));
                    break;

                case "trust grows":
                case "favor is earned":
                case "trust is forged":
                    emissary.SayTo(player, HowItWorks(definition));
                    break;

                case "show location":
                    if (active != null)
                    {
                        active.ShowMarker();
                        emissary.SayTo(player, $"Seek {active.TargetName} in {active.ZoneName}. Enter that zone and open its map to see the red dot. Any {active.TargetName} in the {definition.FactionName} lands counts.");
                    }
                    break;

                case "reroll":
                    if (active != null)
                        player.Out.SendCustomDialog(
                            $"Choose different prey? This hunt will then grant only +{FactionReputationQuest.RerolledReputationGain} reputation, even after further rerolls.",
                            ConfirmReroll);
                    break;

                case "claim reward":
                case "reward":
                    if (active?.IsReady == true)
                    {
                        active.QuestGiver = emissary;
                        active.Claim();
                    }
                    break;
            }
        }

        private static void ConfirmReroll(GamePlayer player, byte response)
        {
            if (response != 0x01 || player?.IsDoingQuest(typeof(FactionReputationQuest)) is not FactionReputationQuest quest ||
                !NearOwnEmissary(player, out _))
                return;
            quest.Reroll();
        }

        private static bool NearOwnEmissary(GamePlayer player, out FactionEmissaryNPC emissary)
        {
            emissary = player == null ? null : GetEmissary(player.Realm);
            return emissary != null && emissary.CurrentRegionID == player.CurrentRegionID &&
                   emissary.IsWithinRadius(player, 600);
        }

        private static void AcceptQuest(DOLEvent e, object sender, EventArgs args)
        {
            if (e != GamePlayerEvent.AcceptQuest || args is not QuestEventArgs accepted ||
                QuestMgr.GetQuestTypeForID(accepted.QuestID) != typeof(FactionReputationQuest) ||
                !NearOwnEmissary(accepted.Player, out FactionEmissaryNPC emissary) ||
                accepted.Player.IsDoingQuest(typeof(FactionReputationQuest)) != null ||
                emissary.CanGiveQuest(typeof(FactionReputationQuest), accepted.Player) <= 0)
                return;

            emissary.GiveQuest(typeof(FactionReputationQuest), accepted.Player, 1);
        }

        private static void PlayerEntered(DOLEvent e, object sender, EventArgs args)
        {
            if (sender is GamePlayer player && player.IsDoingQuest(typeof(FactionReputationQuest)) is FactionReputationQuest quest)
            {
                quest.QuestGiver = GetEmissary(player.Realm);
                quest.ShowMarker();
                UpdateIndicator(player);
            }
        }

        private static void PlayerQuit(DOLEvent e, object sender, EventArgs args)
        {
            if (sender is GamePlayer player)
                ReputationMapMarkers.Clear(player);
        }
    }
}

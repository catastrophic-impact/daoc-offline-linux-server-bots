using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS.PacketHandler;

namespace DOL.GS;

/// <summary>
/// Coordinates scarce, short bot conversations. Faction text stays broad;
/// local text is allowed to mention the speaker's live task and surroundings.
/// </summary>
public static class AutonomousBotChatCoordinator
{
    private static readonly object Sync = new();
    private static readonly Dictionary<eRealm, long> LastFactionAmbient = new();
    private static readonly Dictionary<ushort, long> LastLocalAmbient = new();
    private static readonly Dictionary<eRealm, long> LastFactionPlayerResponse = new();
    private static readonly Dictionary<ushort, long> LastLocalPlayerResponse = new();

    private static readonly string[] FactionOpeners =
    [
        "How fares everyone across the realm?", "Any groups forming today?", "The roads seem lively today.",
        "Anyone heading out for a longer hunt?", "I may look for company later.", "Good hunting, everyone.",
        "Has the frontier been active?", "Anyone trading useful equipment today?", "Stay sharp out there.",
        "Which hunting grounds are treating everyone well?", "Any useful class advice to share today?",
        "Has anyone found a dependable camp for their level?", "The realm feels lively today.",
        "If anyone needs a group, say where you are headed.", "Remember to visit your trainer after leveling.",
        "Are any dungeon groups planning a careful run?", "Safe roads and good hunting to everyone.",
        "What is everyone working on today?",
    ];

    private static readonly string[] FactionReplies =
    [
        "Things seem steady from where I am.", "I have seen a few adventurers moving around.",
        "I may be available once I finish up here.", "That sounds worth keeping in mind.",
        "Safe travels if you head that way.", "I have heard similar talk around the realm.",
        "There should be others interested before long.", "I will keep an eye out while I finish this task.",
        "Sharing the location may help someone nearby.", "A balanced group should handle that well.",
        "Checking your trainer and equipment is always worthwhile.", "Good luck with the next pull.",
    ];

    public static bool TryStartAmbient(GameBot starter, AutonomousBotChat.Context context)
    {
        if (!Eligible(starter))
            return false;

        bool factionAudience = HasFactionAudience(starter.Realm);
        bool localAudience = HasLocalAudience(starter);
        if (!factionAudience && !localAudience)
            return false;

        bool faction = factionAudience && (!localAudience || Random.Shared.NextDouble() < 0.24);
        lock (Sync)
        {
            if (faction)
            {
                if (LastFactionAmbient.TryGetValue(starter.Realm, out long last) && GameLoop.GameLoopTime - last < 180_000)
                    return false;
                LastFactionAmbient[starter.Realm] = GameLoop.GameLoopTime;
            }
            else
            {
                if (LastLocalAmbient.TryGetValue(starter.CurrentRegionID, out long last) && GameLoop.GameLoopTime - last < 90_000)
                    return false;
                LastLocalAmbient[starter.CurrentRegionID] = GameLoop.GameLoopTime;
            }
        }

        string opening = BuildAmbientOpening(starter, context, faction);
        if (faction)
            BroadcastFaction(starter.Name, starter.Realm, opening);
        else if (!starter.Say(opening))
            return false;

        ScheduleBotReplies(starter, opening, faction, false);
        return true;
    }

    public static void OnPlayerFactionChat(GamePlayer player, string message)
    {
        if (player == null || string.IsNullOrWhiteSpace(message))
            return;
        BroadcastFaction(player.Name, player.Realm, message);
        lock (Sync)
        {
            if (LastFactionPlayerResponse.TryGetValue(player.Realm, out long last) && GameLoop.GameLoopTime - last < 12_000)
                return;
            LastFactionPlayerResponse[player.Realm] = GameLoop.GameLoopTime;
        }
        SchedulePlayerReplies(player, message, true);
    }

    public static void OnPlayerLocalChat(GamePlayer player, string message)
    {
        if (player == null || string.IsNullOrWhiteSpace(message) || player.CurrentRegion == null)
            return;
        if (AutonomousChatIntentModel.Predict(message) == eAutonomousChatIntent.IgnoreOutOfWorld)
            return;
        lock (Sync)
        {
            if (LastLocalPlayerResponse.TryGetValue(player.CurrentRegionID, out long last) && GameLoop.GameLoopTime - last < 10_000)
                return;
            LastLocalPlayerResponse[player.CurrentRegionID] = GameLoop.GameLoopTime;
        }
        SchedulePlayerReplies(player, message, false);
    }

    public static bool TryAdvertiseExchangeItem(GameBot seller, string exactItemName)
    {
        if (!Eligible(seller) || string.IsNullOrWhiteSpace(exactItemName) || !HasFactionAudience(seller.Realm))
            return false;
        lock (Sync)
        {
            if (LastFactionAmbient.TryGetValue(seller.Realm, out long last) && GameLoop.GameLoopTime - last < 180_000)
                return false;
            LastFactionAmbient[seller.Realm] = GameLoop.GameLoopTime;
        }

        string item = exactItemName.Trim();
        string opening = Random.Shared.Next(2) == 0
            ? $"Does anybody want to make me an offer on {item}?"
            : $"How much do u think {item} is worth?";
        BroadcastFaction(seller.Name, seller.Realm, opening);
        ScheduleBotReplies(seller, opening, true, false);
        return true;
    }

    public static string GenerateFactionReply(string incoming, Random random = null)
    {
        random ??= Random.Shared;
        if (incoming.Contains("offer on", StringComparison.OrdinalIgnoreCase) ||
            incoming.Contains("how much", StringComparison.OrdinalIgnoreCase) && incoming.Contains("worth", StringComparison.OrdinalIgnoreCase))
        {
            return random.Next(5) switch
            {
                0 => "Compare its level and bonuses before setting the price.",
                1 => "The Realm Exchange should show what similar gear is worth.",
                2 => "That may be useful to someone leveling nearby.",
                3 => "A merchant price can provide a reasonable minimum value.",
                _ => "I might offer a few silver if it suits my class.",
            };
        }
        if (incoming.Contains("mythic", StringComparison.OrdinalIgnoreCase) ||
            incoming.Contains(" broken", StringComparison.OrdinalIgnoreCase) ||
            incoming.Contains(" op", StringComparison.OrdinalIgnoreCase) ||
            incoming.Contains("best class", StringComparison.OrdinalIgnoreCase))
        {
            return random.Next(8) switch
            {
                0 => "Every class has strengths that show in the right group.",
                1 => "Which specialization are you considering?",
                2 => "That ability may work better with the right positioning.",
                3 => "It is worth asking someone who plays that class often.",
                4 => "Equipment and specialization can change how the class feels.",
                5 => "I would compare how it performs solo and in a group.",
                6 => "A careful pull usually tells you more than the delve text.",
                _ => "Try the class and see which playstyle suits you.",
            };
        }
        if (incoming.Contains("lag", StringComparison.OrdinalIgnoreCase) ||
            incoming.Contains(" dc", StringComparison.OrdinalIgnoreCase) ||
            incoming.Contains("disconnect", StringComparison.OrdinalIgnoreCase))
        {
            return random.Next(5) switch
            {
                0 => "Movement seems steady where I am.",
                1 => "I noticed a short delay too; it seems better now.",
                2 => "Give the area a moment to finish loading.",
                3 => "Hopefully it clears before your next pull.",
                _ => "Stay with your group until movement catches up.",
            };
        }
        return AutonomousChatIntentModel.Predict(incoming) switch
        {
            eAutonomousChatIntent.Greeting => random.Next(2) == 0 ? "Greetings. Good hunting out there." : "Hello there. The realm is awake today.",
            eAutonomousChatIntent.Banter => random.Next(2) == 0 ? "Good luck on your next pull." : "Save some of that energy for the frontier.",
            eAutonomousChatIntent.Abuse => random.Next(3) switch { 0 => "Let us keep faction chat helpful.", 1 => "I would rather focus on the realm.", _ => "Good hunting when you head back out." },
            eAutonomousChatIntent.Grouping or eAutonomousChatIntent.Help => random.Next(2) == 0 ? "Share your level and location so nearby adventurers can help." : "I may be free once I finish my current task.",
            eAutonomousChatIntent.Travel => random.Next(2) == 0 ? "A stable master may shorten the trip." : "Check the road signs and travel with your group.",
            eAutonomousChatIntent.RvR => random.Next(2) == 0 ? "I have heard the frontier has some movement." : "A few defenders may head out before long.",
            eAutonomousChatIntent.Trading => random.Next(2) == 0 ? "The Realm Exchange may have something useful." : "I have seen a little trade around the capital.",
            eAutonomousChatIntent.Dungeon => "A careful group should clear one pull at a time on the way inside.",
            eAutonomousChatIntent.Grinding => "Share the camp and level if you are looking for company.",
            eAutonomousChatIntent.Farewell => "Safe travels. Until next time.",
            eAutonomousChatIntent.IgnoreOutOfWorld => "Let us keep faction chat focused on the realm.",
            _ => FactionReplies[random.Next(FactionReplies.Length)],
        };
    }

    public static string GenerateLocalReply(GameBot bot, string incoming, Random random = null)
    {
        random ??= Random.Shared;
        string activity = Clean(bot?.PersistentRecord?.Activity, "taking a short rest");
        string target = Clean(bot?.PersistentRecord?.TargetName, "the creatures nearby");
        string destination = Clean(bot?.PersistentRecord?.TravelDestination, bot?.CurrentZone?.Description ?? "this area");

        return AutonomousChatIntentModel.Predict(incoming) switch
        {
            eAutonomousChatIntent.Greeting => random.Next(2) == 0 ? $"Hello. I am keeping an eye on {target}." : $"Greetings. I am {LowerFirst(activity)}.",
            eAutonomousChatIntent.Banter => random.Next(2) == 0 ? "Good luck on the next pull." : "Keep your weapon ready and stay with the group.",
            eAutonomousChatIntent.Abuse => random.Next(3) switch { 0 => "Let us keep this helpful.", 1 => "I am focusing on my current task.", _ => "Safe travels when you move on." },
            eAutonomousChatIntent.Grouping or eAutonomousChatIntent.Help => bot?.Group == null ? $"I am working around {target} for now, but I could group." : "I am already moving with a group right now.",
            eAutonomousChatIntent.Travel => $"I am headed toward {destination}.",
            eAutonomousChatIntent.Dungeon => $"We should clear one pull at a time if we head deeper. I am {LowerFirst(activity)}.",
            eAutonomousChatIntent.RvR => "If I head to the frontier, I will stay with the realm group.",
            eAutonomousChatIntent.Trading => "I will check my real inventory and the Realm Exchange when I return to town.",
            eAutonomousChatIntent.Grinding => $"I am focused on {target} at the moment.",
            eAutonomousChatIntent.Farewell => "Safe travels. I will keep working here.",
            eAutonomousChatIntent.IgnoreOutOfWorld => "Let us keep the conversation focused on the realm.",
            _ => random.Next(3) switch
            {
                0 => $"I am focused on {target} at the moment.",
                1 => $"Right now I am {LowerFirst(activity)}.",
                _ => $"I will be around {destination} for a while.",
            },
        };
    }

    public static int RollResponseCount(int eligibleCount, Random random = null)
    {
        if (eligibleCount <= 0)
            return 0;
        random ??= Random.Shared;
        return Math.Min(eligibleCount, random.NextDouble() < 0.32 ? 2 : 1);
    }

    public static bool ContainsMention(string message, string name)
    {
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(name))
            return false;
        int start = 0;
        while ((start = message.IndexOf(name, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = start + name.Length;
            bool left = start == 0 || !char.IsLetterOrDigit(message[start - 1]);
            bool right = end == message.Length || !char.IsLetterOrDigit(message[end]);
            if (left && right)
                return true;
            start = end;
        }
        return false;
    }

    public static void BroadcastFaction(string speakerName, eRealm realm, string message)
    {
        string safe = (message ?? string.Empty).Trim();
        if (safe.Length == 0)
            return;
        if (safe.Length > 240)
            safe = safe[..240];
        string formatted = $"({speakerName}) <Faction> {safe}";
        foreach (GamePlayer player in ClientService.Instance.GetPlayersOfRealm(realm))
            player.Out.SendMessage(formatted, eChatType.CT_Broadcast, eChatLoc.CL_ChatWindow);
    }

    private static void SchedulePlayerReplies(GamePlayer player, string message, bool faction)
    {
        bool hasKnowledge = AutonomousChatKnowledge.TryAnswer(player, message, out string knowledge);
        eAutonomousChatIntent intent = AutonomousChatIntentModel.Predict(message);
        GameBot[] eligible = AutonomousBotRegistry.Snapshot()
            .Where(Eligible)
            .Where(bot => bot.Realm == player.Realm)
            .Where(bot => faction || bot.CurrentRegion == player.CurrentRegion && bot.IsWithinRadius(player, WorldMgr.SAY_DISTANCE))
            .OrderByDescending(bot => Relevance(bot, message))
            .ThenBy(_ => Random.Shared.Next())
            .ToArray();
        GameBot[] mentioned = eligible.Where(bot => ContainsMention(message, bot.Name)).ToArray();
        bool directed = mentioned.Length > 0;
        if (directed)
            eligible = mentioned;
        int count = directed || hasKnowledge || intent is eAutonomousChatIntent.IgnoreOutOfWorld or eAutonomousChatIntent.Abuse
            ? Math.Min(1, eligible.Length)
            : RollResponseCount(eligible.Length);
        HashSet<string> scheduledResponses = new(StringComparer.OrdinalIgnoreCase);
        int scheduledIndex = 0;
        for (int index = 0; index < count; index++)
        {
            GameBot responder = eligible[index];
            string response = hasKnowledge
                ? knowledge
                : directed
                    ? GenerateDirectedReply(message)
                    : faction ? GenerateFactionReply(message) : GenerateLocalReply(responder, message);
            if (!TryAddDistinctResponse(scheduledResponses, response, out response))
                continue;
            Schedule(responder, 1_300 + scheduledIndex++ * 1_800 + Random.Shared.Next(1_200), () =>
            {
                if (faction && HasFactionAudience(responder.Realm))
                    BroadcastFaction(responder.Name, responder.Realm, response);
                else if (!faction && HasLocalAudience(responder))
                    responder.Say(response);
            });
        }
    }

    private static void ScheduleBotReplies(GameBot starter, string opening, bool faction, bool fromPlayer)
    {
        bool hasKnowledge = AutonomousChatKnowledge.TryAnswer(starter.Realm, opening, out string knowledge);
        GameBot[] eligible = AutonomousBotRegistry.Snapshot()
            .Where(bot => bot != starter && Eligible(bot) && bot.Realm == starter.Realm)
            .Where(bot => faction || bot.CurrentRegion == starter.CurrentRegion && bot.IsWithinRadius(starter, WorldMgr.SAY_DISTANCE))
            .OrderByDescending(bot => Relevance(bot, opening))
            .ThenBy(_ => Random.Shared.Next())
            .ToArray();
        int count = hasKnowledge ? Math.Min(1, eligible.Length) : RollResponseCount(eligible.Length);
        HashSet<string> scheduledResponses = new(StringComparer.OrdinalIgnoreCase);
        int scheduledCount = 0;
        for (int index = 0; index < count; index++)
        {
            GameBot responder = eligible[index];
            string response = hasKnowledge ? knowledge : faction ? GenerateFactionReply(opening) : GenerateLocalReply(responder, opening);
            if (!TryAddDistinctResponse(scheduledResponses, response, out response))
                continue;
            Schedule(responder, 1_700 + scheduledCount++ * 2_100 + Random.Shared.Next(1_300), () =>
            {
                if (faction && HasFactionAudience(responder.Realm))
                    BroadcastFaction(responder.Name, responder.Realm, response);
                else if (!faction && HasLocalAudience(responder))
                    responder.Say(response);
            });
        }

        // A short optional closing line makes this read like a conversation,
        // while the channel cooldown prevents it from becoming a chat loop.
        if (!fromPlayer && !hasKnowledge && scheduledCount > 0 && Random.Shared.NextDouble() < 0.28)
        {
            string closing = faction ? "Fair enough. Safe travels." : "Understood. I will keep at it.";
            Schedule(starter, 5_500 + scheduledCount * 1_500, () =>
            {
                if (faction && HasFactionAudience(starter.Realm))
                    BroadcastFaction(starter.Name, starter.Realm, closing);
                else if (!faction && HasLocalAudience(starter))
                    starter.Say(closing);
            });
        }
    }

    private static void Schedule(GameBot speaker, int delay, Action action)
    {
        _ = new ECSGameTimer(speaker, timer =>
        {
            if (Eligible(speaker) && !speaker.InCombat && !speaker.IsCasting)
                action();
            return 0;
        }, delay);
    }

    public static bool TryAddDistinctResponse(HashSet<string> scheduled, string response, out string normalized)
    {
        normalized = response?.Trim() ?? string.Empty;
        return scheduled != null && normalized.Length > 0 && scheduled.Add(normalized);
    }

    private static bool Eligible(GameBot bot) => bot?.IsAutonomousWorldBot == true && !bot.IsTemporaryGroupHelper &&
        bot.IsAlive && bot.ObjectState == GameObject.eObjectState.Active && !bot.InCombat && !bot.IsCasting &&
        !bot.IsOnStableMasterRoute && !bot.IsOnHorse;

    private static bool HasFactionAudience(eRealm realm) => ClientService.Instance.GetPlayersOfRealm(realm).Count > 0;

    private static bool HasLocalAudience(GameBot bot) => bot?.CurrentRegion != null &&
        ClientService.Instance.GetPlayersOfRegion(bot.CurrentRegion)
            .Any(player => player.ObjectState == GameObject.eObjectState.Active && bot.IsWithinRadius(player, WorldMgr.SAY_DISTANCE));

    private static string BuildAmbientOpening(GameBot starter, AutonomousBotChat.Context context, bool faction)
    {
        if (Random.Shared.NextDouble() < 0.18)
        {
            string activity = Clean(starter.PersistentRecord?.Activity, string.Empty);
            string destination = Clean(starter.PersistentRecord?.TravelDestination, string.Empty);
            if (!string.IsNullOrWhiteSpace(destination) &&
                (activity.StartsWith("Walking to", StringComparison.OrdinalIgnoreCase) ||
                 activity.StartsWith("Approaching", StringComparison.OrdinalIgnoreCase)))
                return Random.Shared.Next(2) == 0
                    ? $"Anyone know where {destination} is?"
                    : $"Does anyone know where {destination} is at?";

            string target = Clean(starter.PersistentRecord?.TargetName, string.Empty);
            if (!string.IsNullOrWhiteSpace(target))
                return Random.Shared.Next(3) switch
                {
                    0 => $"Anyone know where {target} is?",
                    1 => $"Does anyone know where {target} is at?",
                    _ => $"Has anybody ever found {target}?",
                };

            if (!string.IsNullOrWhiteSpace(destination))
                return Random.Shared.Next(3) switch
                {
                    0 => $"Anyone know where {destination} is?",
                    1 => $"How do I get to {destination}?",
                    _ => $"Which stable master takes me to {destination}?",
                };
        }

        if (faction)
        {
            string situation = $"{starter.PersistentRecord?.Activity} {starter.PersistentRecord?.CurrentGoal} {starter.CurrentZone?.Description}";
            bool pvp = situation.Contains("rvr", StringComparison.OrdinalIgnoreCase) ||
                       situation.Contains("pvp", StringComparison.OrdinalIgnoreCase) ||
                       situation.Contains("frontier", StringComparison.OrdinalIgnoreCase) || starter.CurrentZone?.IsRvR == true;
            return BuildFactionTaskOpening(starter, context, pvp);
        }
        return AutonomousBotChat.Generate(context);
    }

    private static string BuildFactionTaskOpening(GameBot starter, AutonomousBotChat.Context context, bool pvp)
    {
        string className = Clean(context.ClassName, starter.ClassName);
        string zone = Clean(context.ZoneName, starter.CurrentZone?.Description ?? "my current area");
        string target = Clean(context.MonsterName, string.Empty);
        string destination = Clean(starter.PersistentRecord?.TravelDestination, string.Empty);
        int choice = Random.Shared.Next(10);

        if (pvp)
        {
            return (choice % 4) switch
            {
                0 => $"I am watching the frontier around {zone}. Where are defenders gathering?",
                1 => $"Any {className}s have advice for supporting a frontier group?",
                2 => "If anyone sees enemy movement, share the location so the realm can respond.",
                _ => "Good luck to everyone heading into the frontier. Stay with your group.",
            };
        }

        if (!string.IsNullOrWhiteSpace(target) && choice < 4)
        {
            return choice switch
            {
                0 => $"I am hunting {target} around {zone}. Has anyone found a steady camp nearby?",
                1 => $"These {target} are keeping me busy in {zone}. Anyone else working nearby?",
                2 => starter.Group?.MemberCount > 1
                    ? $"Our group is taking careful pulls of {target} in {zone}."
                    : $"I am taking careful pulls of {target} in {zone}.",
                _ => $"Has anyone learned a reliable way to handle {target} around {zone}?",
            };
        }

        if (!string.IsNullOrWhiteSpace(destination) && choice < 7)
        {
            return (choice % 3) switch
            {
                0 => $"I am heading toward {destination}. Any advice on the safest route?",
                1 => $"Does anyone know which stable master is best for reaching {destination}?",
                _ => $"Safe travels to anyone heading toward {destination}.",
            };
        }

        return choice switch
        {
            7 => $"Any other {className}s have advice for adventurers around level {starter.Level}?",
            8 => $"How are the hunting grounds around {zone} treating everyone?",
            9 when starter.Group?.MemberCount > 1 => $"Our group is working around {zone}. Are any other parties nearby?",
            _ => FactionOpeners[Random.Shared.Next(FactionOpeners.Length)],
        };
    }

    private static int Relevance(GameBot bot, string incoming)
    {
        string haystack = $"{bot.PersistentRecord?.Activity} {bot.PersistentRecord?.CurrentGoal} {bot.PersistentRecord?.TargetName} {bot.PersistentRecord?.TravelDestination}";
        return (incoming ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('?', '!', '.', ',', '\'', '"'))
            .Where(word => word.Length >= 4)
            .Count(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static string GenerateDirectedReply(string incoming)
    {
        if (incoming.Contains("stop", StringComparison.OrdinalIgnoreCase) ||
            incoming.Contains("quiet", StringComparison.OrdinalIgnoreCase) ||
            incoming.Contains("shut", StringComparison.OrdinalIgnoreCase))
        {
            return Random.Shared.Next(4) switch
            {
                0 => "Understood. I will focus on my task.",
                1 => "All right. Safe travels.",
                2 => "No problem. I will keep faction chat clear.",
                _ => "Okay. I will get back to work.",
            };
        }
        return AutonomousChatIntentModel.Predict(incoming) switch
        {
            eAutonomousChatIntent.Greeting => "Greetings. How can I help?",
            eAutonomousChatIntent.Abuse => "Let us keep the conversation helpful.",
            eAutonomousChatIntent.IgnoreOutOfWorld => "Let us keep the conversation focused on the realm.",
            _ => Random.Shared.Next(3) switch { 0 => "What did you need?", 1 => "Perhaps after this task.", _ => "Yes?" },
        };
    }

    private static string Clean(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().TrimEnd('.', '!', '?');
    private static string LowerFirst(string value) => string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}

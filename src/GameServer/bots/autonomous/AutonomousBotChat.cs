using System;
using System.Collections.Generic;

namespace DOL.GS;

/// <summary>
/// Rare contextual chat assembled from interchangeable clauses. The cross-product
/// yields tens of thousands of lines without an LLM or a huge resident string table.
/// </summary>
public static class AutonomousBotChat
{
    public sealed record Context(
        string BotName,
        string ClassName,
        string ZoneName,
        string MonsterName,
        string NpcName,
        string ItemName,
        string CraftName,
        int Level,
        int GroupSize,
        eRealm Realm);

    private static readonly string[] Openers =
    [
        "Anyone", "Anybody", "Is anyone", "Would anyone", "Any adventurers", "Any {realm} folk",
        "Quick question, anyone", "If anyone is nearby, would they", "Does anyone", "Could someone",
        "Looking around—anyone", "Before I head out, does anyone", "One moment—can anybody", "Greetings, can anyone",
    ];

    private static readonly string[] GrindQuestions =
    [
        "grinding {monster}?", "hunting {monster} near {zone}?", "want to hunt {monster}?",
        "know a good camp for {monster}?", "still getting experience from {monster}?",
        "forming a group for {monster}?", "seen many {monster} around {zone}?", "up for a few pulls of {monster}?",
    ];

    private static readonly string[] FindQuestions =
    [
        "help me find {monster}?", "help me find {npc}?", "point me toward {npc}?",
        "tell me where {monster} gather?", "seen {npc} around {zone}?", "show me the road to {npc}?",
        "remember where {npc} stands?", "help track down {monster}?", "know the safest route to {npc}?",
    ];

    private static readonly string[] Statements =
    [
        "These {monster} are keeping me busy.", "{zone} feels crowded tonight.", "I may look for a new camp soon.",
        "My packs are nearly full.", "I should visit {npc} before long.", "That last {monster} put up a fight.",
        "I could use an upgrade from the Realm Exchange.", "Saving this {item}; it may fetch a fair price.",
        "I have enough {craft} materials for another attempt.", "A short rest, then back to {monster}.",
        "I wonder whether a dungeon group is forming.", "The frontier sounds tempting, but not quite yet.",
    ];

    private static readonly string[] GroupLines =
    [
        "Group has room for {count} more near {zone}.", "We could use a healer for {monster}.",
        "A shield would help with these {monster}.", "Anyone want to join us in {zone}?",
        "We are taking careful pulls at {monster}.", "Our group may try harder prey soon.",
    ];

    public static int MinimumCombinatorialVariants => Openers.Length * (GrindQuestions.Length + FindQuestions.Length) + Statements.Length + GroupLines.Length;
    public static int EstimatedVariantsWithTwentyLiveNames => MinimumCombinatorialVariants * 20;

    public static string Generate(Context context, Random random = null)
    {
        random ??= Random.Shared;
        string template;
        int category = random.Next(context.GroupSize > 1 ? 4 : 3);
        if (category == 0)
            template = $"{Openers[random.Next(Openers.Length)]} {GrindQuestions[random.Next(GrindQuestions.Length)]}";
        else if (category == 1)
            template = $"{Openers[random.Next(Openers.Length)]} {FindQuestions[random.Next(FindQuestions.Length)]}";
        else if (category == 2)
            template = Statements[random.Next(Statements.Length)];
        else
            template = GroupLines[random.Next(GroupLines.Length)];

        return Fill(template, context);
    }

    public static bool ShouldSpeak(DateTime utcNow, DateTime lastSpokeUtc, bool inCombat, Random random = null)
    {
        if (inCombat || utcNow - lastSpokeUtc < TimeSpan.FromMinutes(4))
            return false;
        random ??= Random.Shared;
        return random.NextDouble() < 0.012;
    }

    private static string Fill(string text, Context context)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{realm}"] = context.Realm.ToString(),
            ["{zone}"] = Fallback(context.ZoneName, "this area"),
            ["{monster}"] = Fallback(context.MonsterName, "the local creatures"),
            ["{npc}"] = Fallback(context.NpcName, "the nearest trainer"),
            ["{item}"] = Fallback(context.ItemName, "drop"),
            ["{craft}"] = Fallback(context.CraftName, "crafting"),
            ["{count}"] = Math.Max(1, 8 - context.GroupSize).ToString(),
        };

        foreach ((string token, string value) in values)
            text = text.Replace(token, value, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string Fallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}

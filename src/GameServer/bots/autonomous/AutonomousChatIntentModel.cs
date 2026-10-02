using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DOL.GS;

public enum eAutonomousChatIntent
{
    Unknown,
    Greeting,
    Banter,
    Abuse,
    Grouping,
    Grinding,
    Travel,
    Dungeon,
    RvR,
    Trading,
    Help,
    Farewell,
    IgnoreOutOfWorld,
}

/// <summary>
/// Tiny multinomial Naive Bayes classifier trained from embedded DAoC chat
/// examples. It has no network, no generative model and no player-data training.
/// </summary>
public static partial class AutonomousChatIntentModel
{
    private sealed record Example(eAutonomousChatIntent Intent, string Text);
    private sealed record Model(Dictionary<eAutonomousChatIntent, Dictionary<string, int>> Counts,
        Dictionary<eAutonomousChatIntent, int> Totals, Dictionary<eAutonomousChatIntent, int> Documents,
        HashSet<string> Vocabulary, int DocumentCount);

    private static readonly Example[] Training =
    [
        new(eAutonomousChatIntent.Greeting, "hello hi hey greetings hail whats up how goes it good evening"),
        new(eAutonomousChatIntent.Greeting, "morning everyone hello realm hey folks how are you"),
        new(eAutonomousChatIntent.Greeting, "yo there hi friends good to see you"),
        new(eAutonomousChatIntent.Banter, "you guys suck terrible group bad pull weaklings"),
        new(eAutonomousChatIntent.Banter, "nice try you hit like a kobold that was rough"),
        new(eAutonomousChatIntent.Banter, "come on slowpokes is that all you have"),
        new(eAutonomousChatIntent.Abuse, "you are shit dumb idiots morons garbage trash"),
        new(eAutonomousChatIntent.Abuse, "stupid loser shut up worthless terrible player"),
        new(eAutonomousChatIntent.Abuse, "everyone here is awful you are all pathetic"),
        new(eAutonomousChatIntent.Grouping, "looking for group need healer tank damage invite join party"),
        new(eAutonomousChatIntent.Grouping, "anybody grouping want to team up group has room"),
        new(eAutonomousChatIntent.Grouping, "forming party need help with harder mobs"),
        new(eAutonomousChatIntent.Grinding, "where are you grinding hunting mobs camp experience xp leveling"),
        new(eAutonomousChatIntent.Grinding, "good monster camp killing creatures gain levels"),
        new(eAutonomousChatIntent.Grinding, "anyone farming skeletons wolves bandits loot drops"),
        new(eAutonomousChatIntent.Travel, "where are you going how do I get there road route stable master"),
        new(eAutonomousChatIntent.Travel, "which way to town trainer capital horse ticket"),
        new(eAutonomousChatIntent.Travel, "help me find npc monster location travel"),
        new(eAutonomousChatIntent.Dungeon, "dungeon entrance cave tomb deeper inside careful pulls"),
        new(eAutonomousChatIntent.Dungeon, "want to run a dungeon clear the corridor"),
        new(eAutonomousChatIntent.Dungeon, "group for spraggon den cursed tomb vendo caves"),
        new(eAutonomousChatIntent.RvR, "rvr frontier keep raid enemies relic defense attack realm points"),
        new(eAutonomousChatIntent.RvR, "albion midgard hibernia fighting at the keep"),
        new(eAutonomousChatIntent.RvR, "any action in emain hadrians odins gate"),
        new(eAutonomousChatIntent.Trading, "buy sell price gear weapon armor auction exchange materials"),
        new(eAutonomousChatIntent.Trading, "does anyone have equipment for sale coins merchant"),
        new(eAutonomousChatIntent.Trading, "realm exchange listing item crafting material"),
        new(eAutonomousChatIntent.Help, "can anyone help me question confused how does this work"),
        new(eAutonomousChatIntent.Help, "need advice can somebody explain please help"),
        new(eAutonomousChatIntent.Help, "what should I do trainer skills equipment"),
        new(eAutonomousChatIntent.Farewell, "bye goodbye later see you safe travels good night"),
        new(eAutonomousChatIntent.Farewell, "got to go farewell heading off take care"),
        new(eAutonomousChatIntent.IgnoreOutOfWorld, "politics president election congress democrat republican government"),
        new(eAutonomousChatIntent.IgnoreOutOfWorld, "current news real world country politician vote campaign"),
        new(eAutonomousChatIntent.IgnoreOutOfWorld, "stocks crypto bitcoin market football basketball television movie"),
        new(eAutonomousChatIntent.IgnoreOutOfWorld, "my real job school homework car phone internet social media"),
    ];

    private static readonly HashSet<string> HardOutOfWorld = new(StringComparer.OrdinalIgnoreCase)
    {
        "politics", "political", "president", "election", "congress", "democrat", "republican", "government",
        "trump", "biden", "bitcoin", "crypto", "stock", "stocks", "football", "basketball", "netflix",
        "twitter", "facebook", "instagram", "tiktok", "reddit", "headline", "headlines", "news",
    };

    private static readonly HashSet<string> HardAbuse = new(StringComparer.OrdinalIgnoreCase)
    {
        "shit", "dumb", "idiot", "idiots", "moron", "morons", "garbage", "worthless", "pathetic",
    };

    private static readonly IReadOnlyDictionary<eAutonomousChatIntent, HashSet<string>> StrongGameSignals =
        new Dictionary<eAutonomousChatIntent, HashSet<string>>
        {
            [eAutonomousChatIntent.Trading] = new(StringComparer.OrdinalIgnoreCase) { "buy", "sell", "price", "auction", "exchange", "merchant" },
            [eAutonomousChatIntent.Dungeon] = new(StringComparer.OrdinalIgnoreCase) { "dungeon", "tomb", "caves", "cave" },
            [eAutonomousChatIntent.RvR] = new(StringComparer.OrdinalIgnoreCase) { "rvr", "frontier", "relic" },
            [eAutonomousChatIntent.Grouping] = new(StringComparer.OrdinalIgnoreCase) { "group", "party", "invite", "lfg" },
        };

    private static readonly Model Trained = Train();

    public static eAutonomousChatIntent Predict(string text)
    {
        string[] tokens = Tokens(text).ToArray();
        if (tokens.Length == 0)
            return eAutonomousChatIntent.Unknown;
        if (tokens.Any(HardOutOfWorld.Contains))
            return eAutonomousChatIntent.IgnoreOutOfWorld;
        if (tokens.Any(HardAbuse.Contains))
            return eAutonomousChatIntent.Abuse;
        foreach ((eAutonomousChatIntent intent, HashSet<string> signals) in StrongGameSignals)
        {
            if (tokens.Any(signals.Contains))
                return intent;
        }

        eAutonomousChatIntent bestIntent = eAutonomousChatIntent.Unknown;
        double best = double.NegativeInfinity;
        double runnerUp = double.NegativeInfinity;
        foreach (eAutonomousChatIntent intent in Trained.Documents.Keys)
        {
            double score = Math.Log((Trained.Documents[intent] + 1d) /
                                    (Trained.DocumentCount + Trained.Documents.Count));
            Dictionary<string, int> counts = Trained.Counts[intent];
            double denominator = Trained.Totals[intent] + Trained.Vocabulary.Count;
            foreach (string token in tokens)
                score += Math.Log((counts.GetValueOrDefault(token) + 1d) / denominator);
            if (score > best)
            {
                runnerUp = best;
                best = score;
                bestIntent = intent;
            }
            else if (score > runnerUp)
            {
                runnerUp = score;
            }
        }
        return best - runnerUp < 0.18 ? eAutonomousChatIntent.Unknown : bestIntent;
    }

    private static Model Train()
    {
        var counts = new Dictionary<eAutonomousChatIntent, Dictionary<string, int>>();
        var totals = new Dictionary<eAutonomousChatIntent, int>();
        var documents = new Dictionary<eAutonomousChatIntent, int>();
        var vocabulary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Example example in Training)
        {
            Dictionary<string, int> bag = counts.GetValueOrDefault(example.Intent);
            if (bag == null)
                counts[example.Intent] = bag = new(StringComparer.OrdinalIgnoreCase);
            documents[example.Intent] = documents.GetValueOrDefault(example.Intent) + 1;
            foreach (string token in Tokens(example.Text))
            {
                bag[token] = bag.GetValueOrDefault(token) + 1;
                totals[example.Intent] = totals.GetValueOrDefault(example.Intent) + 1;
                vocabulary.Add(token);
            }
        }
        return new(counts, totals, documents, vocabulary, Training.Length);
    }

    private static IEnumerable<string> Tokens(string text) => TokenRegex().Matches(text?.ToLowerInvariant() ?? string.Empty).Cast<Match>()
        .Select(match => match.Value)
        .Where(token => token.Length > 1);

    [GeneratedRegex("[a-z']+")]
    private static partial Regex TokenRegex();
}

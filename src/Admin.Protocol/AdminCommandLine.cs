using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace DaocServer.Admin;

public sealed class AdminUsageException(string message) : Exception(message);

/// <summary>
/// The admin command grammar, shared by the daoc-admin CLI and the server console so both accept
/// exactly the same commands.
/// </summary>
public static class AdminCommandLine
{
    public const string Usage = """
        Commands:
          status
          server stop                     save and stop the server
          bots list [--realm alb|mid|hib] [--online]
          bots create [--realm alb|mid|hib|all] [--count N] [--level 1|50] [--class NAME]
          bots delete <name|id>
          bots delete-all --yes
          population                      show population settings
          population on | off             off also logs every bot out of the world
          population max <N>              most bots in the world at once; 0 = whole roster
          accounts list
          accounts show <name>
          accounts create <name> <password>
          accounts set-role <name> player|gm|admin
          options                         announcement, teleporter and siege switches
          options set <key> <value>       on|off for switches, a number for the rest
          goals                           bot goal percentages per level bracket
          goals set <1-19|20-49|50> <solo> <group> <rvr> <battlegrounds>
                                          percentages adding up to 100
          rvr                             battlegrounds, keeps, relics and raids
        """;

    /// <summary>First words that the server console treats as admin commands.</summary>
    public static readonly IReadOnlySet<string> Groups =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "status", "server", "bots", "population", "accounts", "options", "goals", "rvr" };

    public static AdminRequest Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            throw new AdminUsageException(Usage);

        string group = args[0].ToLowerInvariant();
        string verb = args.Count > 1 ? args[1].ToLowerInvariant() : string.Empty;
        var rest = args.Skip(2).ToList();

        return (group, verb) switch
        {
            ("status", "") => Request(AdminOps.Status),
            ("server", "stop") => Request(AdminOps.Shutdown),
            ("bots", "list") => BotsList(rest),
            ("bots", "create") => BotsCreate(rest),
            ("bots", "delete") => Request(AdminOps.BotsDelete, ("bot", Single(rest, "bots delete <name|id>"))),
            ("bots", "delete-all") => BotsDeleteAll(rest),
            ("population", "" or "show") => Request(AdminOps.PopulationGet),
            ("population", "on") => Request(AdminOps.PopulationSet, ("enabled", true)),
            ("population", "off") => Request(AdminOps.PopulationSet, ("enabled", false)),
            ("population", "max") => Request(AdminOps.PopulationSet, ("maxActiveBots", ParseInt(Single(rest, "population max <N>"), 0, 100_000, "N"))),
            ("accounts", "list") => Request(AdminOps.AccountsList),
            ("accounts", "show") => Request(AdminOps.AccountsShow, ("name", Single(rest, "accounts show <name>"))),
            ("accounts", "create") => AccountsCreate(rest),
            ("accounts", "set-role") => AccountsSetRole(rest),
            ("options", "" or "list") => Request(AdminOps.OptionsList),
            ("options", "set") => OptionsSet(rest),
            ("goals", "" or "show") => Request(AdminOps.GoalsGet),
            ("goals", "set") => GoalsSet(rest),
            ("rvr", "") => Request(AdminOps.RvrStatus),
            _ => throw new AdminUsageException($"Unknown command: {string.Join(' ', args)}\n\n{Usage}"),
        };
    }

    /// <summary>Splits a console line on whitespace; double quotes group words.</summary>
    public static List<string> SplitLine(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    public static string NormalizeRealm(string value) => value.ToLowerInvariant() switch
    {
        "alb" or "albion" or "1" => "albion",
        "mid" or "midgard" or "2" => "midgard",
        "hib" or "hibernia" or "3" => "hibernia",
        "all" => "all",
        _ => throw new AdminUsageException($"Unknown realm '{value}'. Use alb, mid, hib or all."),
    };

    private static AdminRequest BotsList(List<string> rest)
    {
        var options = Options(rest, flags: ["--online"], values: ["--realm"]);
        var request = Request(AdminOps.BotsList);
        if (options.TryGetValue("--realm", out string? realm))
            request.Args["realm"] = NormalizeRealm(realm!);
        if (options.ContainsKey("--online"))
            request.Args["onlineOnly"] = true;
        return request;
    }

    private static AdminRequest BotsCreate(List<string> rest)
    {
        var options = Options(rest, flags: [], values: ["--realm", "--count", "--level", "--class"]);
        var request = Request(AdminOps.BotsCreate,
            ("realm", NormalizeRealm(options.GetValueOrDefault("--realm") ?? "all")),
            ("count", ParseInt(options.GetValueOrDefault("--count") ?? "1", 1, 100, "--count")),
            ("level", ParseInt(options.GetValueOrDefault("--level") ?? "1", 1, 50, "--level")));
        if (options.TryGetValue("--class", out string? className))
            request.Args["class"] = className;
        return request;
    }

    private static AdminRequest BotsDeleteAll(List<string> rest)
    {
        if (!rest.Contains("--yes"))
            throw new AdminUsageException("This permanently deletes every bot character. Repeat with --yes to confirm.");
        return Request(AdminOps.BotsDeleteAll, ("confirm", true));
    }

    private static AdminRequest AccountsCreate(List<string> rest)
    {
        if (rest.Count != 2)
            throw new AdminUsageException("Usage: accounts create <name> <password>");
        return Request(AdminOps.AccountsCreate, ("name", rest[0]), ("password", rest[1]));
    }

    private static AdminRequest AccountsSetRole(List<string> rest)
    {
        if (rest.Count != 2)
            throw new AdminUsageException("Usage: accounts set-role <name> player|gm|admin");
        return Request(AdminOps.AccountsSetRole, ("name", rest[0]), ("role", rest[1].ToLowerInvariant()));
    }

    private static AdminRequest OptionsSet(List<string> rest)
    {
        if (rest.Count != 2)
            throw new AdminUsageException("Usage: options set <key> <value>");
        return Request(AdminOps.OptionsSet, ("key", rest[0].ToLowerInvariant()), ("value", rest[1]));
    }

    public static readonly IReadOnlyList<string> GoalBrackets = ["1-19", "20-49", "50"];

    private static AdminRequest GoalsSet(List<string> rest)
    {
        const string usage = "Usage: goals set <1-19|20-49|50> <solo> <group> <rvr> <battlegrounds>";
        if (rest.Count != 5)
            throw new AdminUsageException(usage);
        if (!GoalBrackets.Contains(rest[0]))
            throw new AdminUsageException($"Unknown level bracket '{rest[0]}'. {usage}");
        return Request(AdminOps.GoalsSet,
            ("bracket", rest[0]),
            ("solo", ParseInt(rest[1], 0, 100, "solo")),
            ("group", ParseInt(rest[2], 0, 100, "group")),
            ("rvr", ParseInt(rest[3], 0, 100, "rvr")),
            ("battlegrounds", ParseInt(rest[4], 0, 100, "battlegrounds")));
    }

    private static Dictionary<string, string?> Options(List<string> rest, string[] flags, string[] values)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rest.Count; i++)
        {
            string arg = rest[i];
            if (flags.Contains(arg, StringComparer.OrdinalIgnoreCase))
                result[arg] = null;
            else if (values.Contains(arg, StringComparer.OrdinalIgnoreCase) && i + 1 < rest.Count)
                result[arg] = rest[++i];
            else
                throw new AdminUsageException($"Unexpected argument '{arg}'.\n\n{Usage}");
        }
        return result;
    }

    private static string Single(List<string> rest, string usage) =>
        rest.Count == 1 ? rest[0] : throw new AdminUsageException($"Usage: {usage}");

    private static int ParseInt(string value, int min, int max, string name) =>
        int.TryParse(value, out int result) && result >= min && result <= max
            ? result
            : throw new AdminUsageException($"{name} must be a whole number from {min} to {max}.");

    private static AdminRequest Request(string op, params (string Name, JsonNode? Value)[] args)
    {
        var request = new AdminRequest { Op = op };
        foreach (var (name, value) in args)
            request.Args[name] = value;
        return request;
    }
}

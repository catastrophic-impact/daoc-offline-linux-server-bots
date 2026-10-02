using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace DaocServer.Admin;

/// <summary>Plain-text rendering of admin results for the CLI and the server console.</summary>
public static class AdminText
{
    public static string Format(string op, JsonNode? result)
    {
        switch (op)
        {
            case AdminOps.Status:
            {
                var s = AdminJson.To<ServerStatus>(result)!;
                return $"""
                    {s.Name} ({s.Edition}, {s.Version})
                    Uptime:     {TimeSpan.FromSeconds(s.UptimeSeconds):d\.hh\:mm\:ss}
                    Players:    {s.PlayersOnline} online
                    Bots:       {s.BotsOnline} in the world, {s.BotRoster} in the roster
                    Population: {(s.PopulationEnabled ? "ON" : "OFF")}, max active {MaxText(s.MaxActiveBots)}
                    """;
            }
            case AdminOps.Shutdown:
                return "Server is saving and stopping.";
            case AdminOps.BotsList:
            {
                var bots = AdminJson.To<List<BotInfo>>(result)!;
                if (bots.Count == 0)
                    return "No bots. Create some with: bots create --realm all --count 10";
                string table = Table(
                    ["Name", "Realm", "Class", "Lv", "Zone", "State", "Activity"],
                    bots.Select(b => new[] { b.Name, b.Realm, b.Class, b.Level.ToString(), b.Zone, BotState(b), b.Activity }));
                int online = bots.Count(b => b.Online);
                return $"{table}\n{bots.Count} bots, {online} in the world.";
            }
            case AdminOps.BotsCreate:
            {
                var r = AdminJson.To<BotCreateResult>(result)!;
                string names = string.Join(", ", r.Names);
                return $"Created {r.Names.Count} bot(s): {names}\n" +
                       (r.PopulationEnabled
                           ? "Population is ON: they log in over the next minutes."
                           : "Population is OFF: they stay offline until you run 'population on'.");
            }
            case AdminOps.BotsDelete:
            case AdminOps.BotsDeleteAll:
            {
                var r = AdminJson.To<BotDeleteResult>(result)!;
                return $"Deletion queued for {r.Queued} bot(s). The server removes them from the world, then deletes their data.";
            }
            case AdminOps.PopulationGet:
            case AdminOps.PopulationSet:
            {
                var p = AdminJson.To<PopulationInfo>(result)!;
                return $"Population {(p.Enabled ? "ON" : "OFF")}: {p.Online} of {p.Roster} roster bots in the world, max active {MaxText(p.MaxActiveBots)}.";
            }
            case AdminOps.AccountsList:
            {
                var accounts = AdminJson.To<List<AccountInfo>>(result)!;
                if (accounts.Count == 0)
                    return "No accounts yet.";
                return Table(
                    ["Account", "Role", "Online", "Chars", "Last login"],
                    accounts.Select(a => new[] { a.Name, a.Role, a.Online ? "yes" : "", a.Characters.ToString(), Date(a.LastLogin) }));
            }
            case AdminOps.AccountsShow:
            case AdminOps.AccountsCreate:
            case AdminOps.AccountsSetRole:
            {
                var a = AdminJson.To<AccountInfo>(result)!;
                return $"{a.Name}: {a.Role}{(a.Online ? ", online" : "")}, {a.Characters} character(s), created {Date(a.Created)}, last login {Date(a.LastLogin)}";
            }
            default:
                return result?.ToJsonString() ?? "OK";
        }
    }

    public static string BotState(BotInfo bot) => bot.Deleting ? "deleting" : bot.Online ? "online" : "offline";

    public static string MaxText(int maxActive) => maxActive > 0 ? maxActive.ToString() : "whole roster";

    private static string Date(DateTime value) => value == default ? "never" : value.ToString("yyyy-MM-dd HH:mm");

    public static string Table(string[] headers, IEnumerable<string[]> rows)
    {
        const int maxWidth = 40;
        var all = rows.Select(r => r.Select(c => Clip(c ?? string.Empty, maxWidth)).ToArray()).ToList();
        int[] widths = headers.Select((h, i) => Math.Max(h.Length, all.Count == 0 ? 0 : all.Max(r => r[i].Length))).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine(Row(headers, widths));
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (string[] row in all)
            sb.AppendLine(Row(row, widths));
        return sb.ToString().TrimEnd();
    }

    private static string Row(string[] cells, int[] widths) =>
        string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

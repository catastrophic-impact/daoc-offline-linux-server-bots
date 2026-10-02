using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DaocServer.Admin;

/// <summary>
/// The admin protocol: one JSON object per line over a local Unix socket.
/// Request  {"id":1,"op":"bots.list","args":{...}}
/// Response {"id":1,"ok":true,"result":...} or {"id":1,"ok":false,"error":{"code":"not_found","message":"..."}}
/// </summary>
public static class AdminOps
{
    public const string Status = "server.status";
    public const string Shutdown = "server.stop";
    public const string BotsList = "bots.list";
    public const string BotsCreate = "bots.create";
    public const string BotsDelete = "bots.delete";
    public const string BotsDeleteAll = "bots.deleteAll";
    public const string PopulationGet = "population.get";
    public const string PopulationSet = "population.set";
    public const string AccountsList = "accounts.list";
    public const string AccountsShow = "accounts.show";
    public const string AccountsCreate = "accounts.create";
    public const string AccountsSetRole = "accounts.setRole";
}

public static class AdminErrorCodes
{
    public const string NotFound = "not_found";
    public const string Invalid = "invalid";
    public const string Refused = "refused";
    public const string Failed = "failed";
}

public sealed class AdminRequest
{
    public int Id { get; set; }
    public string Op { get; set; } = string.Empty;
    public JsonObject Args { get; set; } = new();

    public string? GetString(string name) => Args[name]?.GetValue<string>();
    public int? GetInt(string name) => Args[name]?.GetValue<int>();
    public bool? GetBool(string name) => Args[name]?.GetValue<bool>();
}

public sealed class AdminError
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class AdminResponse
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public JsonNode? Result { get; set; }
    public AdminError? Error { get; set; }

    public static AdminResponse Success(int id, object? result) =>
        new() { Id = id, Ok = true, Result = result == null ? null : JsonSerializer.SerializeToNode(result, AdminJson.Options) };

    public static AdminResponse Failure(int id, string code, string message) =>
        new() { Id = id, Ok = false, Error = new AdminError { Code = code, Message = message } };
}

/// <summary>A refusal the caller should see as-is (bad input, unknown account, ...).</summary>
public sealed class AdminException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class AdminJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static T? To<T>(JsonNode? node) => node == null ? default : node.Deserialize<T>(Options);
}

public static class AdminRoles
{
    public const string Player = "player";
    public const string Gm = "gm";
    public const string Admin = "admin";

    /// <summary>OpenDAoC ePrivLevel: Player = 1, GM = 2, Admin = 3.</summary>
    public static uint ToPrivLevel(string role) => role.ToLowerInvariant() switch
    {
        Player => 1,
        Gm => 2,
        Admin => 3,
        _ => throw new AdminException(AdminErrorCodes.Invalid, $"Unknown role '{role}'. Use player, gm or admin."),
    };

    public static string FromPrivLevel(uint privLevel) => privLevel switch
    {
        >= 3 => Admin,
        2 => Gm,
        _ => Player,
    };
}

public sealed record ServerStatus(
    string Name,
    string Version,
    string Edition,
    long UptimeSeconds,
    int PlayersOnline,
    int BotsOnline,
    int BotRoster,
    bool PopulationEnabled,
    int MaxActiveBots);

public sealed record BotInfo(
    long Id,
    string Name,
    string Realm,
    string Class,
    string Race,
    int Level,
    string Zone,
    string Activity,
    string Goal,
    bool Online,
    bool Deleting);

public sealed record BotCreateResult(IReadOnlyList<string> Names, bool PopulationEnabled);

public sealed record BotDeleteResult(int Queued);

public sealed record PopulationInfo(bool Enabled, int MaxActiveBots, int Roster, int Online);

public sealed record AccountInfo(
    string Name,
    string Role,
    bool Online,
    int Characters,
    DateTime Created,
    DateTime LastLogin);

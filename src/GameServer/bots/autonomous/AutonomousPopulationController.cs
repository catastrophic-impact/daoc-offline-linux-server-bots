using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Collections.Generic;
using System.Numerics;
using DOL.Database;
using DOL.Database.Attributes;
using DOL.Events;
using DOL.GS.Commands;
using DOL.GS.GameEvents;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS;

/// <summary>
/// Loads persistent launcher-created characters as real world actors according
/// to the configured startup ramp. It never invents offline progress and never
/// creates characters on its own.
/// </summary>
public static class AutonomousPopulationController
{
    public const string TeleportToBotCommandType = "TeleportToBot";
    public const int MaximumSpawnEnqueuePerPoll = 16;
    private const float MaximumStartupGroundDelta = 96f;
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly ConcurrentDictionary<long, byte> PendingSpawns = new();
    private static readonly ConcurrentDictionary<long, byte> PendingCommands = new();
    private static readonly ConcurrentDictionary<long, byte> PendingLogouts = new();
    private static System.Threading.Timer _timer;
    private static DateTime _startedUtc;
    private static DateTime _nextRosterRefreshUtc;
    private static DateTime _nextCommandPollUtc;
    private static DateTime _nextOrphanRepairUtc;
    private static volatile OfflineWorldBotRecord[] _cachedRoster = Array.Empty<OfflineWorldBotRecord>();
    private static bool _cachedEnabled;
    private static int _cachedRampMinutes;
    private static int _cachedLoginsPerSecond = 8;
    private static int _cachedMaxActive;
    private static int _polling;
    private static int _loginGeneration;

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        Interlocked.Increment(ref _loginGeneration);
        _startedUtc = DateTime.UtcNow;
        OfflineWorldBotRecord[] previouslyOnline = DOLDB<OfflineWorldBotRecord>
            .SelectObjects(DB.Column("IsOnline").IsEqualTo(true))
            .ToArray();
        foreach (OfflineWorldBotRecord record in previouslyOnline)
        {
            record.IsOnline = false;
            record.Activity = record.IsRetired ? "Deletion requested" : "Queued for staggered login";
            record.Dirty = true;
        }
        if (previouslyOnline.Length > 0)
        {
            lock (AutonomousBotStatusPersistence.DatabaseWriteLock)
                GameServer.Database.SaveObject(previouslyOnline.Cast<DataObject>());
        }
        RefreshControlPlane(force: true);
        _nextCommandPollUtc = DateTime.MinValue;
        _nextOrphanRepairUtc = DateTime.MinValue;
        _timer = new System.Threading.Timer(Poll, null, 500, 1000);
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        Interlocked.Increment(ref _loginGeneration);
        _timer?.Dispose();
        _timer = null;
        // Persist the coalesced launcher/status batch here. Inventories are
        // event-driven and included only when loot, equipment, buying, selling,
        // or training actually changed them; there is no shutdown-wide rewrite.
        AutonomousBotStatusPersistence.FlushAll();
        PendingSpawns.Clear();
        PendingCommands.Clear();
        PendingLogouts.Clear();
        _cachedRoster = Array.Empty<OfflineWorldBotRecord>();
    }

    private static void Poll(object state)
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
            return;

        try
        {
            DateTime nowUtc = DateTime.UtcNow;
            RefreshControlPlane(force: false);
            if (nowUtc >= _nextCommandPollUtc)
            {
                _nextCommandPollUtc = nowUtc.AddSeconds(3);
                bool repairOrphans = nowUtc >= _nextOrphanRepairUtc;
                if (repairOrphans)
                    _nextOrphanRepairUtc = nowUtc.AddMinutes(1);
                QueueOwnerCommands(repairOrphans);
            }

            OfflineWorldBotRecord[] roster = _cachedRoster;
            int desired = AutonomousPopulationRamp.DesiredActiveCount(_cachedEnabled, roster.Length, _cachedRampMinutes, nowUtc - _startedUtc);
            if (_cachedMaxActive > 0)
                desired = Math.Min(desired, _cachedMaxActive);
            LogOutExcessBots();
            int missing = desired - AutonomousBotRegistry.Count - PendingSpawns.Count;
            _populationSatisfied = missing <= 0;
            if (missing <= 0)
                return;

            var realmLoad = new Dictionary<eRealm, int>(AutonomousBotRegistry.CountByRealm());
            foreach (OfflineWorldBotRecord pending in roster.Where(record => PendingSpawns.ContainsKey(record.BotId)))
            {
                eRealm realm = (eRealm)pending.Realm;
                realmLoad[realm] = realmLoad.TryGetValue(realm, out int count) ? count + 1 : 1;
            }

            List<OfflineWorldBotRecord> available = roster
                .Where(record => !record.IsOnline && !AutonomousBotRegistry.Contains(record.BotId) && !PendingSpawns.ContainsKey(record.BotId))
                .ToList();

            // The poll runs once a second: bot_logins_per_second bounds each pulse,
            // so a small roster is in within seconds and a large one never bursts.
            int enqueueCount = Math.Min(Math.Min(missing, available.Count), _cachedLoginsPerSecond);
            for (int i = 0; i < enqueueCount; i++)
            {
                long? nextId = AutonomousRealmLoginBalancer.SelectNext(
                    available.Select(record => new AutonomousRealmLoginBalancer.Candidate(record.BotId, (eRealm)record.Realm)),
                    realmLoad);
                OfflineWorldBotRecord next = nextId.HasValue
                    ? available.FirstOrDefault(record => record.BotId == nextId.Value)
                    : null;
                if (next == null)
                    break;
                available.Remove(next);
                if (!PendingSpawns.TryAdd(next.BotId, 0))
                    continue;

                eRealm realm = (eRealm)next.Realm;
                realmLoad[realm] = realmLoad.TryGetValue(realm, out int count) ? count + 1 : 1;
                try
                {
                    // Fetch the exact real inventory on this existing background
                    // poller. Applying slots/bonuses and actor creation still belong
                    // to the game loop; it must never spin waiting for SQLite here.
                    int generation = Volatile.Read(ref _loginGeneration);
                    string ownerId = AutonomousBotEconomy.GetOwnerId(next.BotId);
                    var inventory = new BotInventory(ownerId);
                    var items = inventory.StartLoadFromDatabaseTask(ownerId).GetAwaiter().GetResult();
                    if (generation != Volatile.Read(ref _loginGeneration) || _timer == null)
                    {
                        PendingSpawns.TryRemove(next.BotId, out _);
                        return;
                    }
                    NpcService.Instance.Post(SpawnOnGameLoop, new PreparedSpawn(next.BotId, items, generation));
                }
                catch
                {
                    PendingSpawns.TryRemove(next.BotId, out _);
                    throw;
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error("Autonomous population poll failed", exception);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private sealed record PreparedSpawn(long BotId, System.Collections.IList Inventory, int Generation);

    private static void SpawnOnGameLoop(PreparedSpawn prepared)
    {
        long botId = prepared.BotId;
        try
        {
            if (prepared.Generation != Volatile.Read(ref _loginGeneration) || _timer == null) return;
            OfflineWorldBotRecord record = _cachedRoster.FirstOrDefault(candidate => candidate.BotId == botId);
            if (record == null || record.IsRetired || record.IsOnline || AutonomousBotRegistry.Contains(botId))
                return;

            GameBot bot = new(record, prepared.Inventory);
            if (IsFreshUnplacedLevelOneRecord(record))
            {
                if (!TryPlaceFreshBotAtAuthoritativeStart(bot, record))
                    PlaceAtCapitalFallback(bot, record);
            }
            else if (record.RegionId == 0)
            {
                // Preserve legacy handling for existing records with no usable
                // stored position. Recovery and bind paths intentionally remain
                // capital-based and are not part of new-character placement.
                PlaceAtCapitalFallback(bot, record);
            }

            // GameNPC keeps a separate interpolated movement position. Before
            // AddToWorld it still contains the constructor default (0,0,0),
            // even after the authoritative saved/start coordinates were set.
            // Synchronize it before validation so a valid Classic/SI start is
            // not mistaken for an invalid origin and replaced by a capital.
            bot.SynchronizePositionForLoginValidation();
            AutonomousStuckWatchdog.RepairInvalidLoginPosition(bot);
            if (string.IsNullOrWhiteSpace(record.LastSavedUtc) && record.Experience == 0)
            {
                bot.Health = bot.MaxHealth;
                bot.Mana = bot.MaxMana;
                bot.Endurance = bot.MaxEndurance;
            }

            if (!bot.AddToWorld())
            {
                record.IsOnline = false;
                record.Activity = "Login attempt failed; waiting to retry";
                record.LastUpdateUtc = DateTime.UtcNow.ToString("O");
                record.Dirty = true;
                AutonomousBotStatusPersistence.Queue(bot);
                return;
            }

            record.IsOnline = true;
            record.Activity = "Choosing first live goal";
            record.CurrentGoal = "Find a reachable level-appropriate XP camp";
            record.ObjectiveProgress = "Entered world through staggered login queue";
            bot.MarkAutonomousStateDirty();
            AutonomousBotStatusPersistence.QueueLogin(bot);
            Log.Info($"Autonomous character {record.Name} entered the live world from the staggered queue.");
        }
        catch (Exception exception)
        {
            Log.Error($"Could not load autonomous bot {botId}", exception);
        }
        finally
        {
            PendingSpawns.TryRemove(botId, out _);
        }
    }

    /// <summary>Only a never-saved, zero-position level-one record may enter fresh-start placement.</summary>
    public static bool IsFreshUnplacedLevelOneRecord(OfflineWorldBotRecord record) =>
        record != null && record.Level == 1 && record.Experience == 0 && record.RealmPoints == 0 &&
        record.RegionId == 0 && string.IsNullOrWhiteSpace(record.LastSavedUtc);

    private static void PlaceAtCapitalFallback(GameBot bot, OfflineWorldBotRecord record)
    {
        AutonomousStuckWatchdog.CapitalLocation capital = AutonomousStuckWatchdog.SafeCapitalFor((eRealm)record.Realm);
        bot.CurrentRegionID = capital.RegionId;
        bot.X = capital.X;
        bot.Y = capital.Y;
        bot.Z = capital.Z;
    }

    /// <summary>
    /// New level-one autonomous actors use the server's normal StartupLocation
    /// data, after validating the live map and navmesh. Existing records never
    /// enter this path, so watchdog recovery stays capital-only.
    /// </summary>
    private static bool TryPlaceFreshBotAtAuthoritativeStart(GameBot bot, OfflineWorldBotRecord record)
    {
        StartupLocation[] candidates = StartupLocations
            .GetClassicSiLocationsForAutonomous((eRealm)record.Realm, record.RaceId, record.ClassId)
            .ToArray();
        if (candidates.Length == 0)
            return false;

        var valid = new List<(StartupLocation Location, Vector3 Position)>();
        foreach (StartupLocation candidate in candidates)
        {
            Region region = WorldMgr.GetRegion((ushort)candidate.Region);
            if (region == null || region.IsCapitalCity)
                continue;

            Zone zone = region.GetZone(candidate.XPos, candidate.YPos);
            if (zone == null)
                continue;

            Vector3 snappedPosition = new(candidate.XPos, candidate.YPos, candidate.ZPos);
            if (PathfindingProvider.Instance.IsAvailable && PathfindingProvider.Instance.HasNavmesh(zone) &&
                !PathfindingProvider.Instance.TrySnapToMesh(zone, ref snappedPosition, 768f))
            {
                continue;
            }
            if (Math.Abs(snappedPosition.Z - candidate.ZPos) > MaximumStartupGroundDelta)
                continue;
            // A point can be on a valid polygon yet isolated on a prop/ledge.
            // Only fresh-character placement is filtered; no live bot is moved
            // and no collision filter is relaxed.
            if (PathfindingProvider.Instance.IsAvailable && PathfindingProvider.Instance.HasNavmesh(zone) &&
                !AutonomousRendezvousNavigation.HasLocalExit(PathfindingProvider.Instance, zone, snappedPosition))
                continue;

            valid.Add((candidate, snappedPosition));
        }

        if (valid.Count == 0)
            return false;

        // The only fresh-character placement draw: all validated, applicable
        // Classic+SI start locations are equally likely. Capital is reached
        // only through the explicit no-valid-start fallback above or watchdog
        // recovery, never through a location preference here.
        StartupLocation chosenLocation = StartupLocations.ChooseUniformAutonomousLocation(
            valid.Select(candidate => candidate.Location), Random.Shared);
        (StartupLocation location, Vector3 chosenPosition) = valid.First(candidate => ReferenceEquals(candidate.Location, chosenLocation));
        bot.CurrentRegionID = (ushort)location.Region;
        bot.X = (int)Math.Round(chosenPosition.X);
        bot.Y = (int)Math.Round(chosenPosition.Y);
        bot.Z = (int)Math.Round(chosenPosition.Z);
        bot.Heading = (ushort)Math.Clamp(location.Heading, 0, ushort.MaxValue);
        return true;
    }

    private static void RefreshControlPlane(bool force)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (!force && nowUtc < _nextRosterRefreshUtc)
            return;
        // Refresh quickly while actors are still being admitted. Once every
        // requested actor is live, the full 6,000-row roster read only needs to
        // notice launcher-side roster edits, so a slower cadence is enough.
        _nextRosterRefreshUtc = nowUtc.AddSeconds(force ? 10 : _populationSatisfied ? 30 : 8);

        // Pure reads: SQLite WAL readers never block the writer, so this no
        // longer holds the shared write lock that game-loop transactions wait on.
        _cachedEnabled = ReadBool("population_enabled", false);
        _cachedRampMinutes = ReadInt("startup_ramp_minutes", 0);
        _cachedLoginsPerSecond = Math.Clamp(ReadInt("bot_logins_per_second", 8), 1, MaximumSpawnEnqueuePerPoll);
        _cachedMaxActive = Math.Max(0, ReadInt("max_active_bots", 0));
        _cachedRoster = DOLDB<OfflineWorldBotRecord>
            .SelectObjects(DB.Column("IsRetired").IsEqualTo(false))
            .OrderBy(record => record.BotId)
            .ToArray();
    }

    private static volatile bool _populationSatisfied;

    /// <summary>Re-read settings and roster on the next poll (after an admin change).</summary>
    public static void RequestRefresh() => _nextRosterRefreshUtc = DateTime.MinValue;

    /// <summary>
    /// The owner lowered max_active_bots or turned the population off: log extra bots out of the
    /// world, a bounded batch per poll. Their characters stay in the roster. Bots grouped with a
    /// human player are left alone.
    /// </summary>
    private static void LogOutExcessBots()
    {
        int allowed = !_cachedEnabled ? 0 : _cachedMaxActive > 0 ? _cachedMaxActive : int.MaxValue;
        int excess = AutonomousBotRegistry.Count - PendingLogouts.Count - allowed;
        if (excess <= 0)
            return;

        foreach (GameBot bot in AutonomousBotRegistry.Snapshot()
                     .Where(bot => bot.PersistentRecord != null && !PendingLogouts.ContainsKey(bot.PersistentRecord.BotId))
                     .Where(bot => bot.Group == null || bot.Group.GetPlayersInTheGroup().Count == 0)
                     .OrderBy(bot => bot.InCombat ? 1 : 0)
                     .Take(Math.Min(excess, MaximumSpawnEnqueuePerPoll)))
        {
            if (PendingLogouts.TryAdd(bot.PersistentRecord.BotId, 0))
                NpcService.Instance.Post(LogOutOnGameLoop, bot);
        }
    }

    private static void LogOutOnGameLoop(GameBot bot)
    {
        try
        {
            // Delete() saves the bot's state and removes it from the world; its record stays.
            if (bot.ObjectState == GameObject.eObjectState.Active)
                bot.Delete();
        }
        catch (Exception exception)
        {
            Log.Error($"Could not log autonomous bot {bot.Name} out of the world", exception);
        }
        finally
        {
            PendingLogouts.TryRemove(bot.PersistentRecord.BotId, out _);
        }
    }

    private static void QueueOwnerCommands(bool repairOrphans)
    {
        bool lockTaken = false;
        try
        {
            // A status flush or server-wide save owns SQLite. A population poll
            // is optional and must never block the game loop or shutdown behind
            // SQLite's busy timeout.
            Monitor.TryEnter(AutonomousBotStatusPersistence.DatabaseWriteLock, 0, ref lockTaken);
            if (!lockTaken)
                return;

        // Repair the narrow orphan case where an older server build marked a
        // delete command Completed without proving the durable row disappeared.
        // Only a bot whose latest command is Completed is retried; Failed
        // commands are left visible for diagnosis instead of spinning forever.
        foreach (OfflineWorldBotRecord retired in repairOrphans
                     ? DOLDB<OfflineWorldBotRecord>.SelectObjects(DB.Column("IsRetired").IsEqualTo(true))
                     : Array.Empty<OfflineWorldBotRecord>())
        {
            OfflineBotCommandRecord latest = DOLDB<OfflineBotCommandRecord>
                .SelectObjects(DB.Column("BotId").IsEqualTo(retired.BotId))
                .OrderByDescending(entry => entry.CommandId)
                .FirstOrDefault();
            if (latest != null && string.Equals(latest.CommandType, "Delete", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(latest.State, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                GameServer.Database.AddObject(new OfflineBotCommandRecord
                {
                    BotId = retired.BotId,
                    CommandType = "Delete",
                    State = "Pending",
                    RequestedUtc = DateTime.UtcNow.ToString("O"),
                    CompletedUtc = string.Empty,
                    Error = "Retrying an orphaned completed deletion",
                });
            }
        }

        foreach (OfflineBotCommandRecord command in DOLDB<OfflineBotCommandRecord>
                     .SelectObjects(DB.Column("State").IsEqualTo("Pending"))
                     .OrderBy(command => command.CommandId))
        {
            if (!PendingCommands.TryAdd(command.CommandId, 0))
                continue;
            NpcService.Instance.Post(ProcessOwnerCommandOnGameLoop, command.CommandId);
        }
        }
        finally
        {
            if (lockTaken)
                Monitor.Exit(AutonomousBotStatusPersistence.DatabaseWriteLock);
        }
    }

    private static void ProcessOwnerCommandOnGameLoop(long commandId)
    {
        OfflineBotCommandRecord command = null;
        try
        {
            command = DOLDB<OfflineBotCommandRecord>.SelectObject(DB.Column("CommandId").IsEqualTo(commandId));
            if (command == null || !string.Equals(command.State, "Pending", StringComparison.OrdinalIgnoreCase))
                return;

            command.State = "Processing";
            command.Dirty = true;
            GameServer.Database.SaveObject(command);
            if (string.Equals(command.CommandType, TeleportToBotCommandType, StringComparison.OrdinalIgnoreCase))
            {
                TeleportOwnerToBot(command);
            }
            else if (string.Equals(command.CommandType, "Delete", StringComparison.OrdinalIgnoreCase))
            {
                DeleteBot(command.BotId);
            }
            else
            {
                throw new InvalidOperationException($"Unknown autonomous bot command: {command.CommandType}");
            }

            command.State = "Completed";
            command.CompletedUtc = DateTime.UtcNow.ToString("O");
            command.Error = string.Empty;
            command.Dirty = true;
            GameServer.Database.SaveObject(command);
        }
        catch (Exception exception)
        {
            Log.Error($"Autonomous owner command {commandId} failed", exception);
            if (command != null)
            {
                command.State = "Failed";
                command.CompletedUtc = DateTime.UtcNow.ToString("O");
                command.Error = exception.Message;
                command.Dirty = true;
                GameServer.Database.SaveObject(command);
            }
        }
        finally
        {
            PendingCommands.TryRemove(commandId, out _);
        }
    }

    private static void TeleportOwnerToBot(OfflineBotCommandRecord command)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedByAccount))
            throw new InvalidOperationException("The launcher teleport request did not identify its local account.");

        GameClient client = ClientService.Instance.GetClientFromAccountName(command.RequestedByAccount.Trim());
        if (client?.ClientState != GameClient.eClientState.Playing ||
            client.Player?.ObjectState != GameObject.eObjectState.Active)
            throw new InvalidOperationException($"Account '{command.RequestedByAccount}' has no character currently in the game.");

        if (!AutonomousBotRegistry.TryGet(command.BotId, out GameBot bot))
            throw new InvalidOperationException($"Playerbot {command.BotId} is no longer online.");

        if (!PlayerBotTeleport.TryTeleport(client.Player, bot, out string message))
            throw new InvalidOperationException(message);

        client.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_SystemWindow);
        Log.Info($"Launcher teleported account {command.RequestedByAccount} to autonomous character {command.BotId} ({bot.Name}).");
    }

    private static void DeleteBot(long botId)
    {
        OfflineWorldBotRecord record = DOLDB<OfflineWorldBotRecord>.SelectObject(DB.Column("BotId").IsEqualTo(botId));
        if (AutonomousBotRegistry.TryGet(botId, out GameBot liveBot))
            liveBot.Delete();

        string ownerId = AutonomousBotEconomy.GetOwnerId(botId);
        DbInventoryItem[] items = DOLDB<DbInventoryItem>.SelectObjects(DB.Column("OwnerID").IsEqualTo(ownerId)).ToArray();
        foreach (DbInventoryItem item in items)
        {
            if (RealmExchangeBroker.IsExchangeOwnerLot(item.OwnerLot))
                MarketCache.RemoveItem(item);
        }
        if (items.Length > 0)
        {
            foreach (DbInventoryItem item in items)
                item.AllowDelete = true;
            if (!GameServer.Database.DeleteObject(items))
                throw new InvalidOperationException($"Could not delete inventory owned by autonomous character {botId}");
        }
        if (record != null)
        {
            record.AllowDelete = true;
            if (!GameServer.Database.DeleteObject(record))
                throw new InvalidOperationException($"Could not delete autonomous character record {botId}");
            if (DOLDB<OfflineWorldBotRecord>.SelectObject(DB.Column("BotId").IsEqualTo(botId)) != null)
                throw new InvalidOperationException($"Autonomous character record {botId} still exists after deletion");
        }

        Log.Warn($"Owner permanently deleted autonomous character {botId} after safe world removal.");
    }

    private static int ReadInt(string key, int fallback) =>
        int.TryParse(ReadProperty(key), out int value) ? value : fallback;

    private static bool ReadBool(string key, bool fallback) =>
        bool.TryParse(ReadProperty(key), out bool value) ? value : fallback;

    private static string ReadProperty(string key) =>
        DOLDB<DbServerProperty>.SelectObject(DB.Column("Key").IsEqualTo(key))?.Value;
}

[DataTable(TableName = "offline_bot_commands")]
public sealed class OfflineBotCommandRecord : DataObject
{
    [PrimaryKey(AutoIncrement = true)] public long CommandId { get; set; }
    [DataElement(AllowDbNull = false)] public long BotId { get; set; }
    [DataElement(AllowDbNull = false)] public string CommandType { get; set; } = string.Empty;
    [DataElement(AllowDbNull = false)] public string RequestedUtc { get; set; } = string.Empty;
    [DataElement(AllowDbNull = false)] public string State { get; set; } = "Pending";
    [DataElement(AllowDbNull = true)] public string RequestedByAccount { get; set; }
    [DataElement(AllowDbNull = true)] public string CompletedUtc { get; set; }
    [DataElement(AllowDbNull = true)] public string Error { get; set; }
}

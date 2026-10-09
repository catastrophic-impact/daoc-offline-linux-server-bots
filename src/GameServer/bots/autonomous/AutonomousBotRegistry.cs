using System.Collections.Concurrent;
using System;
using System.Linq;
using System.Collections.Generic;
using DOL.Database;
using System.Threading;

namespace DOL.GS;

/// <summary>Keeps live autonomous actors available to the normal server save cycle.</summary>
public static class AutonomousBotRegistry
{
    private static readonly ConcurrentDictionary<long, GameBot> Active = new();
    private static int _populationForBrainTick;
    private static long _nextPopulationSampleTick;
    private const int PopulationSampleIntervalMs = 1_000;

    // A cadence input, not a replacement for the exact live population checks
    // used by spawning, transfer recovery and the launcher. Sample once before
    // parallel AI dispatch instead of copying/counting 4,500 actors per brain.
    public static int PopulationForBrainTick => Volatile.Read(ref _populationForBrainTick);

    public static void PrepareBrainTick()
    {
        // This walk runs serially on the game-loop thread before the parallel
        // brain dispatch. Population and per-dungeon counts only steer AI
        // cadence and dungeon crowding, so sampling them once a second keeps
        // the same behavior without a 6,000-actor scan on every tick.
        long now = GameLoop.GameLoopTime;
        if (now < _nextPopulationSampleTick)
            return;
        _nextPopulationSampleTick = now + PopulationSampleIntervalMs;
        SamplePopulationNow();
    }

    /// <summary>Recounts live actors immediately, ignoring the once-a-second cadence.</summary>
    public static void SamplePopulationNow()
    {
        int count = 0;
        var dungeonPopulation = new Dictionary<ushort, int>();
        foreach (var entry in Active)
        {
            GameBot bot = entry.Value;
            if (bot?.ObjectState != GameObject.eObjectState.Active)
                continue;
            count++;

            ushort dungeonRegion = 0;
            if (bot.CurrentZone?.IsDungeon == true || bot.CurrentRegion?.IsDungeon == true)
                dungeonRegion = bot.CurrentRegionID;
            else
                AutonomousDungeonPopulationPolicy.TryGetAssignedRegion(
                    bot.PersistentRecord?.CurrentCampId, out dungeonRegion);
            if (dungeonRegion != 0)
                dungeonPopulation[dungeonRegion] = dungeonPopulation.GetValueOrDefault(dungeonRegion) + 1;
        }
        Volatile.Write(ref _populationForBrainTick, count);
        AutonomousDungeonPopulationPolicy.PublishPopulation(dungeonPopulation);
    }

    // A failed cross-region AddToWorld can briefly leave an entry inactive.
    // Population targets are counts of live actors, never dictionary slots.
    public static int Count => Active.Values.Count(bot => bot?.ObjectState == GameObject.eObjectState.Active);

    public static bool Contains(long botId) => Active.ContainsKey(botId);

    public static bool TryGet(long botId, out GameBot bot) => Active.TryGetValue(botId, out bot);

    // One array per game-loop tick, shared by every caller (all read-only): RvR planning built a
    // fresh 9,000-entry array per AI turn (55 MB of allocations in 40 s, run 9 trace).
    private sealed record CachedSnapshot(long Tick, GameBot[] Bots);
    private static volatile CachedSnapshot _snapshot = new(-1, []);
    public const long SnapshotMaxAgeMilliseconds = 250;

    public static GameBot[] Snapshot()
    {
        long tick = GameLoop.GameLoopTime;
        CachedSnapshot cached = _snapshot;
        // Register/Unregister invalidate it; otherwise a quarter-second-old roster is fine for
        // planning (callers check state themselves). Per-tick rebuilds were ~23 MB per 40 s.
        if (cached.Tick > 0 && tick >= cached.Tick && tick - cached.Tick < SnapshotMaxAgeMilliseconds) return cached.Bots;
        GameBot[] bots = Active.Values.Where(bot => bot?.ObjectState == GameObject.eObjectState.Active).ToArray();
        _snapshot = new(tick, bots);
        return bots;
    }

    public static IReadOnlyDictionary<eRealm, int> CountByRealm() => Active.Values
        .Where(bot => bot?.ObjectState == GameObject.eObjectState.Active)
        .GroupBy(bot => bot.Realm)
        .ToDictionary(group => group.Key, group => group.Count());

    public static bool TryGetByName(string name, out GameBot bot)
    {
        bot = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        string requestedName = name.Trim();
        bot = Active.Values.FirstOrDefault(candidate =>
            candidate?.ObjectState == GameObject.eObjectState.Active &&
            string.Equals(candidate.Name, requestedName, StringComparison.OrdinalIgnoreCase));
        return bot != null;
    }

    public static void Register(GameBot bot)
    {
        if (bot?.IsAutonomousWorldBot == true && bot.DatabaseID > 0)
        {
            Active[bot.DatabaseID] = bot;
            _snapshot = new(-1, []);
        }
    }

    public static void Unregister(GameBot bot)
    {
        if (bot?.DatabaseID > 0)
        {
            AutonomousGoalDiagnostics.End(bot, GoalAttemptEnd.Logout, "Bot left the active world; not a goal failure");
            Active.TryRemove(bot.DatabaseID, out _);
            _snapshot = new(-1, []);
            AutonomousBotEconomy.ForgetCachedDecision(bot.DatabaseID);
        }
    }

    public static int SaveAll()
    {
        GameBot[] bots = Active.Values
            .Where(bot => bot?.PersistentRecord?.IsPersisted == true)
            .ToArray();
        if (bots.Length == 0)
            return 0;

        // Inventory is already persisted by AutonomousBotStatusPersistence on
        // real inventory events. Rewriting every slot for every online bot made
        // a 739-bot server save monopolize SQLite for roughly nineteen seconds.
        // Queue the full state sweep into the same FIFO coalescer as live status
        // changes. At 32 records every two seconds, all 4,500 slots complete in
        // at most 4m42s while inventory and exchange writes retain low latency.
        foreach (GameBot bot in bots)
            AutonomousBotStatusPersistence.Queue(bot);
        return bots.Length;
    }
}

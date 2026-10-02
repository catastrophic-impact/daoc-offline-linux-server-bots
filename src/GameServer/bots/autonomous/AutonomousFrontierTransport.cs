using System;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.Scripts;

namespace DOL.GS;

/// <summary>Bot passengers on the existing Old Frontiers porter's cast cycle.</summary>
public static class AutonomousFrontierTransport
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<OFTeleporter, byte> Porters = new();
    public static void Register(OFTeleporter porter) => Porters[porter] = 0;
    public static void Unregister(OFTeleporter porter) => Porters.TryRemove(porter, out _);
    public static OFTeleporter NearestPorter(GameBot bot) => Porters.Keys
        .Where(p => p.ObjectState == GameObject.eObjectState.Active && p.CurrentRegion == bot.CurrentRegion && p.Realm == bot.Realm)
        .OrderBy(bot.GetDistanceTo).FirstOrDefault();

    public static GameBot[] BoardingParty(GameBot bot, Passage passage) =>
        HasCommittedSiegePassage(bot, passage) || passage.Medallion == "home_necklace" &&
        AutonomousObjectiveAssignments.Parse(bot.PersistentRecord?.ObjectiveKind) != eAutonomousObjectiveKind.RvR
            ? [bot] // Initial PvE meetups can have members returning from different frontiers.
            : bot.Group?.GetMembersInTheGroup().OfType<GameBot>().ToArray() ?? [bot];

    private sealed class BoardingBatch
    {
        public bool Active;
        public FrontierBoardingQueue<GameBot> Waiting = new();
        public System.Collections.Generic.HashSet<GameBot> Handled = new();
        public ECSGameTimer Timer;
        public long OpenUntil;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OFTeleporter, BoardingBatch> Batches = new();
    public const string RequestKey = "RvrFrontierPassage";
    public const int BoardingRadius = 500;
    public const int SlicePassengerLimit = 8;
    public const int SliceMilliseconds = 3;
    public const int SliceIntervalMilliseconds = 50;
    public const int BoardingWindowMilliseconds = 30_000;
    public static bool SliceFull(int processed, double elapsedMilliseconds) =>
        processed >= SlicePassengerLimit || elapsedMilliseconds >= SliceMilliseconds;

    public static Vector3 WaitingOffset(long botId, int attempt = 0)
    {
        // Stable per character, not per warband: no shared endpoint at the NPC.
        ulong seed = unchecked((ulong)botId * 11400714819323198485UL);
        double angle = ((seed >> 16) % 65536) * (Math.PI * 2 / 65536) + attempt * 2.399963229728653;
        float radius = 220 + (float)(seed % 191);
        return new((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
    }

    public static bool TryWaitingPoint(GameBot bot, OFTeleporter porter, out Vector3 point)
        => TryWaitingPoint(PathfindingProvider.Instance, porter.CurrentZone, new(porter.X, porter.Y, porter.Z),
            bot.Realm, bot.DatabaseID, out point);

    public static bool TryWaitingPoint(IPathfindingMgr nav, Zone zone, Vector3 center, eRealm realm, long botId, out Vector3 point)
    {
        point = default;
        if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Vector3 desired = center + WaitingOffset(botId, attempt);
            Vector3? floor = nav.GetClosestPoint(zone, desired, 32, 32, 96, nav.DefaultFilters);
            if (!floor.HasValue || Vector3.Distance(center, floor.Value) > 450 ||
                Vector2.Distance(new(center.X, center.Y), new(floor.Value.X, floor.Value.Y)) < 200 ||
                !AutonomousRealmBoundary.Allows(realm, zone.ZoneRegion.ID, zone.ID) ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, center, floor.Value)) continue;
            point = floor.Value;
            return true;
        }
        return false;
    }
    public sealed record Passage(ushort Region, string Medallion, GameLocation Location);
    public sealed record Request(OFTeleporter Porter, Passage Passage, string ForceId);

    private static AutonomousRvrEventLayer.Plan ActiveSiegePlan(GameBot bot)
    {
        if (bot?.IsAutonomousWorldBot != true || bot.IsPlayerLedGroup || bot.IsTemporaryGroupHelper ||
            !AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR)) return null;
        string force = bot.TempProperties.GetProperty<string>("RvrEventForce") ?? $"rvr-{bot.DatabaseID}";
        return AutonomousRvrEventLayer.KeepPlan(force, bot.Realm, GameLoop.GameLoopTime);
    }

    public static bool PassageMatchesSiege(GameBot bot, Passage passage)
    {
        var plan=ActiveSiegePlan(bot);
        return plan==null || plan.RegionId==passage?.Region;
    }
    public static bool HasCommittedSiegePassage(GameBot bot, Passage passage) =>
        ActiveSiegePlan(bot) is { } plan && plan.RegionId==passage?.Region;
    public static bool HasDefenderPriority(GameBot bot, Passage passage) =>
        ActiveSiegePlan(bot) is { Intent: AutonomousRvrEventLayer.Intent.DefendEvent } plan && plan.RegionId==passage?.Region;

    // These are the existing OFTeleporter destinations, shared with its player
    // callback. The two invading realms arrive at different native portal keeps.
    public static Passage Destination(eRealm realm, ushort region) => (realm, region) switch
    {
        (eRealm.Albion, 100) => new(100,"odin_necklace",new("Odin Alb",100,596364,631509,5971)),
        (eRealm.Albion, 200) => new(200,"emain_necklace",new("Emain Alb",200,475835,343661,4080)),
        (eRealm.Midgard, 1) => new(1,"hadrian_necklace",new("Hadrian Mid",1,655200,293217,4879)),
        (eRealm.Midgard, 200) => new(200,"emain_necklace",new("Emain Mid",200,474107,295199,3871)),
        (eRealm.Hibernia, 1) => new(1,"hadrian_necklace",new("Hadrian Hib",1,605743,293676,4839)),
        (eRealm.Hibernia, 100) => new(100,"odin_necklace",new("Odin Hib",100,596055,581400,6031)),
        (eRealm.Albion, 1) => new(1,"home_necklace",new("Home Alb",1,584285,477200,2600)),
        (eRealm.Midgard, 100) => new(100,"home_necklace",new("Home Mid",100,766811,669605,5736)),
        (eRealm.Hibernia, 200) => new(200,"home_necklace",new("Home Hib",200,334386,420071,5184)),
        _ => null,
    };

    public static DbInventoryItem Ticket(GameBot bot, Passage passage) => bot.Inventory.AllItems
        .FirstOrDefault(item => item.SlotPosition >= (int)eInventorySlot.FirstBackpack &&
            item.SlotPosition <= (int)eInventorySlot.LastBackpack && item.Count > 0 && item.Id_nb == passage.Medallion);

    public static bool Ready(GameBot bot, OFTeleporter porter, Request request) =>
        bot is { IsAutonomousWorldBot: true, IsAlive: true } && bot.Realm == porter.Realm &&
        !bot.IsPlayerLedGroup && !bot.IsTemporaryGroupHelper && !bot.IsStunned && !bot.IsMezzed &&
        bot.ObjectState == GameObject.eObjectState.Active && bot.CurrentRegion == porter.CurrentRegion &&
        bot.IsWithinRadius(porter, BoardingRadius) && !bot.InCombat && !bot.IsAttacking &&
        (bot.Brain as BotBrain)?.HasAggro != true && !GameRelic.IsPlayerCarryingRelic(bot) &&
        CanBoardForObjective(bot.Realm, AutonomousObjectiveAssignments.KindFor(bot), request?.Passage) &&
        PassageMatchesSiege(bot,request?.Passage) &&
        request?.Porter == porter && request.ForceId == (bot.TempProperties.GetProperty<string>("RvrEventForce") ?? $"rvr-{bot.DatabaseID}") &&
        Ticket(bot,request.Passage) != null;

    public static bool CanBoardForObjective(eRealm realm, eAutonomousObjectiveKind objective, Passage passage)
    {
        if (passage == null) return false;
        Passage canonical = Destination(realm, passage.Region);
        if (canonical == null || canonical.Medallion != passage.Medallion) return false;
        return objective == eAutonomousObjectiveKind.RvR || passage.Medallion == "home_necklace";
    }

    public static void Depart(OFTeleporter porter)
    {
        var batch = Batches.GetOrCreateValue(porter);
        lock (batch)
        {
        batch.OpenUntil = GameLoop.GameLoopTime + BoardingWindowMilliseconds;
        if (batch.Waiting.OrdinaryCount == 0)
            foreach (var passenger in porter.GetNPCsInRadius(BoardingRadius).OfType<GameBot>()) batch.Waiting.Enqueue(passenger);
        if (batch.Active) return;
        // The ceremony authorizes this batch; spread actual transfers over
        // bounded slices rather than remove/add hundreds of actors at once.
        batch.Handled.Clear();
        batch.Active = true;
        batch.Timer = new ECSGameTimer(porter) { Callback = _ => ProcessBatch(porter, batch) };
        batch.Timer.Start(1);
        }
    }

    private static int ProcessBatch(OFTeleporter porter, BoardingBatch batch)
    {
        lock (batch) return ProcessBatchLocked(porter, batch);
    }

    private static int ProcessBatchLocked(OFTeleporter porter, BoardingBatch batch)
    {
        int processed = 0;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
        if (porter.ObjectState != GameObject.eObjectState.Active) { batch.Waiting.Clear(); batch.OpenUntil = 0; }
        else if (batch.Waiting.Count == 0 && GameLoop.GameLoopTime < batch.OpenUntil)
        {
            batch.Handled.Clear();
            foreach (var passenger in porter.GetNPCsInRadius(BoardingRadius).OfType<GameBot>()) batch.Waiting.Enqueue(passenger);
        }
        int examined = 0;
        while (examined < 64 && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < SliceMilliseconds &&
            batch.Waiting.TryDequeue(out var bot, out bool priority))
        {
            examined++;
            if (batch.Handled.Contains(bot)) continue;
            var request = bot.TempProperties.GetProperty<Request>(RequestKey);
            if (priority && !HasDefenderPriority(bot, request?.Passage)) continue;
            if (!Ready(bot,porter,request)) continue;
            var group = BoardingParty(bot, request.Passage);
            if (group.Any(member => member.CurrentRegionID != request.Passage.Region &&
                (member.TempProperties.GetProperty<Request>(RequestKey) is not Request memberRequest ||
                memberRequest.Passage.Region != request.Passage.Region || !Ready(member,porter,memberRequest)))) continue;
            int departed=0;
            for (int index = 0; index < group.Length; index++)
            {
                // A warband is checked together but even its transfers can be
                // split across ticks; one costly transfer cannot trigger seven more.
                if (SliceFull(processed, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds))
                {
                    foreach (var pending in group.Skip(index))
                        if(priority)batch.Waiting.EnqueuePriority(pending);else batch.Waiting.Enqueue(pending);
                    break;
                }
                var member = group[index];
                batch.Handled.Add(member);
                if(member.CurrentRegionID==request.Passage.Region) continue;
                processed++;
                var ticket = Ticket(member,request.Passage);
                member.StopMovingOnPath(); member.StopMoving();
                if (!member.MoveTo(request.Passage.Location)) continue;
                departed++;
                member.Inventory.RemoveItem(ticket);
                member.TempProperties.RemoveProperty(RequestKey);
                member.ForcePathReplot();
                AutonomousBotEconomy.MarkInventoryChanged(member);
                AutonomousStuckWatchdog.MarkProgress(member,eAutonomousProgressKind.Movement);
                AutonomousBotStatusPersistence.Queue(member,true);
            }
            var log=DOL.Logging.LoggerManager.Create(typeof(AutonomousFrontierTransport));
            if(departed>0 && log.IsInfoEnabled) log.Info($"RVR_FRONTIER_DEPARTURE force={request.ForceId} count={departed} porter=\"{porter.Name}\" destination=\"{request.Passage.Location.Name}\" region={request.Passage.Region}");
            if (SliceFull(processed, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds)) break;
        }
        }
        catch (Exception error)
        {
            DOL.Logging.LoggerManager.Create(typeof(AutonomousFrontierTransport)).Error("Frontier boarding slice failed; unprocessed passengers retained", error);
        }
        if (batch.Waiting.Count > 0) return SliceIntervalMilliseconds;
        if (GameLoop.GameLoopTime < batch.OpenUntil) return 2000;
        batch.Handled.Clear();
        lock (batch) { batch.Active = false; batch.Timer = null; }
        return 0;
    }

    public static bool WakeBoardingPorter(GameBot bot, Request request)
    {
        if (request?.Porter == null || !Ready(bot, request.Porter, request)) return false;
        if (HasDefenderPriority(bot, request.Passage))
        {
            var batch = Batches.GetOrCreateValue(request.Porter);
            lock (batch)
            {
                bool wasEmpty=batch.Waiting.Count==0;
                batch.Waiting.EnqueuePriority(bot);
                if (!batch.Active)
                {
                    batch.Handled.Clear();
                    batch.Active = true;
                    batch.Timer = new ECSGameTimer(request.Porter) { Callback = _ => ProcessBatch(request.Porter, batch) };
                    batch.Timer.Start(1);
                }
                else if(wasEmpty) batch.Timer.Start(1); // wake a two-second ordinary boarding scan
            }
            return true;
        }
        // Friendly service NPCs are not eligible for the hostile-mob wake scan.
        // Wake the actual pad only; retain its native brain, spell and schedule.
        request.Porter.OnAutonomousBotNearby();
        return true;
    }
}

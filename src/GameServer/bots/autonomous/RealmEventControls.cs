using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DOL.Events;

namespace DOL.GS
{
    /// <summary>Small local mailbox. File I/O stays off AI turns; world changes run on the coordinator.</summary>
    public static class RealmEventControls
    {
        public sealed record Request(string Id, string Action, string TargetId, int Realm, DateTime CreatedUtc, bool Confirmed);
        public sealed record Result(string Id, bool Success, string Message, DateTime UpdatedUtc);
        private static readonly ConcurrentQueue<Request> Pending = new();
        private static Timer _timer;
        private static int _polling;
        private static string _lastRead = "";
        private static Result _result;
        private static string _publishedResult = "";
        public static Result LastResult => Volatile.Read(ref _result);
        private static string RequestPath => Path.Combine(AppContext.BaseDirectory, "realm-events.request.json");
        private static string ResultPath => Path.Combine(AppContext.BaseDirectory, "realm-events.result.json");

        [GameServerStartedEvent]
        public static void Start(DOLEvent e, object sender, EventArgs args)
        {
            _timer?.Dispose();
            while (Pending.TryDequeue(out _)) { }
            // Never replay a command left by an earlier server session.
            _lastRead = "";
            Volatile.Write(ref _result, null);
            _publishedResult = "";
            try
            {
                if (File.Exists(RequestPath) && new FileInfo(RequestPath).Length <= 4096)
                    _lastRead = JsonSerializer.Deserialize<Request>(File.ReadAllText(RequestPath))?.Id ?? "";
            }
            catch (Exception) { }
            _timer = new Timer(_ => Poll(), null, 1000, 1000);
        }

        [GameServerStoppedEvent]
        public static void Stop(DOLEvent e, object sender, EventArgs args)
        {
            _timer?.Dispose();
            _timer = null;
        }

        private static void Poll()
        {
            if (Interlocked.Exchange(ref _polling, 1) != 0) return;
            try
            {
                Result result = LastResult;
                if (result != null && result.Id != _publishedResult)
                {
                    File.WriteAllText(ResultPath + ".tmp", JsonSerializer.Serialize(result));
                    File.Move(ResultPath + ".tmp", ResultPath, true);
                    _publishedResult = result.Id;
                }
                if (!File.Exists(RequestPath) || new FileInfo(RequestPath).Length > 4096) return;
                Request request = JsonSerializer.Deserialize<Request>(File.ReadAllText(RequestPath));
                if (request == null || request.Id == _lastRead || !Guid.TryParse(request.Id, out _)) return;
                _lastRead = request.Id;
                if (request.CreatedUtc > DateTime.UtcNow.AddSeconds(10) || request.CreatedUtc < DateTime.UtcNow.AddMinutes(-2))
                { Complete(request, false, "Expired request. Refresh and try again."); return; }
                if (Pending.Count >= 8) { Complete(request, false, "Event controls are busy; try again shortly."); return; }
                Pending.Enqueue(request);
            }
            catch (IOException) { /* Atomic replacement or shutdown; retry next tick. */ }
            catch (JsonException) { /* Never execute malformed input. */ }
            catch (Exception exception)
            { DOL.Logging.LoggerManager.Create(typeof(RealmEventControls)).Warn("Realm event mailbox failed", exception); }
            finally { Volatile.Write(ref _polling, 0); }
        }

        public static void Pulse()
        {
            if (!Pending.TryDequeue(out Request request)) return;
            try
            {
                if (request.CreatedUtc < DateTime.UtcNow.AddMinutes(-2))
                { Complete(request, false, "The queued request expired before execution; please try again."); return; }
                if (AutonomousRealmRaid.Definitions.Any(d => d.Id == request.TargetId))
                {
                    if (request.Action == "reset-cooldown" && request.Confirmed)
                    {
                        bool reset = AutonomousRealmRaid.ResetCooldown(request.TargetId);
                        Complete(request, reset, reset ? "Event cooldown cleared. The boss must still actually be alive." : "An active expedition cannot be reset.");
                    }
                    else if (request.Action == "start")
                    {
                        bool raidAccepted = AutonomousRealmRaid.Start(request.TargetId, (eRealm)request.Realm, out string raidReason, forced: true);
                        Complete(request, raidAccepted, raidReason);
                    }
                    else Complete(request, false, "Unknown action or missing confirmation.");
                    return;
                }
                var keep = new ushort[] { 1, 100, 200 }
                    .SelectMany(region => GameServer.KeepManager.GetKeepsOfRegion(region))
                    .FirstOrDefault(k => $"rvr-keep-{k.KeepID}" == request.TargetId && AutonomousRvrKeepPolicy.IsSiegeObjective(k));
                if (keep == null) { Complete(request, false, "This is not an available keep event."); return; }
                if (request.Action == "reset-cooldown" && request.Confirmed)
                {
                    bool reset = AutonomousRvrEventLayer.ResetCooldown(request.TargetId);
                    Complete(request, reset, reset ? "Event cooldown cleared. No monsters were respawned." : "An active event cannot have its cooldown reset.");
                    return;
                }
                if (request.Action != "start") { Complete(request, false, "Unknown action or missing confirmation."); return; }
                var target = new AutonomousRvrEventLayer.LiveObjective(request.TargetId, keep.Name,
                    keep.IsRelic ? AutonomousRvrEventLayer.Intent.AssaultRelicKeep : AutonomousRvrEventLayer.Intent.AssaultKeep,
                    keep.Realm, keep.Region, keep.X, keep.Y, keep.Z, keep.IsRelic, 0, 0,
                    keep.Guards.Values.Count(g => g.IsAlive), keep.Doors.Values.Count(d => d.IsAlive && d.State == eDoorState.Closed));
                bool accepted = AutonomousRvrEventLayer.ForceStart(target, (eRealm)request.Realm, GameLoop.GameLoopTime, out string reason);
                Complete(request, accepted, reason);
            }
            catch (Exception exception)
            {
                Complete(request, false, "The event request failed safely; see server log.");
                DOL.Logging.LoggerManager.Create(typeof(RealmEventControls)).Error("Realm event control failed", exception);
            }
        }

        private static void Complete(Request request, bool success, string message)
        {
            Volatile.Write(ref _result, new Result(request.Id, success, message, DateTime.UtcNow));
            if (success) AutonomousRvrDashboard.RequestPublish();
        }
    }
}

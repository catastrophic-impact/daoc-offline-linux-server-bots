using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DOL.Database;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS;

/// <summary>
/// Publishes a compact, read-only view of the live autonomous roster for the
/// local launcher. This deliberately does not touch SQLite: durable character,
/// inventory, coin, training and Realm Exchange persistence remains owned by
/// the normal save pipeline and cannot be delayed by a launcher refresh.
/// </summary>
public static class AutonomousBotDashboard
{
    public const int RequestPollIntervalMilliseconds = 500;
    public sealed record BotStatus(long BotId, int Level, string ZoneName, string Activity,
        string CurrentGoal, string TargetName, string TravelDestination, string ObjectiveProgress,
        bool IsAlive, string ItineraryJson, string ObjectiveKind, string ObjectiveAssignmentId,
        string ObjectiveAssignedUtc, string ObjectivePhase, string ObjectiveExpiresUtc);
    public sealed record Snapshot(DateTime UpdatedUtc, bool Running, string RequestId, BotStatus[] Bots);

    private static readonly Logger Log = LoggerManager.Create(typeof(AutonomousBotDashboard));
    private static Timer _timer;
    private static int _writing;
    private static string _lastRequestId = string.Empty;

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "bot-world.json");
    public static string RequestPath => Path.Combine(AppContext.BaseDirectory, "bot-world.request");

    [GameServerStartedEvent]
    public static void Start(DOLEvent e, object sender, EventArgs args)
    {
        _timer?.Dispose();
        _lastRequestId = string.Empty;
        _timer = new Timer(_ => PollForRequest(), null, 0, RequestPollIntervalMilliseconds);
    }

    [GameServerStoppedEvent]
    public static void Stop(DOLEvent e, object sender, EventArgs args)
    {
        _timer?.Dispose();
        _timer = null;
        Publish(false, string.Empty);
    }

    internal static BotStatus Capture(GameBot bot)
    {
        OfflineWorldBotRecord record = bot.PersistentRecord;
        return new BotStatus(
            bot.DatabaseID,
            bot.Level,
            bot.CurrentZone?.Description ?? record?.ZoneName ?? "—",
            record?.Activity ?? string.Empty,
            record?.CurrentGoal ?? string.Empty,
            record?.TargetName ?? string.Empty,
            record?.TravelDestination ?? string.Empty,
            record?.ObjectiveProgress ?? string.Empty,
            bot.IsAlive,
            record?.ItineraryJson ?? string.Empty,
            record?.ObjectiveKind ?? string.Empty,
            record?.ObjectiveAssignmentId ?? string.Empty,
            record?.ObjectiveAssignedUtc ?? string.Empty,
            record?.ObjectivePhase ?? string.Empty,
            record?.ObjectiveExpiresUtc ?? string.Empty);
    }

    private static void PollForRequest()
    {
        try
        {
            if (!File.Exists(RequestPath))
                return;
            string requestId;
            using (var stream = new FileStream(RequestPath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                requestId = reader.ReadToEnd().Trim();
            if (requestId.Length == 0 || requestId.Length > 128 ||
                string.Equals(requestId, _lastRequestId, StringComparison.Ordinal))
                return;
            if (Publish(true, requestId))
                _lastRequestId = requestId;
        }
        catch (IOException)
        {
            // The launcher's atomic request replacement can overlap this tiny
            // read window. The next poll retries without logging noise.
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.Warn("Live bot dashboard request could not be read", exception);
        }
    }

    private static bool Publish(bool running, string requestId)
    {
        if (Interlocked.Exchange(ref _writing, 1) != 0)
            return false;

        try
        {
            BotStatus[] bots = running
                ? AutonomousBotRegistry.Snapshot()
                    .Where(bot => bot?.PersistentRecord != null && bot.DatabaseID > 0)
                    .Select(Capture)
                    .ToArray()
                : [];
            var snapshot = new Snapshot(DateTime.UtcNow, running, requestId, bots);
            string temporaryPath = FilePath + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write,
                       FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, snapshot);
            }
            File.Move(temporaryPath, FilePath, true);
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn("Live bot dashboard snapshot failed", exception);
            return false;
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }
    }
}

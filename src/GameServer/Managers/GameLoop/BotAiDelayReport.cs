#nullable enable
using System;
using System.IO;
using System.Text.Json;

namespace DOL.GS;

// Shared with the launcher (linked file). Once a minute the server writes how long the
// NpcService stage (bot and monster AI) took per game tick; the launcher's BOT AI DELAY
// card reads it. The old card read a database column nothing ever wrote, so it always
// showed 0.0 ms.
public sealed record BotAiDelayReport(DateTime UpdatedUtc, double AverageMs, double PeakMs, long Samples)
{
    public const string FileName = "bot-ai-delay.json";
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(3);

    public static void Write(string path, BotAiDelayReport report)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report));
        File.Move(temporary, path, true);
    }

    // Null when missing, unreadable or older than FreshFor (a stopped or stalled server).
    public static BotAiDelayReport? Read(string path, DateTime nowUtc)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var report = JsonSerializer.Deserialize<BotAiDelayReport>(File.ReadAllText(path));
            return report != null && nowUtc - report.UpdatedUtc <= FreshFor ? report : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

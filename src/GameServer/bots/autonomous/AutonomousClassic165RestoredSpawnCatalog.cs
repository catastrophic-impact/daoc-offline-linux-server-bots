using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DOL.Logging;

namespace DOL.GS;

/// <summary>
/// Immutable IDs of source-corroborated archive rows restored by the Classic
/// 1.65 setup migration.  The IDs never create NPCs; they only let the normal
/// live-world camp planner recognize the corresponding real database spawns.
/// </summary>
public static class AutonomousClassic165RestoredSpawnCatalog
{
    private static readonly string[] ResourceSuffixes =
    {
        "classic165_restored_spawn_ids.txt",
        "classic165_period_restored_spawn_ids.txt",
    };
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly Lazy<HashSet<string>> LoadedIds = new(Load, true);

    public static int Count => LoadedIds.Value.Count;

    public static bool Contains(string mobId) =>
        !string.IsNullOrWhiteSpace(mobId) && LoadedIds.Value.Contains(mobId);

    private static HashSet<string> Load()
    {
        try
        {
            Assembly assembly = typeof(AutonomousClassic165RestoredSpawnCatalog).Assembly;
            string[] resources = assembly.GetManifestResourceNames();
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string suffix in ResourceSuffixes)
            {
                string resource = resources.FirstOrDefault(name =>
                    name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (resource == null)
                {
                    TryLogError($"Classic 1.65 restored-spawn resource '{suffix}' is missing.");
                    continue;
                }

                using Stream stream = assembly.GetManifestResourceStream(resource);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    line = line.Trim();
                    if (Guid.TryParse(line, out _) || long.TryParse(line, out long numericId) && numericId > 0)
                        result.Add(line);
                }
            }
            TryLogInfo($"Loaded {result.Count:N0} source-checked Classic 1.65 spawn IDs.");
            return result;
        }
        catch (Exception exception)
        {
            TryLogError("Unable to load the Classic 1.65 restored-spawn IDs.", exception);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void TryLogInfo(string message)
    {
        try { Log.Info(message); }
        catch { }
    }

    private static void TryLogError(string message, Exception exception = null)
    {
        try
        {
            if (exception == null)
                Log.Error(message);
            else
                Log.Error(message, exception);
        }
        catch { }
    }
}

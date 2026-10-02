using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DOL.Logging;

namespace DOL.GS;

/// <summary>
/// Immutable, build-time audited Classic + Shrouded Isles PvE locations from
/// CapnBry.  The live controller still requires a real nearby server NPC before
/// exposing an entry as a goal; these records never create or simulate mobs.
/// </summary>
public static class AutonomousCapnBryGoalCatalog
{
    private const string ResourceSuffix = "capnbry_classic_si_goals.json";
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly Lazy<CatalogData> LoadedCatalog = new(Load, true);

    public sealed class Entry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("zone")]
        public string Zone { get; set; } = string.Empty;

        [JsonPropertyName("zone_id")]
        public ushort ZoneId { get; set; }

        [JsonPropertyName("region_id")]
        public ushort RegionId { get; set; }

        [JsonPropertyName("local_x")]
        public int LocalX { get; set; }

        [JsonPropertyName("local_y")]
        public int LocalY { get; set; }

        [JsonPropertyName("z")]
        public int Z { get; set; }

        [JsonPropertyName("levels")]
        public int[] Levels { get; set; } = [];

        [JsonPropertyName("sightings")]
        public int Sightings { get; set; }
    }

    private sealed class CatalogDocument
    {
        [JsonPropertyName("covered_zone_ids")]
        public ushort[] CoveredZoneIds { get; set; } = [];

        [JsonPropertyName("source_empty_supported_zone_ids")]
        public ushort[] SourceEmptySupportedZoneIds { get; set; } = [];

        [JsonPropertyName("goals")]
        public Entry[] Goals { get; set; } = [];
    }

    private sealed record CatalogData(Entry[] Entries, HashSet<ushort> CoveredZoneIds, HashSet<ushort> SourceEmptySupportedZoneIds);

    public static IReadOnlyList<Entry> Entries => LoadedCatalog.Value.Entries;
    public static IReadOnlySet<ushort> CoveredZoneIds => LoadedCatalog.Value.CoveredZoneIds;
    public static IReadOnlySet<ushort> SourceEmptySupportedZoneIds => LoadedCatalog.Value.SourceEmptySupportedZoneIds;

    // Region.Expansion exposes the raw database value + 1. Classic is 1,
    // Shrouded Isles is 2, and ToA begins at 3.
    public static bool IsClassicOrShroudedIslesExpansion(int expansion) => expansion is 1 or 2;

    private static CatalogData Load()
    {
        try
        {
            Assembly assembly = typeof(AutonomousCapnBryGoalCatalog).Assembly;
            string resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (resource == null)
            {
                TryLogError($"CapnBry goal catalog resource '{ResourceSuffix}' is missing; no authoritative PvE goals will be exposed.");
                return new([], [], []);
            }

            using Stream stream = assembly.GetManifestResourceStream(resource);
            CatalogDocument document = JsonSerializer.Deserialize<CatalogDocument>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Entry[] valid = document?.Goals?
                .Where(entry => entry != null)
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && entry.Name == entry.Name.ToLowerInvariant())
                .Where(entry => entry.Levels is { Length: > 0 } && entry.Levels.All(level => level is >= 1 and <= 50))
                .ToArray() ?? [];
            TryLogInfo($"Loaded {valid.Length:N0} CapnBry-authoritative Classic/SI PvE goal locations.");
            return new(valid,
                new HashSet<ushort>(document?.CoveredZoneIds ?? []),
                new HashSet<ushort>(document?.SourceEmptySupportedZoneIds ?? []));
        }
        catch (Exception exception)
        {
            TryLogError("Unable to load the embedded CapnBry PvE goal catalog; no unverified local fallback will be used.", exception);
            return new([], [], []);
        }
    }

    // Unit tests intentionally do not initialize the asynchronous server logger.
    // Catalog loading must remain deterministic in that environment too.
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DOL.Database;

namespace DOL.GS;

/// <summary>
/// Ordinary DF grinds and raid/event encounters have different owners. The
/// 85 raid/event rows are frozen by exact Mob_ID and original DB attributes,
/// never inferred from a name, level, or ClassType during goal selection.
/// A changed baseline closes the ordinary-goal certificate for re-audit.
/// </summary>
public static class AutonomousDarknessFallsGoalScope
{
    public const int TotalCombatRows = 2460;
    public const int RaidEventRows = 85;
    public const int OrdinaryCombatRows = 2375;
    private const string TemplatedHighLordOroId = "504d573f-deab-4cb2-9dbc-d9d053d7af2f";
    private const string ResourceSuffix = "darkness_falls_raid_event_mobs.json";
    private static readonly Lazy<Manifest> Data = new(Load, true);
    private static readonly Lazy<HashSet<string>> RaidIds = new(() =>
        Data.Value == null ? null : Data.Value.Rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal), true);

    public sealed class RaidEventSpawn
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Level { get; set; }
        // High Lord Oro's 65-70 NPC template randomly chooses a level and
        // saves that value back to its Mob row at every server start.
        // Only this frozen ID may use the exact template range below.
        public int[] AllowedLevels { get; set; }
        public string ClassType { get; set; }
        public int[] Spawn { get; set; }
        public string[] Reasons { get; set; }
    }

    private sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public int BaselineCombatRows { get; set; }
        public int RaidEventRows { get; set; }
        public int OrdinaryCombatRows { get; set; }
        public RaidEventSpawn[] Rows { get; set; }
    }

    public static bool IsRaidEventId(string id) =>
        !string.IsNullOrWhiteSpace(id) && RaidIds.Value?.Contains(id) == true;

    public static bool IsOrdinaryCatalogId(string id) =>
        !string.IsNullOrWhiteSpace(id) && RaidIds.Value != null && !RaidIds.Value.Contains(id);

    // Return copies so test fixtures cannot mutate the process-wide manifest.
    public static RaidEventSpawn[] SnapshotRaidEventRows() => Data.Value?.Rows.Select(row => new RaidEventSpawn
    {
        Id = row.Id, Name = row.Name, Level = row.Level, ClassType = row.ClassType,
        AllowedLevels = row.AllowedLevels == null ? null : (int[])row.AllowedLevels.Clone(),
        Spawn = (int[])row.Spawn.Clone(), Reasons = (string[])row.Reasons.Clone()
    }).ToArray() ?? [];

    public static bool TryGetOrdinaryCombatRows(IEnumerable<DbMob> databaseRows, out DbMob[] ordinary)
    {
        ordinary = [];
        if (databaseRows == null || Data.Value == null) return false;
        DbMob[] combat = databaseRows.Where(mob => mob != null &&
            mob.Region == AutonomousDarknessFallsPolicy.RegionId && mob.Realm == 0 && mob.Level > 0).ToArray();
        if (combat.Length != TotalCombatRows ||
            combat.Any(mob => string.IsNullOrWhiteSpace(mob.ObjectId)) ||
            combat.Select(mob => mob.ObjectId).Distinct(StringComparer.Ordinal).Count() != TotalCombatRows ||
            !HasExactRaidEventRows(combat, Data.Value.Rows)) return false;
        ordinary = combat.Where(mob => !RaidIds.Value.Contains(mob.ObjectId)).ToArray();
        return ordinary.Length == OrdinaryCombatRows;
    }

    public static bool HasExactRaidEventRows(IEnumerable<DbMob> combatRows, IEnumerable<RaidEventSpawn> expectedRows)
    {
        if (combatRows == null || expectedRows == null) return false;
        RaidEventSpawn[] expected = expectedRows.ToArray();
        if (!HasValidManifestRows(expected)) return false;
        DbMob[] combat = combatRows.Where(mob => mob != null).ToArray();
        if (combat.Any(mob => string.IsNullOrWhiteSpace(mob.ObjectId))) return false;
        var live = combat.GroupBy(mob => mob.ObjectId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return expected.All(row => live.TryGetValue(row.Id, out DbMob[] matches) && matches.Length == 1 &&
            string.Equals(matches[0].Name, row.Name, StringComparison.Ordinal) &&
            string.Equals(matches[0].ClassType, row.ClassType, StringComparison.Ordinal) &&
            (matches[0].Level == row.Level || row.AllowedLevels?.Contains(matches[0].Level) == true) &&
            matches[0].Realm == 0 &&
            matches[0].Region == AutonomousDarknessFallsPolicy.RegionId &&
            matches[0].X == row.Spawn[0] && matches[0].Y == row.Spawn[1] && matches[0].Z == row.Spawn[2]);
    }

    private static bool HasValidManifestRows(RaidEventSpawn[] rows)
    {
        if (rows?.Length != RaidEventRows ||
            rows.Select(row => row?.Id).Distinct(StringComparer.Ordinal).Count() != RaidEventRows ||
            rows.Any(row => row == null || string.IsNullOrWhiteSpace(row.Id) ||
                string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.ClassType) ||
                row.Level <= 0 || row.Spawn?.Length != 3 || row.Reasons?.Length is not > 0 ||
                (row.Id == TemplatedHighLordOroId
                    ? row.Name != "High Lord Oro" || row.ClassType != "DOL.GS.HighLordOro" ||
                        row.AllowedLevels == null ||
                        !row.AllowedLevels.SequenceEqual(new[] { 65, 66, 67, 68, 69, 70 }) ||
                        !row.AllowedLevels.Contains(row.Level)
                    : row.AllowedLevels is { Length: > 0 }) ||
                row.Reasons.Distinct(StringComparer.Ordinal).Count() != row.Reasons.Length)) return false;

        Dictionary<string, int> counts = rows.SelectMany(row => row.Reasons)
            .GroupBy(reason => reason, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return counts.Count == 4 &&
            counts.GetValueOrDefault("scripted") == 12 &&
            counts.GetValueOrDefault("legion_island_soul") == 26 &&
            counts.GetValueOrDefault("chthonic_wing") == 6 &&
            counts.GetValueOrDefault("epic_level_70_plus") == 43 &&
            rows.Count(row => row.Reasons.Length > 1) == 2;
    }

    private static Manifest Load()
    {
        try
        {
            Assembly assembly = typeof(AutonomousDarknessFallsGoalScope).Assembly;
            string resource = assembly.GetManifestResourceNames().SingleOrDefault(name =>
                name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
            if (resource == null) return null;
            using Stream stream = assembly.GetManifestResourceStream(resource);
            Manifest data = JsonSerializer.Deserialize<Manifest>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return data is { SchemaVersion: 1, BaselineCombatRows: TotalCombatRows,
                RaidEventRows: RaidEventRows, OrdinaryCombatRows: OrdinaryCombatRows } &&
                HasValidManifestRows(data.Rows) ? data : null;
        }
        catch
        {
            // A missing/invalid resource never silently broadens ordinary DF
            // goals to scripted bosses or the Legion encounter island.
            return null;
        }
    }
}

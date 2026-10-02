using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DOL.GS;

/// <summary>
/// Low-cost, published snapshots used to keep dungeon preference soft. The
/// actor registry and camp builder already scan their respective inputs, so a
/// planning decision performs dictionary lookups instead of another roster or
/// world-object scan.
/// </summary>
public static class AutonomousDungeonPopulationPolicy
{
    private static Dictionary<ushort, int> _population = new();
    private static Dictionary<ushort, int> _capacity = new();
    private static Dictionary<string, ushort> _campRegions = new(StringComparer.OrdinalIgnoreCase);

    public static int Population(ushort regionId)
    {
        Dictionary<ushort, int> snapshot = Volatile.Read(ref _population);
        return snapshot.TryGetValue(regionId, out int value) ? value : 0;
    }

    public static int Capacity(ushort regionId, int fallbackLiveMobs = 0)
    {
        Dictionary<ushort, int> snapshot = Volatile.Read(ref _capacity);
        if (snapshot.TryGetValue(regionId, out int value))
            return value;
        return SoftCapacity(fallbackLiveMobs);
    }

    public static void PublishPopulation(Dictionary<ushort, int> population) =>
        Volatile.Write(ref _population, population ?? new());

    public static void PublishCatalog(IEnumerable<(string Id, ushort RegionId, int LiveMobCount)> camps)
    {
        (string Id, ushort RegionId, int LiveMobCount)[] snapshot = camps?.ToArray() ?? [];
        Dictionary<ushort, int> capacities = snapshot
            .GroupBy(camp => camp.RegionId)
            .ToDictionary(group => group.Key, group => SoftCapacity(group.Sum(camp => Math.Max(0, camp.LiveMobCount))))
            ?? new();
        Dictionary<string, ushort> campRegions = snapshot
            .Where(camp => !string.IsNullOrWhiteSpace(camp.Id))
            .GroupBy(camp => camp.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().RegionId, StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _capacity, capacities);
        Volatile.Write(ref _campRegions, campRegions);
    }

    public static int SoftCapacity(int liveMobs) =>
        Math.Clamp((int)Math.Ceiling(Math.Max(0, liveMobs) * 0.50), 12, 96);

    public static int AvailabilityPermille(int population, int capacity)
    {
        capacity = Math.Max(1, capacity);
        population = Math.Max(0, population);
        int half = Math.Max(1, capacity / 2);
        if (population <= half)
            return 1_000;
        if (population < capacity)
            return 1_000 - (population - half) * 750 / Math.Max(1, capacity - half);
        return Math.Clamp(250 * capacity / Math.Max(1, population), 50, 250);
    }

    public static bool TryGetAssignedRegion(string campId, out ushort regionId)
    {
        regionId = 0;
        if (string.IsNullOrWhiteSpace(campId))
            return false;
        return Volatile.Read(ref _campRegions).TryGetValue(campId, out regionId);
    }
}

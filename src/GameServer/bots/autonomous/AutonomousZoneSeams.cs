using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DOL.GS;

/// <summary>
/// The zone-border crossings that join real, reachable ground, generated from the navmeshes by
/// tools/navmesh_splice/seamgraph.py into navmesh/seams.json (regenerate after rebuilding meshes).
/// With every client wall in the meshes, many borders have walkable floor on both sides only on
/// walled-off mountain shelves; the zone itinerary tries these crossings first so it does not route a bot
/// onto such a shelf or plans a chain of zones that does not connect (Hibernia's home zones to
/// Breifine, 2026-10-06). Without the file, or for a region it does not list, nothing changes.
/// </summary>
public static class AutonomousZoneSeams
{
    /// <summary>
    /// Points along one border: position along the border, the floor height on each side and the
    /// walkable area id on each side (connected component of that zone's mesh; -1 in older files).
    /// </summary>
    public readonly record struct Crossing(float Along, float InsideZ, float OutsideZ, int InsideArea = -1, int OutsideArea = -1);

    private static readonly Lazy<Dictionary<(ushort Region, ushort From, ushort To), Crossing[]>> Data = new(Load);
    private static readonly HashSet<ushort> Regions = new();
    // Each zone's main walkable area (largest connected component), when the file lists it.
    private static readonly Dictionary<(ushort Region, ushort Zone), int> MainAreas = new();

    /// <summary>The zone's main ground area id (seam file "main"), or null when unknown.</summary>
    public static int? MainArea(ushort region, ushort zone)
    {
        _ = Data.Value;
        lock (MainAreas) return MainAreas.TryGetValue((region, zone), out int area) ? area : null;
    }

    // The server's folder; tools running elsewhere (UT_ZoneStepProbe) use the current directory.
    public static string FilePath => File.Exists(Path.Combine(AppContext.BaseDirectory, "navmesh", "seams.json"))
        ? Path.Combine(AppContext.BaseDirectory, "navmesh", "seams.json")
        : Path.Combine(Environment.CurrentDirectory, "navmesh", "seams.json");

    /// <summary>True when the file lists this region (its listed crossings and borders are then tried first).</summary>
    public static bool Covers(ushort region)
    {
        _ = Data.Value;
        lock (Regions) return Regions.Contains(region);
    }

    public static Crossing[] For(ushort region, ushort from, ushort to) =>
        Data.Value.TryGetValue((region, from, to), out Crossing[] crossings) ? crossings : null;

    private static Dictionary<(ushort, ushort, ushort), Crossing[]> Load()
    {
        var result = new Dictionary<(ushort, ushort, ushort), Crossing[]>();
        try
        {
            if (!File.Exists(FilePath)) return result;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (document.RootElement.TryGetProperty("main", out JsonElement main))
                foreach (JsonProperty region in main.EnumerateObject())
                    foreach (JsonProperty zone in region.Value.EnumerateObject())
                        lock (MainAreas) MainAreas[(ushort.Parse(region.Name), ushort.Parse(zone.Name))] = zone.Value.GetInt32();
            foreach (JsonProperty region in document.RootElement.GetProperty("regions").EnumerateObject())
            {
                ushort regionId = ushort.Parse(region.Name);
                lock (Regions) Regions.Add(regionId);
                foreach (JsonElement edge in region.Value.EnumerateArray())
                {
                    var points = new List<Crossing>();
                    foreach (JsonElement point in edge.GetProperty("points").EnumerateArray())
                        points.Add(new(point[0].GetSingle(), point[1].GetSingle(), point[2].GetSingle(),
                            point.GetArrayLength() > 4 ? point[3].GetInt32() : -1, point.GetArrayLength() > 4 ? point[4].GetInt32() : -1));
                    result[(regionId, edge.GetProperty("from").GetUInt16(), edge.GetProperty("to").GetUInt16())] = points.ToArray();
                }
            }
        }
        catch (Exception exception)
        {
            DOL.Logging.LoggerManager.Create(typeof(AutonomousZoneSeams)).Warn("navmesh/seams.json could not be read; zone borders are unrestricted", exception);
            result.Clear();
            lock (Regions) Regions.Clear();
        }
        return result;
    }
}

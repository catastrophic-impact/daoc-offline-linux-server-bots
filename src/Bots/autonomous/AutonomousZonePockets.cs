using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DOL.GS;

/// <summary>
/// Walled-off walkable pockets: mesh areas that join no real ground (no bind point, zone point or
/// town NPC on their network), generated with the seam file by tools/navmesh_splice/seamgraph.py
/// into navmesh/pockets.json. With every client wall in the meshes these are mostly mountain shelves
/// along zone borders (66 Hibernian raid bots saved on the Bri Leith / Lough Derg / Mount Collory
/// shelves, 2026-10-06). A bot inside one can never walk out, so it is relocated like any other
/// invalid position. Without the file nothing changes.
/// </summary>
public static class AutonomousZonePockets
{
    public sealed record Data(int Cell, Dictionary<(ushort Zone, int X, int Y), (float Low, float High, float Margin)> Cells);

    /// <summary>Height margin around a pocket's floor band unless the entry gives its own (a fifth number): Camelot's
    /// sunken patch at 7,920 lies 90 below a real street, so its entries use 20.</summary>
    public const float DefaultMargin = 96;

    private static readonly Lazy<Data> Loaded = new(Load);

    // The server's folder; tools running elsewhere use the current directory.
    public static string FilePath => File.Exists(Path.Combine(AppContext.BaseDirectory, "navmesh", "pockets.json"))
        ? Path.Combine(AppContext.BaseDirectory, "navmesh", "pockets.json")
        : Path.Combine(Environment.CurrentDirectory, "navmesh", "pockets.json");

    /// <summary>True when the floor position lies in a listed pocket (within 96 units of its floor heights).</summary>
    public static bool Contains(ushort zone, float x, float y, float floorZ) => Contains(Loaded.Value, zone, x, y, floorZ);

    public static Data Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        int cell = document.RootElement.GetProperty("cell").GetInt32();
        var cells = new Dictionary<(ushort, int, int), (float, float, float)>();
        foreach (JsonProperty zone in document.RootElement.GetProperty("zones").EnumerateObject())
        {
            ushort zoneId = ushort.Parse(zone.Name);
            foreach (JsonElement entry in zone.Value.EnumerateArray())
                cells[(zoneId, entry[0].GetInt32(), entry[1].GetInt32())] = (entry[2].GetSingle(), entry[3].GetSingle(),
                    entry.GetArrayLength() > 4 ? entry[4].GetSingle() : DefaultMargin);
        }
        return new Data(cell, cells);
    }

    public static bool Contains(Data data, ushort zone, float x, float y, float floorZ)
    {
        var key = (zone, (int)Math.Floor(x / data.Cell), (int)Math.Floor(y / data.Cell));
        return data.Cells.TryGetValue(key, out var band) && floorZ >= band.Low - band.Margin && floorZ <= band.High + band.Margin;
    }

    private static Data Load()
    {
        try
        {
            if (File.Exists(FilePath)) return Parse(File.ReadAllText(FilePath));
        }
        catch (Exception exception)
        {
            DOL.Logging.LoggerManager.Create(typeof(AutonomousZonePockets)).Warn("navmesh/pockets.json could not be read; pockets are not checked", exception);
        }
        return new Data(512, new());
    }
}

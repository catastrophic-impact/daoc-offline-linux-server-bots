using System.Collections.Generic;
using System.IO;
using System.Linq;
using CEM.Client;
using CEM.Datastructures;
using OpenTK;

namespace CEM.World {
  /// <summary>
  /// Region
  /// </summary>
  internal class Region : Dictionary<ushort, Zone2> {
    private ZoneConnectionGraph _zonegraph;

    public Region(ushort id, string addon) {
      ID = id;
      Addon = addon;
    }

    /// <summary>
    /// Loads all regions (without zone data attached)
    /// </summary>
    public static IEnumerable<Region> LoadRegions()
    {
      IniFile ini;
      using (Stream fs = ClientData.ZonesDat)
        ini = new IniFile(fs);

      // Load Regions
      return from entry in ini.Topics
             where entry.Name.StartsWith("region")
             select new Region(ushort.Parse(entry.Name.Substring("region".Length)), (entry.Items.ContainsKey("phousing") ? "phousing" : (entry.Items.ContainsKey("frontiers") ? "frontiers" : (entry.Items.ContainsKey("tutorial") ? "tutorial" : ""))));
    }

    /// <summary>
    /// Region ID
    /// </summary>
    public ushort ID { get; private set; }


    /// <summary>
    /// "" for normal zones, "frontiers" for NF, "phousing" for housing, "tutorial" for tutorial
    /// </summary>
    public string Addon { get; private set; }

    /// <summary>
    /// Retrieves the zone with the specified x and y
    /// </summary>
    public Zone2 this[int x, int y] {
      get { return (from z in Values where z.Contains(new Vector3(x, y, 0)) select z).FirstOrDefault(); }
    }

    /// <summary>
    /// Retrieves the zone with the specified x and y
    /// </summary>
    /// <param name="pos"></param>
    /// <returns></returns>
    public Zone2 this[Vector3 pos] {
      get { return this[(int) pos.X, (int) pos.Y]; }
    }

    /// <summary>
    /// Returns a zone connection graph
    /// </summary>
    public ZoneConnectionGraph ZoneGraph { get { return _zonegraph ?? (_zonegraph = new ZoneConnectionGraph(this)); } }

    /// <summary>
    /// Returns the zone in which the specified object is located
    /// </summary>
    /// <param name="pt"></param>
    /// <returns></returns>
    public Zone2 this[IPoint3D pt] {
      get { return this[pt.Position]; }
    }

    public override string ToString() {
      return string.Format("R{0:D3}", ID);
    }

    /// <summary>
    /// Finds the nearest position which is located in a zone
    /// </summary>
    /// <param name="position"></param>
    /// <returns></returns>
    public Vector3 FindNearestZonePosition(Vector3 position) {
      Zone2 z = FindNearestZone(position);

      // Find nearest point
      var nearest = z.Rectangle.Borders.Select(l => l.GetClosestPoint(position.ToVector2()))
                     .OrderBy(p => p.Distance(position.ToVector2()))
                     .First();
      nearest = ((nearest*31) + z.Rectangle.Middle)/32; /* move away from the border */
      return z.GetNearestGround(nearest);
    }

    /// <summary>
    /// Finds the nearest zone for the given location
    /// </summary>
    public Zone2 FindNearestZone(Vector3 gloc) {
      return (from z in Values
              orderby z.Rectangle.Middle.Distance(gloc.ToVector2()) ascending 
              select z).FirstOrDefault();
    }
  }
}
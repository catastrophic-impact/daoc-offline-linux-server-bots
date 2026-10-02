using System.Collections.Generic;
using System.Linq;
using CEM.Datastructures;

namespace CEM.World
{
  /// <summary>
  /// Graph for zone connetions
  /// </summary>
  internal class ZoneConnectionGraph : Graph<Zone2> {
    /// <summary>
    /// Specifies an intersection of two zones
    /// </summary>
    public struct Intersection {
      /// <summary>
      /// Source Zone
      /// </summary>
      public Zone2 ZoneA { get; set; }

      /// <summary>
      /// Destination Zone
      /// </summary>
      public Zone2 ZoneB { get; set; }

      /// <summary>
      /// Line at which the intersection occures
      /// </summary>
      public Line2 Border { get; set; }

      public override string ToString() {
        return string.Format("{0} x {1} @ {2}", ZoneA, ZoneB, Border);
      }
    }

    private readonly List<Intersection> _intersections = new List<Intersection>();

    /// <summary>
    /// Creates a new RegionConnectionGraph based upon the given zonejumps
    /// </summary>
    public ZoneConnectionGraph(Region region) {
      var qry = region.Values.SelectMany(z => region.Values.Select(z2 => new {a = z, b = z2}))
                      .Where(pair => pair.a.ID < pair.b.ID)
                      .Select(pair => MakeIntersection(pair.a, pair.b))
                      .Where(inter => inter.HasValue)
                      .Select(inter => inter.Value);
      foreach (var inter in qry)
        AddIntersection(inter);
    }

    private Intersection? MakeIntersection(Zone2 a, Zone2 b) {
      var line = a.Rectangle.BorderIntersection(b.Rectangle);
      if (line == null || line.Value.IsPoint) return null;
      return new Intersection { Border = line.Value, ZoneA = a, ZoneB = b};
    }

    private void AddIntersection(Intersection intersection) {
      _intersections.Add(intersection);
      AddConnection(intersection.ZoneA, intersection.ZoneB);
      AddConnection(intersection.ZoneB, intersection.ZoneA);
    }

    /// <summary>
    /// Returns the zonejump that has to be taken to get from the specified source region to the destination
    /// </summary>
    public Intersection GetIntersection(Zone2 src, Zone2 dest) {
      return
        _intersections.FirstOrDefault(x => (x.ZoneA == src && x.ZoneB == dest) || (x.ZoneB == src && x.ZoneA == dest));
    }
  }
}

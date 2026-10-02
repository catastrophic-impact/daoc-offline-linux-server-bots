using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CEM.Datastructures
{
  /// <summary>
  /// A length-limited 2D-line
  /// </summary>
  internal struct Line2 {
    public Line2(Vector2 start, Vector2 end) {
      Start = start;
      End = end;
    }
    public Line2(float x1, float y1, float x2, float y2) {
      Start = new Vector2(x1, y1);
      End = new Vector2(x2, y2);
    }

    /// <summary>
    /// Starting point
    /// </summary>
    public Vector2 Start;

    /// <summary>
    /// End point
    /// </summary>
    public Vector2 End;

    /// <summary>
    /// Returns the direction of this vector
    /// </summary>
    public Vector2 Direction {
      get { return (End - Start).Normalize(); }
    }

    /// <summary>
    /// Returns the direction of this vector
    /// </summary>
    public Vector2 DirectionUnnormalized
    {
      get { return (End - Start); }
    }

    /// <summary>
    /// Returns the length of this line
    /// </summary>
    public float Length {
      get { return (End - Start).Length; }
    }

    /// <summary>
    /// Returns the center of this line
    /// </summary>
    public Vector2 Center
    {
      get { return (End + Start) / 2; }
    }

    /// <summary>
    /// True if this line is in fact a point
    /// </summary>
    public bool IsPoint {
      get { return Start == End; }
    }

    /// <summary>
    /// Stretches this Line by the specified factor in both directions
    /// </summary>
    public Line2 Stretch(float factor) {
      var half = (End-Start)*factor/2;
      return new Line2(Center - half, Center + half);
    }

    /// <summary>
    /// Returns the closest point on this line towards the given point
    /// </summary>
    public Vector2 GetClosestPoint(Vector2 fromPoint) {
      // Closest point is the one with a perpendicular angle
      // Factor is currently required so that we can use limitLengths=true, which we need for the THIS-line,
      // but not the line we actually want to test
      const float maxDistance = float.MaxValue; /* todo: get rid of this factor; make a cleaner implementation */
      var ray = new Line2(fromPoint - Direction.Perpendicular * maxDistance, fromPoint + Direction.Perpendicular*maxDistance);
      var line = GetIntersectionLine(ray);
      if (line == null) {
        // This means that one of the end points is closest to us ..
        return fromPoint.Distance(Start) < fromPoint.Distance(End) ? Start : End;
      }
      if (!line.Value.IsPoint) throw new ArgumentException("Intersection should be a point");
      return line.Value.Start;
    }

    /// <summary>
    /// True if both lines intersect
    /// </summary>
    public bool IntersectsWith(Line2 otherLine) {
      return GetIntersectionLine(otherLine) != null;
    }

    /// <summary>
    /// Returns the line (or point) at which the two lines intersect.
    /// </summary>
    public Line2? GetIntersectionLine(Line2 otherLine, bool limitLengths = true) {
      // http://stackoverflow.com/questions/563198/how-do-you-detect-where-two-line-segments-intersect
      // Find point on first line
      float qMinusPCrossS = Vector2.CrossProduct(otherLine.Start - Start, otherLine.Direction);
      float rXs = Vector2.CrossProduct(Direction, otherLine.Direction);
      if (Math.Abs(rXs) < float.Epsilon) {
        // lines are parallel
        if (Math.Abs(qMinusPCrossS) < float.Epsilon) {
          // lines are colinear
          return limitLengths ? CalculateIntersectingParallelLineSegment(this, otherLine) : this; /* todo: return infinite line here */
        }
        return null; // lines never intersect as they are parallel
      }

      // Otherwise: one-point intersection
      var t = qMinusPCrossS / rXs;
      if (limitLengths && (t < 0 || t > Length))
        return null; // not intersecting within the given t
      var u = Vector2.CrossProduct(otherLine.Start - Start, Direction)/rXs;
      if (limitLengths && (u < 0 || u > otherLine.Length))
        return null; // not intersecting within the given u
      var pt = Start + Direction*t;
      return new Line2(pt, pt);
    }

    /// <summary>
    /// Calculates the segment of the two parallel lines which is present in both lines
    /// </summary>
    private static Line2? CalculateIntersectingParallelLineSegment(Line2 a, Line2 b, bool secondCheck = false) {
      float startT = (b.Start.X - a.Start.X) / a.Direction.X;
      if (float.IsNaN(startT)) startT = (b.Start.Y - a.Start.Y) / a.Direction.Y;
      float endT = (b.End - a.Start).X / a.Direction.X;
      if (float.IsNaN(endT)) endT = (b.End - a.Start).Y / a.Direction.Y;

      var start = a.Start + a.Direction*startT;
      var end = a.Start + a.Direction*endT;

      // Limit the length
      if (startT < 0) start = a.Start;
      if (endT < 0) end = a.Start;
      if (startT > (a.End - a.Start).Length) start = a.End;
      if (endT > (a.End - a.Start).Length) end = a.End;

      var newLine = new Line2(start, end);
      if (newLine.IsPoint) return null;
      return secondCheck ? newLine : CalculateIntersectingParallelLineSegment(b, newLine, secondCheck: true);
    }

    public override string ToString() {
      return string.Format("Line[{0} -> {1}]", Start, End);
    }
  }
}

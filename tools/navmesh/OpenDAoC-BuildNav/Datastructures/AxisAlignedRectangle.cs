using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace CEM.Datastructures
{
  /// <summary>
  /// Axis-Aligned (non-rotated) rectangle
  /// </summary>
  internal struct AxisAlignedRectangle {
    public AxisAlignedRectangle(Vector2 topLeft, Vector2 bottomRight) {
      TopLeft = new Vector2(Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y));
      BottomRight = new Vector2(Math.Max(topLeft.X, bottomRight.X), Math.Max(topLeft.Y, bottomRight.Y));
    }
    public AxisAlignedRectangle(float x1, float y1, float x2, float y2) : this(new Vector2(x1, y1), new Vector2(x2, y2)) {}

    /// <summary>
    /// Top-Left
    /// </summary>
    public Vector2 TopLeft;

    /// <summary>
    /// Bottom-Right
    /// </summary>
    public Vector2 BottomRight;

    /// <summary>
    /// Returns the middle of this rect
    /// </summary>
    public Vector2 Middle {
      get { return (TopLeft + BottomRight)/2; }
    }

    /// <summary>
    /// Returnms the size of this rect
    /// </summary>
    public Vector2 Size { get { return BottomRight - TopLeft; } }

    /// <summary>
    /// True if both rectangles are intersecting
    /// </summary>
    /// <param name="secondRect"></param>
    /// <returns></returns>
    public bool IntersectsWith(AxisAlignedRectangle secondRect) {
      return Intersection(secondRect) != null;
    }

    /// <summary>
    /// Returns the intersection area of both rectangles
    /// </summary>
    public AxisAlignedRectangle? Intersection(AxisAlignedRectangle secondRect) {
      // TODO: implement this natively to prevent the loss of precision
      var intersection = ToRectangleF();
      intersection.Intersect(secondRect.ToRectangleF());
      if (intersection.IsEmpty)
        return null;
      return new AxisAlignedRectangle() {
        TopLeft = new Vector2(intersection.Left, intersection.Top),
        BottomRight = new Vector2(intersection.Right, intersection.Bottom)
      };
    }

    /// <summary>
    /// Returns the trimmed line at which the borders of the two rects collides
    /// </summary>
    /// <param name="secondRect"></param>
    /// <returns></returns>
    public Line2? BorderIntersection(AxisAlignedRectangle secondRect) {
      if (IntersectsWith(secondRect))
        throw new ArgumentException("Rects are overlapping");
      return
        Borders.SelectMany(a => secondRect.Borders.Select(b => new {A = a, B = b}))
               .Select(pair => pair.A.GetIntersectionLine(pair.B))
               .Where(line => line.HasValue && !line.Value.IsPoint)
               .FirstOrDefault(line => line.HasValue);
    }

    
    /// <summary>
    /// Returns the line border fo this rect
    /// </summary>
    public IEnumerable<Line2> Borders {
      get {
        var topRight = new Vector2(BottomRight.X, TopLeft.Y);
        var bottomLeft= new Vector2(TopLeft.X, BottomRight.Y);
        yield return new Line2 { Start = TopLeft, End = topRight};
        yield return new Line2 { Start = topRight, End = BottomRight };
        yield return new Line2 { Start = BottomRight, End = bottomLeft };
        yield return new Line2 { Start = bottomLeft, End = TopLeft };
      }
    } 
    
    /// <summary>
    /// Converts this AA-Rect to a RectangleF
    /// </summary>
    /// <returns></returns>
    public RectangleF ToRectangleF() {
      return new RectangleF((float)TopLeft.X, (float)TopLeft.Y, (float)Size.X, (float)Size.Y);
    }
    public override string ToString() {
      return string.Format("Rect[{0},{1},{2},{3}]", TopLeft.X, TopLeft.Y, BottomRight.X, BottomRight.Y);
    }
  }
}

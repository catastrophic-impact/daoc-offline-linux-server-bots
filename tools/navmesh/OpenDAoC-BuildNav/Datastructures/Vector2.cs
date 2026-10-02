using System;
using System.Globalization;
using OpenTK;

namespace CEM.Datastructures {
  /// <summary>
  /// represents a point in 2 dimensional space
  /// </summary>
  public struct Vector2 {
    public bool Equals(Vector2 other) {
      return X.Equals(other.X) && Y.Equals(other.Y);
    }

    public override bool Equals(object obj) {
      if (ReferenceEquals(null, obj)) return false;
      return obj is Vector2 && Equals((Vector2) obj);
    }

    public override int GetHashCode() {
      unchecked {
        return (X.GetHashCode()*397) ^ Y.GetHashCode();
      }
    }

    /// <summary>
    /// Nullvector
    /// </summary>
    public static readonly Vector2 Zero = new Vector2(0, 0);

    /// <summary>
    /// X-unit vector
    /// </summary>
    public static readonly Vector2 UnitX = new Vector2(1, 0);

    /// <summary>
    /// Y-unit vector
    /// </summary>
    public static readonly Vector2 UnitY = new Vector2(0, 1);

    /// <summary>
    /// The factor to convert a heading value to radians
    /// </summary>
    /// <remarks>
    /// Heading to degrees = heading * (360 / 4096)
    /// Degrees to radians = degrees * (PI / 180)
    /// </remarks>
    public const float HEADING_TO_RADIAN = (float)((360.0/4096.0)*(Math.PI/180.0));

    /// <summary>
    /// The factor to convert radians to a heading value
    /// </summary>
    /// <remarks>
    /// Radians to degrees = radian * (180 / PI)
    /// Degrees to heading = degrees * (4096 / 360)
    /// </remarks>
    public const float RADIAN_TO_HEADING = (float)((180.0/Math.PI)*(4096.0/360.0));

    /// <summary>
    /// Constructs a new 2D point object
    /// </summary>
    /// <param name="x">The X coord</param>
    /// <param name="y">The Y coord</param>
    public Vector2(float x, float y) {
      X = x;
      Y = y;
    }

    /// <summary>
    /// Constructs a new 2D point object
    /// </summary>
    /// <param name="point">The 2D point</param>
    public Vector2(Vector2 point)
      : this(point.X, point.Y) {}

    /// <summary>
    /// X coord of this point
    /// </summary>
    public float X;

    /// <summary>
    /// Y coord of this point
    /// </summary>
    public float Y;

    /// <summary>
    /// Returns the length of this vector
    /// </summary>
    /// <returns></returns>
    public float Length {
      get { return (float)Math.Sqrt(X*X + Y*Y); }
    }

    /// <summary>
    /// Returns the perpendicular vector
    /// </summary>
    public Vector2 Perpendicular {
      get {
        // m1 * m2 = -1
        return new Vector2(Y, -X);
      }
    }

    /// <summary>
    /// Normalizes this vector
    /// </summary>
    /// <returns></returns>
    public Vector2 Normalize() {
      if (this == Zero)
        return this;
      return this / Length;
    }

    /// <summary>
    /// Returns the (ax*by - ay*bx)
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    public static float CrossProduct(Vector2 a, Vector2 b) {
      // http://stackoverflow.com/questions/563198/how-do-you-detect-where-two-line-segments-intersect
      return a.X * b.Y - a.Y * b.X;
    }

    /// <summary>
    /// Returns the distance between both vectors
    /// </summary>
    /// <param name="a"></param>
    /// <returns></returns>
    public float Distance(Vector2 a) {
      var rel = a - this;
      return (float)Math.Sqrt(rel.X*rel.X + rel.Y*rel.Y);
    }

    /// <summary>
    /// Creates the string representation of this point
    /// </summary>
    /// <returns></returns>
    public override string ToString() {
      return string.Format("({0},{1})", X.ToString(CultureInfo.InvariantCulture), Y.ToString(CultureInfo.InvariantCulture));
    }

    public static bool operator ==(Vector2 a, Vector2 b) {
      return (a - b).Length <= float.Epsilon;
    }
    public static bool operator !=(Vector2 a, Vector2 b) {
      return !(a == b);
    }
    public static Vector2 operator +(Vector2 a, Vector2 b) {
      return new Vector2(a.X + b.X, a.Y + b.Y);
    }
    public static Vector2 operator -(Vector2 a, Vector2 b) {
      return new Vector2(a.X - b.X, a.Y - b.Y);
    }
    public static Vector2 operator -(Vector2 a) {
      return Zero - a;
    }
    public static Vector2 operator *(float factor, Vector2 a) {
      return a*factor;
    }
    public static Vector2 operator *(Vector2 a, float factor) {
      return new Vector2(a.X * factor, a.Y * factor);
    }
    public static Vector2 operator/(Vector2 a, float factor)
    {
      return new Vector2(a.X / factor, a.Y / factor);
    }

    /// <summary>
    /// calculate heading from point1 to point2
    /// </summary>
    /// <param name="p1"></param>
    /// <param name="p2"></param>
    /// <returns></returns>
    public static ushort GetHeadingToLocation(Vector2 p1, Vector2 p2) {
      float dx = p2.X - p1.X;
      float dy = p2.Y - p1.Y;
      var heading = (short) (Math.Atan2(-dx, dy)*RADIAN_TO_HEADING);
      if (heading < 0) heading += 0x1000;
      return (ushort) heading;
    }

    public static ushort GetHeadingToLocation(Vector3 p1, Vector3 p2) {
      float dx = p2.X - p1.X;
      float dy = p2.Y - p1.Y;
      float heading = (float)(Math.Atan2(-dx, dy)*RADIAN_TO_HEADING);
      if (heading < 0) heading += 0x1000;
      return (ushort) heading;
    }
  }
}
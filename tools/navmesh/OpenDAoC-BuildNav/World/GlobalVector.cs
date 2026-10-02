using System;
using CEM.Datastructures;
using OpenTK;

namespace CEM.World
{
  /// <summary>
  /// Global Vector containing both region id and glocs
  /// </summary>
  [Serializable]
  internal struct GlobalVector : IPoint3D {
    /// <summary>
    /// Middle of Cotswold
    /// [System] You are at X:560502 Y:511897 Z:2344 Heading:957 Region:1 Intern ID: 1
    /// </summary>
    public static readonly GlobalVector LocationCotswold = new GlobalVector(560502, 511897, 2344, 1);

    /// <summary>
    /// Begin of Dartmoor
    /// </summary>
    public static readonly GlobalVector LocationDartmoor = new GlobalVector(388145, 716065, 2168, 1);

    /// <summary>
    /// Middle of camelot, near castle.
    /// </summary>
    public static readonly GlobalVector LocationCamelot = new GlobalVector(38778, 24380, 8264, 10);

    private ushort _regionID;

    public GlobalVector(Vector3 pos, Region r) {
      Position = pos;
      _regionID = r.ID;
    }
    public GlobalVector(float x, float y, float z, ushort regionID) {
      Position = new Vector3(x, y, z);
      _regionID = regionID;
    }

    /// <summary>
    /// Position
    /// </summary>
    public Vector3 Position;

    /// <summary>
    /// Region
    /// </summary>
    public Region Region {
      get { return WorldMgr.GetRegion(_regionID); }
      set { _regionID = value.ID; }
    }

    /// <summary>
    /// Returns the zone in which this vector is located
    /// </summary>
    public Zone2 Zone { get { return Region[Position]; } }

    Vector3 IPoint3D.Position { get { return Position; } }

    public override string ToString() {
      return string.Format("{0}/{1}", Position, Zone);
    }

    /// <summary>
    /// Returns the distance between both vectors, or infinity if in different regions
    /// </summary>
    public float Distance(GlobalVector vec) {
      return vec.Region == Region ? (vec.Position - Position).Length : float.PositiveInfinity;
    }

    /// <summary>
    /// Calculates the most likely ground position for this vector
    /// </summary>
    public void CalculateGroundPosition() {
      Position = Zone.GetNearestGround(Position - Zone.OffsetVector)
                 + Zone.OffsetVector;
    }
  }
}

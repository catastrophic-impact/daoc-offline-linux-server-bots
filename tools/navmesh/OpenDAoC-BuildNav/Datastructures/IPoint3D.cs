using OpenTK;
namespace CEM.Datastructures {
  /// <summary>
  /// interface for classes that represent a point in 3d space
  /// </summary>
  public interface IPoint3D {
    Vector3 Position { get; }
  }
}
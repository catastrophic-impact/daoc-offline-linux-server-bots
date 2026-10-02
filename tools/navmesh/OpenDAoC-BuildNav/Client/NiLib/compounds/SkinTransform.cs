using System.IO;
using OpenTK;

namespace MNL {
  public class SkinTransform {
    public Matrix4 Rotation;
    public Vector3 Translation;
    public float Scale;

    public SkinTransform(NiFile file, BinaryReader reader) {
      Rotation = reader.ReadMatrix33();
      Translation = reader.ReadVector3();
      Scale = reader.ReadSingle();
    }
  }
}

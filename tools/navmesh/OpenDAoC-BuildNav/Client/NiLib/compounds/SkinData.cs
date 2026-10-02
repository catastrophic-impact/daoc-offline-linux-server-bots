using System.IO;
using OpenTK;

namespace MNL {
  public class SkinData {
    public SkinTransform Transform;
    public Vector3 BoundingSphereOffset;
    public float BoundingSphereRadius;
    public ushort[] Unkown13Shorts;
    public ushort NumVertices;
    public SkinWeight[] VertexWeights;

    public SkinData(NiFile file, BinaryReader reader, bool hasVertexWeights) {
      Transform = new SkinTransform(file, reader);
      BoundingSphereOffset = reader.ReadVector3();
      BoundingSphereRadius = reader.ReadSingle();

      if (file.Version == eNifVersion.VER_20_3_0_9
          && file.Header.UserVersion == 131072) {
        Unkown13Shorts = new ushort[13];
        for (var i = 0; i < 13; i++) {
          Unkown13Shorts[i] = reader.ReadUInt16();
        }
      }

      NumVertices = reader.ReadUInt16();
      if (hasVertexWeights) {
        VertexWeights = new SkinWeight[NumVertices];
        for (var i = 0; i < NumVertices; i++)
          VertexWeights[i] = new SkinWeight(file, reader);
      }
    }
  }
}

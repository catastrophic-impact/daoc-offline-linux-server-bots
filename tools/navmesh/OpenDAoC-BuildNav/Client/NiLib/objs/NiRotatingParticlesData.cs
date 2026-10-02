using System.IO;
using OpenTK;

namespace MNL {
  public class NiRotatingParticlesData : NiParticlesData {
    public bool HasRotations2;
    public Vector4[] Rotations2;

    public NiRotatingParticlesData(NiFile file, BinaryReader reader) : base(file, reader) {
      if (Version <= eNifVersion.VER_4_2_2_0) {
        HasRotations2 = reader.ReadBoolean();
        Rotations2 = new Vector4[NumVertices];
        for (var i = 0; i < NumVertices; i++)
          Rotations2[i] = reader.ReadVector4();
      }
    }
  }
}

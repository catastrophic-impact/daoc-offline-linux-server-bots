using System.IO;
using OpenTK;

namespace MNL {
  public class Morph {
    public NiString FrameName;
    public uint NumKeys;
    public uint Interpolation;
    public KeyGroup<FloatKey> Keys;
    public uint UnkownInt = 0;
    public Vector3[] Vectors;

    public Morph(NiFile file, BinaryReader reader, uint numVertices) {
      if (file.Version >= eNifVersion.VER_10_1_0_106) {
        FrameName = new NiString(file, reader);
      }

      if (file.Version <= eNifVersion.VER_10_1_0_0) {
        Keys = new KeyGroup<FloatKey>(reader);
      }

      if (file.Version >= eNifVersion.VER_10_1_0_106
          && file.Version <= eNifVersion.VER_10_2_0_0) {
        UnkownInt = reader.ReadUInt32();
      }

      if (file.Version >= eNifVersion.VER_20_0_0_4
          && file.Version <= eNifVersion.VER_20_1_0_3) {
        UnkownInt = reader.ReadUInt32();
      }

      Vectors = new Vector3[numVertices];
      for (var i = 0; i < numVertices; i++) {
        Vectors[i] = reader.ReadVector3();
      }
    }
  }
}

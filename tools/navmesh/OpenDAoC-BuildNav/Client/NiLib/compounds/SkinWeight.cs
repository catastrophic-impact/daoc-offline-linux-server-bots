using System.IO;

namespace MNL {
  public class SkinWeight {
    public ushort Index;
    public float Weight;

    public SkinWeight(NiFile file, BinaryReader reader) {
      Index = reader.ReadUInt16();
      Weight = reader.ReadSingle();
    }
  }
}

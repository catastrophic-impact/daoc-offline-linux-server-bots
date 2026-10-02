using System.IO;

namespace MNL {
  public class SkinPartitionUnkownItem1 {
    public uint Flags;
    public float Unkown1;
    public float Unkown2;
    public float Unkown3;
    public float Unkown4;
    public float Unkown5;

    public SkinPartitionUnkownItem1(NiFile file, BinaryReader reader) {
      Flags = reader.ReadUInt32();
      Unkown1 = reader.ReadSingle();
      Unkown2 = reader.ReadSingle();
      Unkown3 = reader.ReadSingle();
      Unkown4 = reader.ReadSingle();
      Unkown5 = reader.ReadSingle();
    }
  }
}

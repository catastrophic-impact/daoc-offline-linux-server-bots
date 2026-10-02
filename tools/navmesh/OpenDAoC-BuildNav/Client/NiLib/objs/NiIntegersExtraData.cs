using System.IO;

namespace MNL {
  public class NiIntegersExtraData : NiExtraData {
    public uint[] ExtraIntData;

    public NiIntegersExtraData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      ExtraIntData = new uint[(int)reader.ReadUInt32()];
      for (var i = 0; i < ExtraIntData.Length; i++)
        ExtraIntData[i] = reader.ReadUInt32();
    }
  }
}
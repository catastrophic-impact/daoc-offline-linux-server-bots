using System.IO;

namespace MNL {
  public class NiIntegerExtraData : NiExtraData {
    public uint Data;

    public NiIntegerExtraData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Data = reader.ReadUInt32();
    }
  }
}
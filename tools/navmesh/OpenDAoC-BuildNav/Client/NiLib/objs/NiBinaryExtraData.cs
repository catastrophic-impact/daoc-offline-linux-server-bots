using System.IO;

namespace MNL {
  public class NiBinaryExtraData : NiExtraData {
    public byte[] Data;

    public NiBinaryExtraData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Data = reader.ReadBytes((int)reader.ReadUInt32());
    }
  }
}

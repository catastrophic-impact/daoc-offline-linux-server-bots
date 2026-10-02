using System.IO;

namespace MNL {
  public class NiStringExtraData : NiExtraData {
    public uint BytesRemaining;
    public NiString StringData;

    public NiStringExtraData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (Version <= eNifVersion.VER_4_2_2_0) {
        BytesRemaining = reader.ReadUInt32();
      }

      StringData = new NiString(file, reader);
    }
  }
}
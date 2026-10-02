using System.IO;

namespace MNL {
  public class NiExtraData : NiObject {
    public NiString Name;
    public NiRef<NiExtraData> NextExtraData;

    public NiExtraData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (File.Header.Version >= eNifVersion.VER_10_0_1_0) {
        Name = new NiString(file, reader);
      }
      if (File.Header.Version <= eNifVersion.VER_4_2_2_0) {
        NextExtraData = new NiRef<NiExtraData>(reader.ReadUInt32());
      }
    }
  }
}

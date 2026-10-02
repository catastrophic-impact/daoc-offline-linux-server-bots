using System.IO;

namespace MNL {
  public class NiStringsExtraData : NiExtraData {
    public NiString[] ExtraStringData;

    public NiStringsExtraData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      ExtraStringData = new NiString[(int)reader.ReadUInt32()];
      for (var i = 0; i < ExtraStringData.Length; i++) {
        ExtraStringData[i] = new NiString(file, reader);
      }
    }
  }
}
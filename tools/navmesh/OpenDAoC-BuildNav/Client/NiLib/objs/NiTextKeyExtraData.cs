using System;
using System.IO;

namespace MNL {
  public class NiTextKeyExtraData : NiExtraData {
    public uint NumTextKeys;
    public uint UnkownInt1;
    public StringKey[] TextKeys;

    public NiTextKeyExtraData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (Version <= eNifVersion.VER_4_2_2_0)
        UnkownInt1 = reader.ReadUInt32();

      NumTextKeys = reader.ReadUInt32();

      TextKeys = new StringKey[NumTextKeys];
      for (var i = 0; i < NumTextKeys; i++) {
        TextKeys[i] = new StringKey(reader, eKeyType.LINEAR_KEY);
      }
    }
  }
}
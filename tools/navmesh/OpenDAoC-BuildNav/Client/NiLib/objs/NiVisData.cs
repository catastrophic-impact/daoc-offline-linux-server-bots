using System.IO;

namespace MNL {
  public class NiVisData : NiObject {
    public ByteKey[] Keys;

    public NiVisData(NiFile file, BinaryReader reader) : base(file, reader) {
      var numKeys = reader.ReadUInt32();

      Keys = new ByteKey[numKeys];

      for (var i = 0; i < Keys.Length; i++)
        Keys[i] = new ByteKey(reader, eKeyType.LINEAR_KEY);
    }
  }
}

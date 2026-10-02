using System;
using System.IO;

namespace MNL {
  public class StringKey {
    public float Time;
    public NiString Value;

    public StringKey(BinaryReader reader, eKeyType type) {
      Time = reader.ReadSingle();

      if (type != eKeyType.LINEAR_KEY)
        throw new Exception("Invalid eKeyType");

      Value = new NiString(null, reader);
    }
  }
}
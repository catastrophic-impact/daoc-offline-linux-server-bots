using System;
using System.IO;
using OpenTK;

namespace MNL {
  public class ByteKey {
    public float Time;
    public byte Value;
    public Vector3 TBC;

    public ByteKey(BinaryReader reader, eKeyType type) {
      Time = reader.ReadSingle();

      if (type != eKeyType.LINEAR_KEY)
        throw new Exception("Invalid eKeyType");

      Value = reader.ReadByte();

      if (type == eKeyType.TBC_KEY) {
        TBC = reader.ReadVector3();
      }
    }
  }
}
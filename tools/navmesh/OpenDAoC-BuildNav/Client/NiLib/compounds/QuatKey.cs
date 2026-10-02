using System;
using System.IO;
using OpenTK;

namespace MNL {
  public class QuatKey {
    public float Time;
    public Vector4 Value;
    public Vector3 TBC;

    public QuatKey(BinaryReader reader, eKeyType type) {
      Time = reader.ReadSingle();

      if (type < eKeyType.LINEAR_KEY || type > eKeyType.TBC_KEY)
        throw new Exception("Invalid eKeyType");

      Value = reader.ReadVector4();

      if (type == eKeyType.TBC_KEY) {
        TBC = reader.ReadVector3();
      }
    }
  }
}
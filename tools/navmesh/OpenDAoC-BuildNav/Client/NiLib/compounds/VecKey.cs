using System;
using System.IO;
using OpenTK;

namespace MNL {
  public class VecKey : BaseKey {
    public float Time;
    public Vector3 Value;
    public Vector3 Forward;
    public Vector3 Backward;
    public Vector3 TBC;

    public VecKey(BinaryReader reader, eKeyType type) : base(reader, type) {
      Time = reader.ReadSingle();
      if (type < eKeyType.LINEAR_KEY || type > eKeyType.TBC_KEY)
        throw new Exception("Invalid eKeyType!");

      if (type == eKeyType.LINEAR_KEY) {
        Value = reader.ReadVector3();
      }

      if (type == eKeyType.QUADRATIC_KEY) {
        Value = reader.ReadVector3();
        Forward = reader.ReadVector3();
        Backward = reader.ReadVector3();
      }

      if (type == eKeyType.TBC_KEY) {
        Value = reader.ReadVector3();
        TBC = reader.ReadVector3();
      }
    }
  }
}
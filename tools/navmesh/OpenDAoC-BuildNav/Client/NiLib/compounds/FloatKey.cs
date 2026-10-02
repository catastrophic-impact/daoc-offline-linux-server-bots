using System;
using System.IO;
using OpenTK;

namespace MNL {
  public class FloatKey : BaseKey {
    public float Time;
    public float Value;
    public float Forward;
    public float Backward;
    public Vector3 TBC;

    public FloatKey(BinaryReader reader, eKeyType type) : base(reader, type) {
      Time = reader.ReadSingle();
      Value = reader.ReadSingle();

      if (type < eKeyType.LINEAR_KEY || type > eKeyType.TBC_KEY)
        throw new Exception("Invalid eKeyType!");

      if (type == eKeyType.QUADRATIC_KEY) {
        Forward = reader.ReadSingle();
        Backward = reader.ReadSingle();
      }

      if (type == eKeyType.TBC_KEY) {
        TBC = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
      }
    }
  }
}
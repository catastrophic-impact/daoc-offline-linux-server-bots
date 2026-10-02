using System;
using System.IO;
using OpenTK;
using OpenTK.Graphics;

namespace MNL {
  public class Color4Key : BaseKey {
    public float Time = 0f;
    public Color4 Value;
    public Color4 Forward;
    public Color4 Backward;

    public Color4Key(BinaryReader reader, eKeyType type) : base(reader, type) {
      Time = reader.ReadSingle();
      if (type < eKeyType.LINEAR_KEY || type > eKeyType.TBC_KEY)
        throw new Exception("Invalid eKeyType!");

      Value = reader.ReadColor4();

      if (type == eKeyType.QUADRATIC_KEY) {
        Forward = reader.ReadColor4();
        Backward = reader.ReadColor4();
      }
    }
  }
}

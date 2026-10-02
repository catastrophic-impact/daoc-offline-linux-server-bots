using System.IO;

namespace MNL {
  public struct Triangle {
    public ushort X;
    public ushort Y;
    public ushort Z;

    public Triangle(ushort x, ushort y, ushort z) {
      X = x;
      Y = y;
      Z = z;
    }

    public Triangle(BinaryReader reader) {
      X = reader.ReadUInt16();
      Y = reader.ReadUInt16();
      Z = reader.ReadUInt16();
    }
  }
}

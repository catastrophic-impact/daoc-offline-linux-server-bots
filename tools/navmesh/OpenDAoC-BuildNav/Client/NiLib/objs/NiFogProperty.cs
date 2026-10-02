using System.IO;
using OpenTK;

namespace MNL {
  public class NiFogProperty : NiProperty {
    public ushort Flags;
    public float Depth;
    public Vector3 Color;

    public NiFogProperty(NiFile file, BinaryReader reader) : base(file, reader) {
      Flags = reader.ReadUInt16();
      Depth = reader.ReadSingle();
      Color = reader.ReadColor3();
    }
  }
}
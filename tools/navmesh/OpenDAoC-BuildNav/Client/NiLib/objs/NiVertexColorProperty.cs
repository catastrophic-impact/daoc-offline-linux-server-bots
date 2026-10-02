using System;
using System.IO;

namespace MNL {
  public class NiVertexColorProperty : NiProperty {
    public ushort Flags;
    public uint VertexMode;
    public uint LightingMode;

    public NiVertexColorProperty(NiFile file, BinaryReader reader) : base(file, reader) {
      Flags = reader.ReadUInt16();

      if (Version > eNifVersion.VER_20_0_0_5) {
        throw new Exception("unsupported data!");
      }

      VertexMode = reader.ReadUInt32();
      LightingMode = reader.ReadUInt32();
    }
  }
}

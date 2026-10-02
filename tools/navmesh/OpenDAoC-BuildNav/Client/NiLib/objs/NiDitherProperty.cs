using System.IO;

namespace MNL {
  public class NiDitherProperty : NiProperty {
    public ushort Flags = 0;
    public NiDitherProperty(NiFile file, BinaryReader reader) : base(file, reader) {
      Flags = reader.ReadUInt16();
    }
  }
}
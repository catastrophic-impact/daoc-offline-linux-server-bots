using System.IO;

namespace MNL {
  public class NiSpecularProperty : NiProperty {
    public ushort Flags;

    public NiSpecularProperty(NiFile file, BinaryReader reader) : base(file, reader) {
      Flags = reader.ReadUInt16();
    }
  }
}
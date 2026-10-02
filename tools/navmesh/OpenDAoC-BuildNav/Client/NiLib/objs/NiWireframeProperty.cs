using System.IO;

namespace MNL {
  public class NiWireframeProperty : NiProperty {
    public ushort Flags;

    public NiWireframeProperty(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Flags = reader.ReadUInt16();
    }
  }
}
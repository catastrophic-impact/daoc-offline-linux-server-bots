using System.IO;

namespace MNL {
  public class NiZBufferProperty : NiProperty {
    public ushort Flags;
    public uint ZCompareMode = 0;

    public NiZBufferProperty(NiFile file, BinaryReader reader) : base(file, reader) {
      Flags = reader.ReadUInt16();

      if (Version >= eNifVersion.VER_4_1_0_12
          && Version <= eNifVersion.VER_20_0_0_5) {
        ZCompareMode = reader.ReadUInt32();
      }
    }
  }
}
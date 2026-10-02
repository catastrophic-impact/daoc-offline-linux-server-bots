using System.IO;

namespace MNL {
  public class MipMap {
    public uint Width;
    public uint Height;
    public uint Offset;

    public MipMap(NiFile file, BinaryReader reader) {
      Width = reader.ReadUInt32();
      Height = reader.ReadUInt32();
      Offset = reader.ReadUInt32();
    }
  }
}

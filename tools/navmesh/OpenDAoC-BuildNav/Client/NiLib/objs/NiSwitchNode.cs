using System.IO;

namespace MNL {
  public class NiSwitchNode : NiNode {
    public ushort UnkownFlags;
    public int UnkownInt;

    public NiSwitchNode(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (File.Header.Version >= eNifVersion.VER_10_0_1_0) {
        UnkownFlags = reader.ReadUInt16();
      }

      UnkownInt = reader.ReadInt32();
    }
  }
}
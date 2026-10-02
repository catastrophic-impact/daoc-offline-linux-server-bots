using System.IO;

namespace MNL {
  public class NiBillboardNode : NiNode {
    public eBillboardMode BillboardMode;

    public NiBillboardNode(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (File.Header.Version >= eNifVersion.VER_10_1_0_0) {
        BillboardMode = (eBillboardMode)reader.ReadUInt16();
      }
    }
  }
}
using System.IO;

namespace MNL {
  public class NiLookAtController : NiTimeController {
    public ushort Unknown1 = 0;
    public NiRef<NiNode> CameraTargetNode;

    public NiLookAtController(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (File.Header.Version >= eNifVersion.VER_10_1_0_0) {
        Unknown1 = reader.ReadUInt16();
      }

      CameraTargetNode = new NiRef<NiNode>(reader);
    }
  }
}
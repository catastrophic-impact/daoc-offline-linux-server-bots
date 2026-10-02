using System.IO;

namespace MNL {
  public class NiVisController : NiBoolInterpController {
    public NiRef<NiVisData> Data;

    public NiVisController(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (Version <= eNifVersion.VER_10_1_0_0) {
        Data = new NiRef<NiVisData>(reader);
      }
    }
  }
}
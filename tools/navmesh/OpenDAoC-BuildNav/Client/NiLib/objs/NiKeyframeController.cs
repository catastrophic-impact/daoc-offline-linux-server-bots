using System.IO;

namespace MNL {
  public class NiKeyframeController : NiSingleInterpController {
    public NiRef<NiKeyframeData> Data;

    public NiKeyframeController(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (Version <= eNifVersion.VER_10_1_0_0) {
        Data = new NiRef<NiKeyframeData>(reader);
      }
    }
  }
}
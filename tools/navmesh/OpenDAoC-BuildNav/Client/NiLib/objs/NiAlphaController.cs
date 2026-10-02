
using System.IO;

namespace MNL {
  public class NiAlphaController : NiFloatInterpController {
    public NiRef<NiFloatData> Data;

    public NiAlphaController(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (File.Header.Version <= eNifVersion.VER_10_1_0_0) {
        Data = new NiRef<NiFloatData>(reader);
      }
    }
  }
}
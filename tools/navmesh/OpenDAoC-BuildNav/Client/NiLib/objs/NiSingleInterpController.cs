using System.IO;

namespace MNL {
  public class NiSingleInterpController : NiInterpController {
    public NiRef<NiInterpolator> Interpolator;

    public NiSingleInterpController(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (Version >= eNifVersion.VER_10_2_0_0) {
        Interpolator = new NiRef<NiInterpolator>(reader);
      }
    }
  }
}

using System.IO;

namespace MNL {
  public class NiTriStrips : NiTriBasedGeometry {
    public NiTriStrips(NiFile file, BinaryReader reader)
        : base(file, reader) {

    }
  }
}

using System.IO;

namespace MNL {
  public class NiTriShape : NiTriBasedGeometry {
    public NiTriShape(NiFile file, BinaryReader reader)
        : base(file, reader) {

    }
  }
}

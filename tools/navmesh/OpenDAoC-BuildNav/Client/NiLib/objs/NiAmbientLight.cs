using System.IO;

namespace MNL {
  public class NiAmbientLight : NiLight {
    public NiAmbientLight(NiFile file, BinaryReader reader)
        : base(file, reader) {

    }
  }
}
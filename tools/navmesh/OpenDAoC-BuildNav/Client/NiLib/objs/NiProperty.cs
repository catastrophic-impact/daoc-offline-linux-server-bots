using System.IO;

namespace MNL {
  public class NiProperty : NiObjectNET {
    public NiProperty(NiFile file, BinaryReader reader)
        : base(file, reader) {
    }
  }
}

using System.IO;

namespace MNL {
  public class NiFloatData : NiObject {
    public KeyGroup<FloatKey> Data;

    public NiFloatData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Data = new KeyGroup<FloatKey>(reader);
    }
  }
}

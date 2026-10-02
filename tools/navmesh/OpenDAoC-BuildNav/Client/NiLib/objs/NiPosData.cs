using System.IO;

namespace MNL {
  public class NiPosData : NiObject {
    public KeyGroup<VecKey> Data;

    public NiPosData(NiFile file, BinaryReader reader) : base(file, reader) {
      Data = new KeyGroup<VecKey>(reader);
    }
  }
}

using System.IO;

namespace MNL {
  public class NiColorData : NiObject {
    public KeyGroup<Color4Key> Data;

    public NiColorData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Data = new KeyGroup<Color4Key>(reader);
    }
  }
}

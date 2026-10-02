using System.IO;

namespace MNL {
  public class NiUVData : NiObject {
    public KeyGroup<FloatKey> UTranslation;
    public KeyGroup<FloatKey> VTranslation;
    public KeyGroup<FloatKey> UScalingAndTiling;
    public KeyGroup<FloatKey> VScalingAndTiling;

    public NiUVData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      UTranslation = new KeyGroup<FloatKey>(reader);
      VTranslation = new KeyGroup<FloatKey>(reader);
      UScalingAndTiling = new KeyGroup<FloatKey>(reader);
      VScalingAndTiling = new KeyGroup<FloatKey>(reader);
    }
  }
}

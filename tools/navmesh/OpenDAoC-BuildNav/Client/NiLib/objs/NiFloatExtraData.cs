using System.IO;

namespace MNL {
  public class NiFloatExtraData : NiExtraData {
    public float Data;

    public NiFloatExtraData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Data = reader.ReadSingle();
    }
  }
}
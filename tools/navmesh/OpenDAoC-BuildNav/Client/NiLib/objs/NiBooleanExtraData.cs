using System.IO;

namespace MNL {
  public class NiBooleanExtraData : NiExtraData {
    public bool Data;

    public NiBooleanExtraData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Data = reader.ReadBoolean();
    }
  }
}

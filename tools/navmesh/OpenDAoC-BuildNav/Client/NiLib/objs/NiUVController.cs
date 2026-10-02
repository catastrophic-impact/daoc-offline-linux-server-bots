using System.IO;

namespace MNL {
  public class NiUVController : NiTimeController {
    public ushort UnkownShort1 = 0;
    public NiRef<NiUVData> Data;

    public NiUVController(NiFile file, BinaryReader reader)
        : base(file, reader) {
      UnkownShort1 = reader.ReadUInt16();
      Data = new NiRef<NiUVData>(reader);
    }
  }
}

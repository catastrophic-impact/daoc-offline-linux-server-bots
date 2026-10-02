using System.IO;

namespace MNL {
  public class NiPathController : NiTimeController {
    public ushort Unknown1;
    public uint Unknown2;
    public float Unknown3;
    public float Unknown4;
    public ushort Unknown5;
    public NiRef<NiPosData> PosData;
    public NiRef<NiFloatData> FloatData;

    public NiPathController(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (File.Header.Version >= eNifVersion.VER_10_1_0_0) {
        Unknown1 = reader.ReadUInt16();
      }
      Unknown2 = reader.ReadUInt32();
      Unknown3 = reader.ReadSingle();
      Unknown4 = reader.ReadSingle();
      Unknown5 = reader.ReadUInt16();
      PosData = new NiRef<NiPosData>(reader);
      FloatData = new NiRef<NiFloatData>(reader);
    }
  }
}

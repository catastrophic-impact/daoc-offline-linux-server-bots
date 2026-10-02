using System.IO;

namespace MNL {
  public class NiTimeController : NiObject {
    public NiRef<NiTimeController> NextController;
    public ushort Flags;
    public float Frequency;
    public float Phase;
    public float StartTime;
    public float StopTime;
    public NiRef<NiObjectNET> Target;
    public uint UnkownInt;

    public NiTimeController(NiFile file, BinaryReader reader)
        : base(file, reader) {
      NextController = new NiRef<NiTimeController>(reader);
      Flags = reader.ReadUInt16();
      Frequency = reader.ReadSingle();
      Phase = reader.ReadSingle();
      StartTime = reader.ReadSingle();
      StopTime = reader.ReadSingle();
      if (file.Header.Version >= eNifVersion.VER_3_3_0_13) {
        Target = new NiRef<NiObjectNET>(reader);
      }
      if (file.Header.Version <= eNifVersion.VER_3_1) {
        UnkownInt = reader.ReadUInt32();
      }
    }
  }
}

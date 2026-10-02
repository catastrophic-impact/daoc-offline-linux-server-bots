using System.IO;

namespace MNL {
  public class NiPoint3InterpController : NiSingleInterpController {
    public eTargetColor TargetColor;
    public NiRef<NiPosData> Data;

    public NiPoint3InterpController(NiFile file, BinaryReader reader) : base(file, reader) {
      if (Version >= eNifVersion.VER_10_1_0_0)
        TargetColor = (eTargetColor)reader.ReadUInt16();

      if (Version <= eNifVersion.VER_10_1_0_0)
        Data = new NiRef<NiPosData>(reader);
    }
  }
}

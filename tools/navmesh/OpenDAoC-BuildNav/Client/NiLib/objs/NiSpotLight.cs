using System.IO;

namespace MNL {
  public class NiSpotLight : NiPointLight {
    public float CutoffAngle;
    public float UnkownFloat;
    public float Exponent;

    public NiSpotLight(NiFile file, BinaryReader reader)
        : base(file, reader) {
      CutoffAngle = reader.ReadSingle();
      if (Version >= eNifVersion.VER_20_2_0_7)
        UnkownFloat = reader.ReadSingle();
      Exponent = reader.ReadSingle();
    }
  }
}

using System.IO;

namespace MNL {
  public class NiPointLight : NiLight {
    public float ConstantAttenuation;
    public float LinearAttenuation;
    public float QuadraticAttenuation;

    public NiPointLight(NiFile file, BinaryReader reader) : base(file, reader) {
      ConstantAttenuation = reader.ReadSingle();
      LinearAttenuation = reader.ReadSingle();
      QuadraticAttenuation = reader.ReadSingle();
    }
  }
}

using System.IO;

namespace MNL {
  public class NiParticleColorModifier : NiParticleModifier {
    public NiRef<NiColorData> Data;

    public NiParticleColorModifier(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Data = new NiRef<NiColorData>(reader);
    }
  }
}

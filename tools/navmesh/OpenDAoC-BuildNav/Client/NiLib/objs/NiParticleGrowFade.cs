using System.IO;

namespace MNL {
  public class NiParticleGrowFade : NiParticleModifier {
    public float Grow;
    public float Fade;

    public NiParticleGrowFade(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Grow = reader.ReadSingle();
      Fade = reader.ReadSingle();
    }
  }
}

using System.IO;

namespace MNL {
  public class NiParticleModifier : NiObject {
    public NiRef<NiParticleModifier> Next;
    public NiRef<NiParticleSystemController> Controller;

    public NiParticleModifier(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Next = new NiRef<NiParticleModifier>(reader);
      if (File.Header.Version >= eNifVersion.VER_4_0_0_2) {
        Controller = new NiRef<NiParticleSystemController>(reader);
      }
    }
  }
}

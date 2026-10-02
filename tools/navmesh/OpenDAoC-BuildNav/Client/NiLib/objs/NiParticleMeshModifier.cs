using System.IO;

namespace MNL {
  public class NiParticleMeshModifier : NiParticleModifier {
    public NiRef<NiAVObject>[] ParticleMeshes;

    public NiParticleMeshModifier(NiFile file, BinaryReader reader)
        : base(file, reader) {
      ParticleMeshes = new NiRef<NiAVObject>[reader.ReadUInt32()];

      for (var i = 0; i < ParticleMeshes.Length; i++)
        ParticleMeshes[i] = new NiRef<NiAVObject>(reader);
    }
  }
}

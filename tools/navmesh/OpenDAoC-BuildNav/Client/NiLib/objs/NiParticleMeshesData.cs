using System.IO;

namespace MNL {
  public class NiParticleMeshesData : NiRotatingParticlesData {
    public NiRef<NiAVObject> UnkownLink;

    public NiParticleMeshesData(NiFile file, BinaryReader reader) : base(file, reader) {
      UnkownLink = new NiRef<NiAVObject>(reader);
    }
  }
}

using System.IO;
using OpenTK;

namespace MNL {
  public class NiParticleRotation : NiParticleModifier {
    public bool RandomInitalAxis;
    public Vector3 InitialAxis;
    public float Speed;

    public NiParticleRotation(NiFile file, BinaryReader reader)
        : base(file, reader) {
      RandomInitalAxis = reader.ReadBoolean();
      InitialAxis = reader.ReadVector3();
      Speed = reader.ReadSingle();
    }
  }
}

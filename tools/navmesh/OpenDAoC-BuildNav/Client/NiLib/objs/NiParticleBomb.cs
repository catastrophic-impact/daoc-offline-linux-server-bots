using System.IO;
using OpenTK;

namespace MNL {
  public class NiParticleBomb : NiParticleModifier {
    public float Decay;
    public float Duration;
    public float DeltaV;
    public float Start;
    public eDecayType DecayType;
    public eSymmetryType SymmetryType;
    public Vector3 Position;
    public Vector3 Direction;

    public NiParticleBomb(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Decay = reader.ReadSingle();
      Duration = reader.ReadSingle();
      DeltaV = reader.ReadSingle();
      Start = reader.ReadSingle();
      DecayType = (eDecayType)reader.ReadUInt32();
      if (Version >= eNifVersion.VER_4_1_0_12) {
        SymmetryType = (eSymmetryType)reader.ReadUInt32();
      }
      Position = reader.ReadVector3();
      Direction = reader.ReadVector3();
    }
  }
}

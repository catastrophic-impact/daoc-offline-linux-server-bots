using System.IO;
using OpenTK;

namespace MNL {
  public class NiGravity : NiParticleModifier {
    public float UnkownFloat1;
    public float Force;
    public uint Type;
    public Vector3 Position;
    public Vector3 Direction;

    public NiGravity(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (File.Header.Version >= eNifVersion.VER_4_0_0_2) {
        UnkownFloat1 = reader.ReadSingle();
      }

      Force = reader.ReadSingle();
      Type = reader.ReadUInt32();
      Position = reader.ReadVector3();
      Direction = reader.ReadVector3();
    }
  }
}

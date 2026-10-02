using System.IO;

namespace MNL {
  public class NiSphericalCollider : NiParticleModifier {
    public float UnkownFloat1;
    public ushort UnkownShort1;
    public float UnkownFloat2;
    public ushort UnkownShort2;
    public float UnkownFloat3;
    public float UnkownFloat4;
    public float UnkownFloat5;

    public NiSphericalCollider(NiFile file, BinaryReader reader)
        : base(file, reader) {
      UnkownFloat1 = reader.ReadSingle();
      UnkownShort1 = reader.ReadUInt16();
      UnkownFloat2 = reader.ReadSingle();

      if (Version <= eNifVersion.VER_4_2_0_2) {
        UnkownShort2 = reader.ReadUInt16();
      } else {
        UnkownFloat3 = reader.ReadSingle();
      }

      UnkownFloat4 = reader.ReadSingle();
      UnkownFloat5 = reader.ReadSingle();
    }
  }
}

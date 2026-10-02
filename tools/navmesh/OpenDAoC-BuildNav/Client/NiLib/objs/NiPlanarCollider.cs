using System.IO;

namespace MNL {
  public class NiPlanarCollider : NiParticleModifier {
    public ushort UnkownShort1;
    public float UnkownFloat1;
    public float UnkownFloat2;
    public ushort UnkownShort2;
    public float UnkownFloat3;
    public float UnkownFloat4;
    public float UnkownFloat5;
    public float UnkownFloat6;
    public float UnkownFloat7;
    public float UnkownFloat8;
    public float UnkownFloat9;
    public float UnkownFloat10;
    public float UnkownFloat11;
    public float UnkownFloat12;
    public float UnkownFloat13;
    public float UnkownFloat14;
    public float UnkownFloat15;
    public float UnkownFloat16;

    public NiPlanarCollider(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (Version >= eNifVersion.VER_10_0_1_0)
        UnkownShort1 = reader.ReadUInt16();

      UnkownFloat1 = reader.ReadSingle();
      UnkownFloat2 = reader.ReadSingle();

      if (Version == eNifVersion.VER_4_2_2_0)
        UnkownShort2 = reader.ReadUInt16();

      UnkownFloat3 = reader.ReadSingle();
      UnkownFloat4 = reader.ReadSingle();
      UnkownFloat5 = reader.ReadSingle();
      UnkownFloat6 = reader.ReadSingle();
      UnkownFloat7 = reader.ReadSingle();
      UnkownFloat8 = reader.ReadSingle();
      UnkownFloat9 = reader.ReadSingle();
      UnkownFloat10 = reader.ReadSingle();
      UnkownFloat11 = reader.ReadSingle();
      UnkownFloat12 = reader.ReadSingle();
      UnkownFloat13 = reader.ReadSingle();
      UnkownFloat14 = reader.ReadSingle();
      UnkownFloat15 = reader.ReadSingle();
      UnkownFloat16 = reader.ReadSingle();
    }
  }
}

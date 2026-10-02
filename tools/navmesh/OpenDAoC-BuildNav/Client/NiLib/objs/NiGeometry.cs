using System;
using System.IO;

namespace MNL {
  public class NiGeometry : NiAVObject {
    public NiRef<NiGeometryData> Data;
    public NiRef<NiSkinInstance> SkinInstance;
    public NiString[] MaterialNames;
    public int[] MaterialExtraData;
    public int ActiveMaterial;
    public bool HasShader;
    public string ShaderName;
    public uint UnkownInteger;


    public NiGeometry(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Data = new NiRef<NiGeometryData>(reader);

      if (Version >= eNifVersion.VER_3_3_0_13) {
        SkinInstance = new NiRef<NiSkinInstance>(reader);
      }

      if (Version >= eNifVersion.VER_20_2_0_7) {
        MaterialNames = new NiString[reader.ReadUInt32()];
        for (var i = 0; i < MaterialNames.Length; i++) {
          MaterialNames[i] = new NiString(file, reader);
        }
        MaterialExtraData = new int[MaterialNames.Length];
        for (var i = 0; i < MaterialNames.Length; i++) {
          MaterialExtraData[i] = reader.ReadInt32();
        }

        ActiveMaterial = reader.ReadInt32();
      }

      if (Version >= eNifVersion.VER_10_0_1_0
          && Version <= eNifVersion.VER_20_1_0_3) {
        HasShader = reader.ReadBoolean();
        if (HasShader) {
          ShaderName = new string(reader.ReadChars(reader.ReadInt32()));
          UnkownInteger = reader.ReadUInt32();
        }
      }

      if (Version == eNifVersion.VER_10_4_0_1)
        reader.ReadUInt32(); //unkown

      if (Version >= eNifVersion.VER_20_2_0_7) {
        throw new Exception("unspported data");
      }
    }
  }
}

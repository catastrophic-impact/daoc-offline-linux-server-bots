using System;
using System.Collections.Generic;
using System.IO;

namespace MNL {
  public class NiGeomMorpherController : NiInterpController {
    public ushort ExtraFlags;
    public byte Unknown2;
    public NiRef<NiMorphData> Data;
    public bool AlwaysUpdate;
    public uint NumInterpolators;
    public NiRef<NiInterpolator>[] Interpolators;
    //public MorphWeight[] InterpolatorWeights;
    public uint NumUnkownInts;
    public uint[] UnkownInts;

    public NiGeomMorpherController(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (Version >= eNifVersion.VER_10_0_1_2) {
        ExtraFlags = reader.ReadUInt16();
      }

      if (Version == eNifVersion.VER_10_1_0_106) {
        Unknown2 = reader.ReadByte();
      }

      Data = new NiRef<NiMorphData>(reader);
      AlwaysUpdate = reader.ReadBoolean();

      if (Version >= eNifVersion.VER_10_1_0_106) {
        NumInterpolators = reader.ReadUInt32();
      }

      if (Version >= eNifVersion.VER_10_1_0_106
          && Version < eNifVersion.VER_20_2_0_7) {
        Interpolators = new NiRef<NiInterpolator>[NumInterpolators];
        for (var i = 0; i < NumInterpolators; i++)
          Interpolators[i] = new NiRef<NiInterpolator>(reader);
      }
    }
  }
}

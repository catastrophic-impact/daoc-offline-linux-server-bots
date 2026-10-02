using System;
using System.IO;

namespace MNL {
  public class NiObjectNET : NiObject {
    public NiString Name;
    public NiRef<NiExtraData>[] ExtraData;
    public NiRef<NiTimeController> Controller;

    public NiObjectNET(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Name = new NiString(file, reader);

      if (File.Header.Version <= eNifVersion.VER_2_3) {
        throw new Exception("Unsupported Version!");
      }

      if (File.Header.Version >= eNifVersion.VER_3_0 && File.Header.Version <= eNifVersion.VER_4_2_2_0) {
        ExtraData = new NiRef<NiExtraData>[1];
        ExtraData[0] = new NiRef<NiExtraData>(reader.ReadUInt32());
      }

      if (File.Header.Version >= eNifVersion.VER_10_0_1_0) {
        var numExtraData = reader.ReadUInt32();
        ExtraData = new NiRef<NiExtraData>[numExtraData];
        for (var i = 0; i < numExtraData; i++) {
          ExtraData[i] = new NiRef<NiExtraData>(reader.ReadUInt32());
        }
      }

      if (File.Header.Version >= eNifVersion.VER_3_0) {
        Controller = new NiRef<NiTimeController>(reader.ReadUInt32());
      }

    }
  }
}

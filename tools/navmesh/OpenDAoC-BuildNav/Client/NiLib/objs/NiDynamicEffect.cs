using System.IO;

namespace MNL {
  public class NiDynamicEffect : NiAVObject {
    public bool SwitchState;
    public NiRef<NiAVObject>[] AffectedNodes;

    public NiDynamicEffect(NiFile file, BinaryReader reader)
        : base(file, reader) {
      SwitchState = true;

      if (Version >= eNifVersion.VER_10_1_0_106)
        SwitchState = reader.ReadBoolean();

      if (Version <= eNifVersion.VER_4_0_0_2
          || Version >= eNifVersion.VER_10_0_1_0) {
        AffectedNodes = new NiRef<NiAVObject>[reader.ReadUInt32()];
        for (var i = 0; i < AffectedNodes.Length; i++)
          AffectedNodes[i] = new NiRef<NiAVObject>(reader);
      }
    }
  }
}

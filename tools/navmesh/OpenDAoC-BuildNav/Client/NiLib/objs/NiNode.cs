using System.IO;

namespace MNL {
  public class NiNode : NiAVObject {
    public NiRef<NiAVObject>[] Children;
    public NiRef<NiDynamicEffect>[] Effects;

    public NiNode(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Children = new NiRef<NiAVObject>[reader.ReadUInt32()];
      for (var i = 0; i < Children.Length; i++) {
        Children[i] = new NiRef<NiAVObject>(reader.ReadUInt32());
      }
      Effects = new NiRef<NiDynamicEffect>[reader.ReadUInt32()];
      for (var i = 0; i < Effects.Length; i++) {
        Effects[i] = new NiRef<NiDynamicEffect>(reader.ReadUInt32());
      }
    }
  }
}

using System.IO;

namespace MNL {
  public class NiSkinInstance : NiObject {
    public NiRef<NiSkinData> Data;
    public NiRef<NiSkinPartition> Partition;
    public NiRef<NiNode> SkeletonRoot;
    public NiRef<NiNode>[] Bones;

    public NiSkinInstance(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Data = new NiRef<NiSkinData>(reader);

      if (Version >= eNifVersion.VER_10_2_0_0)
        Partition = new NiRef<NiSkinPartition>(reader);

      SkeletonRoot = new NiRef<NiNode>(reader);

      var numBones = reader.ReadUInt32();
      Bones = new NiRef<NiNode>[numBones];
      for (var i = 0; i < numBones; i++) {
        Bones[i] = new NiRef<NiNode>(reader);
      }
    }
  }
}

using System.IO;

namespace MNL {
  public class NiSkinData : NiObject {
    public SkinTransform Transform;
    public NiRef<NiSkinPartition> Partition;
    public bool HasVertexWeights;
    public SkinData[] BoneList;

    public NiSkinData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      HasVertexWeights = true;
      Transform = new SkinTransform(file, reader);
      var numBones = reader.ReadUInt32();
      if (Version >= eNifVersion.VER_4_0_0_2 && Version <= eNifVersion.VER_10_1_0_0) {
        Partition = new NiRef<NiSkinPartition>(reader);
      }
      if (Version >= eNifVersion.VER_4_2_1_0) {
        HasVertexWeights = reader.ReadBoolean();
      }

      if (HasVertexWeights) {
        BoneList = new SkinData[numBones];
        for (var i = 0; i < numBones; i++) {
          BoneList[i] = new SkinData(file, reader, HasVertexWeights);
        }
      }
    }
  }
}

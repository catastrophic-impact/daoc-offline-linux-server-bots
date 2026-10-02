using System.IO;
using OpenTK;

namespace MNL {
  public class NiLODNode : NiSwitchNode {
    public Vector3 LODCenter;
    public LODRange[] LODLevels;
    public NiRef<NiLODData> LODLevelData;

    public NiLODNode(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (Version >= eNifVersion.VER_4_0_0_2
          && Version <= eNifVersion.VER_10_0_1_0) {
        LODCenter = reader.ReadVector3();
      }

      if (Version <= eNifVersion.VER_10_0_1_0) {
        var numLODLevels = reader.ReadUInt32();
        LODLevels = new LODRange[numLODLevels];
        for (var i = 0; i < numLODLevels; i++)
          LODLevels[i] = new LODRange(file, reader);
      }

      if (Version >= eNifVersion.VER_10_0_1_0) {
        LODLevelData = new NiRef<NiLODData>(reader);
      }
    }
  }
}
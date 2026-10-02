using System.IO;
using OpenTK;

namespace MNL {
  public class NiRangeLODData : NiLODData {
    public Vector3 LODCenter;
    public LODRange[] LODLevels;

    public NiRangeLODData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      LODCenter = reader.ReadVector3();
      var numLevels = reader.ReadUInt32();
      LODLevels = new LODRange[numLevels];
      for (var i = 0; i < numLevels; i++) {
        LODLevels[i] = new LODRange(file, reader);
      }
    }
  }
}
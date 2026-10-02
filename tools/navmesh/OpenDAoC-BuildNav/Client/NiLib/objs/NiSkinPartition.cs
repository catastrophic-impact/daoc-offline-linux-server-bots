using System.IO;

namespace MNL {
  public class NiSkinPartition : NiObject {
    public SkinPartition[] Partitions;

    public NiSkinPartition(NiFile file, BinaryReader reader) : base(file, reader) {
      var count = reader.ReadUInt32();
      Partitions = new SkinPartition[count];
      for (var i = 0; i < count; i++)
        Partitions[i] = new SkinPartition(file, reader);
    }
  }
}

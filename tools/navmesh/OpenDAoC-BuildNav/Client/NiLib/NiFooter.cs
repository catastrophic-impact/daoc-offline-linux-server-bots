using System.IO;

namespace MNL {
  public class NiFooter {
    public NiRef<NiObject>[] RootNodes;

    public NiFooter(NiFile file, BinaryReader reader) {
      if (file.Header.Version >= eNifVersion.VER_3_3_0_13) {
        var numRootNodes = reader.ReadUInt32();
        RootNodes = new NiRef<NiObject>[numRootNodes];

        for (var i = 0; i < numRootNodes; i++)
          RootNodes[i] = new NiRef<NiObject>(reader.ReadUInt32());
      }
    }
  }
}

using System.IO;
using OpenTK.Graphics;

namespace MNL {
  public class NiColorExtraData : NiExtraData {
    public Color4 Data;

    public NiColorExtraData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Data = reader.ReadColor4();
    }
  }
}

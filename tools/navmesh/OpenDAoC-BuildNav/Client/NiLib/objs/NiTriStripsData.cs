using System.IO;

namespace MNL {
  public class NiTriStripsData : NiTriBasedGeomData {
    public bool HasPoints = false;
    public ushort[][] Points;

    public NiTriStripsData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      var pointCounts = new ushort[reader.ReadUInt16()];
      for (var i = 0; i < pointCounts.Length; i++) {
        pointCounts[i] = reader.ReadUInt16();
      }

      if (Version >= eNifVersion.VER_10_0_1_3) {
        HasPoints = reader.ReadBoolean();
      }

      if (Version < eNifVersion.VER_10_0_1_3
          || HasPoints) {
        Points = new ushort[pointCounts.Length][];
        for (var i = 0; i < pointCounts.Length; i++) {
          Points[i] = new ushort[pointCounts[i]];
          for (ushort j = 0; j < pointCounts[i]; j++) {
            Points[i][j] = reader.ReadUInt16();
          }
        }
      }
    }
  }
}

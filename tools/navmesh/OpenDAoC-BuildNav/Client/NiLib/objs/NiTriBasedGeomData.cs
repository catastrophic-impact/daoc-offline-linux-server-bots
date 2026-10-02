using System.IO;

namespace MNL {
  public class NiTriBasedGeomData : NiGeometryData {
    public ushort NumTriangles;

    public NiTriBasedGeomData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      NumTriangles = reader.ReadUInt16();
    }
  }
}

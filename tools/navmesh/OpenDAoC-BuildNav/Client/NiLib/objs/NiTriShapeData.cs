using System.IO;

namespace MNL {
  public class NiTriShapeData : NiTriBasedGeomData {
    public uint NumTrianglePoints;
    public bool HasTriangles;
    public Triangle[] Triangles;
    public ushort[][] MatchGroups;

    public NiTriShapeData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      NumTrianglePoints = reader.ReadUInt32();

      if (Version >= eNifVersion.VER_10_1_0_0)
        HasTriangles = reader.ReadBoolean();

      if (Version <= eNifVersion.VER_10_0_1_2
          || (HasTriangles || Version >= eNifVersion.VER_10_0_1_3)) {
        Triangles = new Triangle[NumTriangles];
        for (var i = 0; i < NumTriangles; i++) {
          Triangles[i] = new Triangle(reader);
        }
      }

      if (Version >= eNifVersion.VER_3_1) {
        var numMatchGroups = reader.ReadUInt16();
        MatchGroups = new ushort[numMatchGroups][];
        for (var i = 0; i < numMatchGroups; i++) {
          var numVertices = reader.ReadUInt16();
          MatchGroups[i] = new ushort[numVertices];
          for (var c = 0; c < numVertices; c++) {
            MatchGroups[i][c] = reader.ReadUInt16();
          }
        }
      }
    }
  }
}

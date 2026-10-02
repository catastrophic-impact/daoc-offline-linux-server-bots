using System.IO;
using OpenTK;
using OpenTK.Graphics;

namespace MNL {
  public class NiGeometryData : NiObject {
    public uint Unkown1;
    public byte KeepFlags;
    public byte CompressFlags;
    public bool HasVertices;
    public Vector3[] Vertices;
    public byte TSpaceFlag;
    public bool HasNormals;
    public Vector3[] Normals;
    public bool HasVertexColors;
    public bool HasUV;
    public ushort ConsistencyFlags;
    public Vector3 Center;
    public float Radius;
    public Color4[] VertexColors;
    public Vector2[][] UVSets;
    public uint AdditionalDataID;
    public Vector3[] Binormals;
    public Vector3[] Tangents;
    public uint NumVertices;

    public NiGeometryData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (Version >= eNifVersion.VER_10_2_0_0) {
        Unkown1 = reader.ReadUInt32();
      }

      NumVertices = reader.ReadUInt16();
      if (Version >= eNifVersion.VER_10_1_0_0) {
        KeepFlags = reader.ReadByte();
        CompressFlags = reader.ReadByte();
      }

      HasVertices = reader.ReadBoolean();

      if (HasVertices) {
        Vertices = new Vector3[NumVertices];
        for (var i = 0; i < NumVertices; i++) {
          Vertices[i] = reader.ReadVector3();
        }
      }

      var numUVSets = 0;
      if (Version >= eNifVersion.VER_10_0_1_0) {
        numUVSets = reader.ReadByte();
        TSpaceFlag = reader.ReadByte();
      }
      HasNormals = reader.ReadBoolean();

      if (HasNormals) {
        Normals = new Vector3[NumVertices];
        for (var i = 0; i < NumVertices; i++)
          Normals[i] = reader.ReadVector3();
      }

      if (Version >= eNifVersion.VER_10_1_0_0) {
        Binormals = new Vector3[NumVertices];
        Tangents = new Vector3[NumVertices];
        if (HasNormals && (TSpaceFlag & 240) != 0) {
          for (var i = 0; i < NumVertices; i++) {
            Binormals[i] = reader.ReadVector3();
          }

          for (var i = 0; i < NumVertices; i++) {
            Tangents[i] = reader.ReadVector3();
          }
        }
      }

      Center = reader.ReadVector3();
      Radius = reader.ReadSingle();
      HasVertexColors = reader.ReadBoolean();
      if (HasVertexColors) {
        VertexColors = new Color4[NumVertices];
        for (var i = 0; i < NumVertices; i++)
          VertexColors[i] = reader.ReadColor4();
      }

      if (Version <= eNifVersion.VER_4_2_2_0) {
        numUVSets = reader.ReadByte();
        TSpaceFlag = reader.ReadByte();
      }

      if (Version <= eNifVersion.VER_4_0_0_2) {
        HasUV = reader.ReadBoolean();
      }

      var lnumUVSets = 0;

      if (!(Version >= eNifVersion.VER_20_2_0_7
          && File.Header.UserVersion == 1)) {
        lnumUVSets = numUVSets & 63;
      } else {
        lnumUVSets = numUVSets & 1;
      }

      UVSets = new Vector2[lnumUVSets][];
      for (var i = 0; i < lnumUVSets; i++) {
        UVSets[i] = new Vector2[NumVertices];
        for (var c = 0; c < NumVertices; c++)
          UVSets[i][c] = reader.ReadVector2();
      }

      if (Version >= eNifVersion.VER_10_0_1_0) {
        ConsistencyFlags = reader.ReadUInt16();
      }

      if (Version >= eNifVersion.VER_20_0_0_4) {
        AdditionalDataID = reader.ReadUInt32();
      }
    }
  }
}

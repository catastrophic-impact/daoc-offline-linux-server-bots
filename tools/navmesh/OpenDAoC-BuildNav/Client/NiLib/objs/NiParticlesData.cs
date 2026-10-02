using System.IO;
using OpenTK;

namespace MNL {
  public class NiParticlesData : NiGeometryData {
    /*public NiString Name;
    public ushort NumVertices;
    public byte KeepFlags;
    public byte CompressFlags;
    public bool HasVertices;
    public Vector3[] Vertices;
    public byte TSpaceFlag;
    public byte NumUVSets;
    public bool HasNormals = false;
    public Vector3[] Normals;
    public Vector3[] Binormals;
    public Vector3[] Tangents;
    public Vector3 Center;
    public float Radius;
    public bool HasVertexColors;
    public Color4[] VertexColors;
    public bool HasUV;
    public Vector2[][] UVSets;
    public ushort ConsistencyFlags;
    public uint AdditionalDataID;*/
    public ushort NumParticles;
    public float ParticleRadius;
    public bool HasRadii;
    public float[] Radii;
    public ushort NumActive;
    public bool HasSizes;
    public float[] Sizes;
    public bool HasRotations;
    public Vector4[] Rotations;

    public NiParticlesData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      /*if (File.Header.Version >= eNifVersion.VER_10_2_0_0)
      {
         Name = new NiString(file, reader);
      }

      NumVertices = reader.ReadUInt16();
      if (File.Header.Version >= eNifVersion.VER_10_1_0_0)
      {
          KeepFlags = reader.ReadByte();
          CompressFlags = reader.ReadByte();
      }
      HasVertices = reader.ReadBoolean();
      if (HasVertices)
      {
          Vertices = new Vector3[NumVertices];
          for (var i = 0; i < NumVertices; i++)
          {
              Vertices[i] = reader.ReadVector3();
          }
      }
      if (File.Header.Version >= eNifVersion.VER_10_0_1_0)
      {
          NumUVSets = reader.ReadByte();
          TSpaceFlag = reader.ReadByte();
      }

      HasNormals = reader.ReadBoolean();
      if (HasNormals)
      {
          Normals = new Vector3[NumVertices];
          for (var i = 0; i < NumVertices; i++)
          {
              Normals[i] = reader.ReadVector3();
          }
      }

      if (File.Header.Version >= eNifVersion.VER_10_1_0_0)
      {
          if (HasNormals && (TSpaceFlag & 240) != 0)
          {
              Binormals = new Vector3[NumVertices];
              Tangents = new Vector3[NumVertices];
              for (var i = 0; i < NumVertices; i++)
              {
                  Binormals[i] = reader.ReadVector3();
              }

              for (var i = 0; i < NumVertices; i++)
              {
                  Tangents[i] = reader.ReadVector3();
              }
          }
      }

      Center = reader.ReadVector3();
      Radius = reader.ReadSingle();
      HasVertexColors = reader.ReadBoolean();
      if (HasVertexColors)
      {
          VertexColors = new Color4[NumVertices];
          for (var i = 0; i < NumVertices; i++)
              VertexColors[i] = reader.ReadColor4();
      }

      if (File.Header.Version <= eNifVersion.VER_4_2_2_0)
      {
          NumUVSets = reader.ReadByte();
          TSpaceFlag = reader.ReadByte();
      }

      if (File.Header.Version <= eNifVersion.VER_4_0_0_2)
      {
          HasUV = reader.ReadBoolean();
      }

      if (!(File.Header.Version >= eNifVersion.VER_20_2_0_7 && File.Header.UserVersion == 1))
      {
          var uvsize = NumUVSets & 63;

          UVSets = new Vector2[uvsize][];
          for (var i = 0; i < uvsize; i++)
          {
              UVSets[i] = new Vector2[NumVertices];
              for (int c = 0; c < NumVertices; c++)
                  UVSets[i][c] = reader.ReadVector2();
          }

      }

      if (File.Header.Version >= eNifVersion.VER_10_0_1_0)
          ConsistencyFlags = reader.ReadUInt16();

      if (File.Header.Version >= eNifVersion.VER_20_0_0_4)
      {
          AdditionalDataID = reader.ReadUInt32();
      }
      */
      if (File.Header.Version <= eNifVersion.VER_4_0_0_2) {
        NumParticles = reader.ReadUInt16();
      }

      if (File.Header.Version <= eNifVersion.VER_10_0_1_0) {
        ParticleRadius = reader.ReadSingle();
      }

      if (File.Header.Version >= eNifVersion.VER_10_1_0_0) {
        HasRadii = reader.ReadBoolean();
        if (HasRadii) {
          Radii = reader.ReadFloatArray((int)(NumVertices));
        }
      }
      NumActive = reader.ReadUInt16();
      HasSizes = reader.ReadBoolean();
      if (HasSizes) {
        Sizes = reader.ReadFloatArray((int)NumVertices);
      }
      if (File.Header.Version >= eNifVersion.VER_10_0_1_0) {
        HasRotations = reader.ReadBoolean();

        if (HasRotations) {
          Rotations = new Vector4[NumVertices];
          for (var i = 0; i < NumVertices; i++) {
            Rotations[i] = reader.ReadVector4();
          }
        }
      }
    }
  }
}
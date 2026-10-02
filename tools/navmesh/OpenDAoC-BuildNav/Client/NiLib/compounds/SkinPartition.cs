using System.IO;

namespace MNL {
  public class SkinPartition {
    public ushort NumVertices;
    public ushort NumTriangles;
    public ushort NumBones;
    public ushort NumStrips;
    public ushort NumWeightsPerVertex;
    public ushort[] Bones;
    public bool HasVertexMap;
    public ushort[] VertexMap;
    public bool HasVertexWeights;
    public float[][] VertexWeights;
    public ushort[] StripLengths;
    public bool HasFaces;
    public ushort[][] Strips;
    public Triangle[] Triangles;
    public bool HasBoneIndicies;
    public byte[][] BoneIndicies;
    public ushort UnkownShort;
    public ushort UnkownShort2;
    public ushort UnkownShort3;
    public ushort NumVertices2;
    public ushort UnkownShort4;
    public ushort UnkownShort5;
    public ushort UnkownShort6;
    public SkinPartitionUnkownItem1[] UnkownArr;

    public SkinPartition(NiFile file, BinaryReader reader) {
      NumVertices = reader.ReadUInt16();
      NumTriangles = reader.ReadUInt16();
      NumBones = reader.ReadUInt16();
      NumStrips = reader.ReadUInt16();
      NumWeightsPerVertex = reader.ReadUInt16();
      Bones = reader.ReadUInt16Array(NumBones);
      HasVertexMap = true;
      HasVertexWeights = true;
      HasFaces = true;

      if (file.Version >= eNifVersion.VER_10_1_0_0) {
        HasVertexMap = reader.ReadBoolean();
      }

      if (HasVertexMap) {
        VertexMap = reader.ReadUInt16Array(NumVertices);
      }

      if (file.Version >= eNifVersion.VER_10_1_0_0) {
        HasVertexWeights = reader.ReadBoolean();
      }

      if (HasVertexWeights) {
        VertexWeights = new float[NumVertices][];
        for (var i = 0; i < NumVertices; i++) {
          VertexWeights[i] = reader.ReadFloatArray(NumWeightsPerVertex);
        }
      }

      StripLengths = reader.ReadUInt16Array(NumStrips);

      if (file.Version >= eNifVersion.VER_10_1_0_0) {
        HasFaces = reader.ReadBoolean();
      }

      if (HasFaces && NumStrips != 0) {
        Strips = new ushort[NumStrips][];

        for (var i = 0; i < NumStrips; i++) {
          Strips[i] = reader.ReadUInt16Array(StripLengths[i]);
        }
      } else if (HasFaces && NumStrips == 0) {
        Triangles = new Triangle[NumTriangles];
        for (var i = 0; i < Triangles.Length; i++)
          Triangles[i] = new Triangle(reader);
      }

      HasBoneIndicies = reader.ReadBoolean();
      if (HasBoneIndicies) {
        BoneIndicies = new byte[NumVertices][];
        for (var i = 0; i < BoneIndicies.Length; i++) {
          BoneIndicies[i] = new byte[NumWeightsPerVertex];
          for (var j = 0; j < NumWeightsPerVertex; j++) {
            BoneIndicies[i][j] = reader.ReadByte();
          }
        }
      }

      if (file.Header.UserVersion >= 12)
        UnkownShort = reader.ReadUInt16();

      if (file.Version == eNifVersion.VER_10_2_0_0
          && file.Header.UserVersion == 1) {
        UnkownShort2 = reader.ReadUInt16();
        UnkownShort3 = reader.ReadUInt16();
        NumVertices2 = reader.ReadUInt16();
        UnkownShort4 = reader.ReadUInt16();
        UnkownShort5 = reader.ReadUInt16();
        UnkownShort6 = reader.ReadUInt16();

        UnkownArr = new SkinPartitionUnkownItem1[NumVertices2];

        for (var i = 0; i < NumVertices2; i++) {
          UnkownArr[i] = new SkinPartitionUnkownItem1(file, reader);
        }
      }
    }
  }
}

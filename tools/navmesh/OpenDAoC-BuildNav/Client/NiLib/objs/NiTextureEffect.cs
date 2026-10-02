using System;
using System.IO;
using OpenTK;

namespace MNL {
  public class NiTextureEffect : NiDynamicEffect {
    public Matrix4 ModelProjectionMatrix;
    public Vector3 ModelProjectionTransform;
    public eTexFilterMode TextureFiltering;
    public eTexClampMode TextureClamping;
    public ushort Unknown1;
    public eEffectType EffectType;
    public eCoordGenType CoordGenType;
    //public NiRef<NiImage> Image;
    public NiRef<NiSourceTexture> SourceTexture;
    public bool ClippingPlane;
    public Vector3 unknownVector;
    public float Unknown2;
    public short PS2L;
    public short PS2K;
    public ushort Unknown3;


    public NiTextureEffect(NiFile file, BinaryReader reader)
        : base(file, reader) {
      ModelProjectionMatrix = reader.ReadMatrix33();
      ModelProjectionTransform = reader.ReadVector3();
      TextureFiltering = (eTexFilterMode)reader.ReadUInt32();
      TextureClamping = (eTexClampMode)reader.ReadUInt32();
      EffectType = (eEffectType)reader.ReadUInt32();
      CoordGenType = (eCoordGenType)reader.ReadUInt32();
      if (Version <= eNifVersion.VER_3_1) {
        throw new Exception("NOT SUPPORTED!");
        //Image = new NiRef<NiImage>(reader);
      }
      if (Version >= eNifVersion.VER_4_0_0_0) {
        SourceTexture = new NiRef<NiSourceTexture>(reader);
      }
      ClippingPlane = reader.ReadBoolean();
      unknownVector = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
      Unknown2 = reader.ReadSingle();
      if (File.Header.Version <= eNifVersion.VER_10_2_0_0) {
        PS2L = reader.ReadInt16();
        PS2K = reader.ReadInt16();
      }
      if (File.Header.Version <= eNifVersion.VER_4_1_0_12) {
        Unknown3 = reader.ReadUInt16();
      }

    }
  }
}

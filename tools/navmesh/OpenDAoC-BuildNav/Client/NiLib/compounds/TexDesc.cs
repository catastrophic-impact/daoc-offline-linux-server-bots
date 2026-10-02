using System.IO;
using OpenTK;

namespace MNL {
  public class TexDesc {
    public NiRef<NiSourceTexture> Source;
    public eTexClampMode ClampMode;
    public eTexFilterMode FilterMode;
    public ushort Flags;
    public uint UVSetIndex;
    public short PS2L;
    public short PS2K;
    public bool HasTextureTransform;
    public Vector2 Translation;
    public Vector2 Tiling;
    public float WRotation;
    public uint TransformType;
    public Vector2 CenterOffset;

    public TexDesc(NiFile file, BinaryReader reader) {
      Source = new NiRef<NiSourceTexture>(reader);

      if (file.Version <= eNifVersion.VER_20_0_0_5) {
        ClampMode = (eTexClampMode)reader.ReadUInt32();
        FilterMode = (eTexFilterMode)reader.ReadUInt32();
      }
      if (file.Version >= eNifVersion.VER_20_1_0_3) {
        Flags = reader.ReadUInt16();
      }
      if (file.Version <= eNifVersion.VER_20_0_0_5) {
        UVSetIndex = reader.ReadUInt32();
      }
      if (file.Version <= eNifVersion.VER_10_4_0_1) {
        PS2L = reader.ReadInt16();
        PS2K = reader.ReadInt16();
      }

      if (file.Version <= eNifVersion.VER_4_1_0_12) {
        reader.ReadUInt16();
      }

      if (file.Version >= eNifVersion.VER_10_1_0_0) {
        HasTextureTransform = reader.ReadBoolean();
        if (HasTextureTransform) {
          Translation = reader.ReadVector2();
          Tiling = reader.ReadVector2();
          WRotation = reader.ReadSingle();
          TransformType = reader.ReadUInt32();
          CenterOffset = reader.ReadVector2();
        }
      }
    }
  }
}

using System;
using System.IO;

namespace MNL {
  public class NiSourceTexture : NiTexture {
    public bool UseExternal;
    public NiString FileName;
    public ePixelLayout PixelLayout;
    public eMipMapFormat UseMipmaps;
    public eAlphaFormat AlphaFormat;
    public bool IsStatic;
    public bool DirectRender;
    public bool PersistentRenderData;

    public NiRef<ATextureRenderData> InternalTexture;

    public NiSourceTexture(NiFile file, BinaryReader reader)
        : base(file, reader) {
      IsStatic = true;

      UseExternal = reader.ReadBoolean();

      if (UseExternal) {
        FileName = new NiString(file, reader);

        if (Version >= eNifVersion.VER_10_1_0_0) {
          reader.ReadUInt32();
        }
      }

      if (!UseExternal) {
        if (Version <= eNifVersion.VER_10_0_1_0)
          reader.ReadByte();

        if (Version >= eNifVersion.VER_10_1_0_0)
          FileName = new NiString(file, reader);

        InternalTexture = new NiRef<ATextureRenderData>(reader);
      }

      PixelLayout = (ePixelLayout)reader.ReadUInt32();
      UseMipmaps = (eMipMapFormat)reader.ReadUInt32();
      AlphaFormat = (eAlphaFormat)reader.ReadUInt32();
      IsStatic = reader.ReadBoolean();

      if (Version >= eNifVersion.VER_10_1_0_106)
        DirectRender = reader.ReadBoolean();

      if (Version >= eNifVersion.VER_20_2_0_7)
        PersistentRenderData = reader.ReadBoolean();
    }
  }
}
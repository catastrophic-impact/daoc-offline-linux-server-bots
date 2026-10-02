using System.IO;

namespace MNL {
  public class ATextureRenderData : NiObject {
    public ePixelFormat PixelFormat;
    public uint RedMask;
    public uint GreenMask;
    public uint BlueMask;
    public uint AlphaMask;
    public byte BitsPerPixel;
    public byte[] Unkown3Bytes;
    public byte[] Unkown8Bytes;
    public uint UnkownInt;
    public uint UnkownInt2;
    public uint UnkownInt3;
    public uint UnkownInt4;
    public byte Flags;
    public byte UnkownByte1;
    public ChannelData[] ChannelData;
    public NiRef<NiPalette> Palette;
    public uint NumMipMaps;
    public uint BytesPerPixel;
    public MipMap[] MipMaps;

    public ATextureRenderData(NiFile file, BinaryReader reader) : base(file, reader) {
      PixelFormat = (ePixelFormat)reader.ReadUInt32();

      if (Version <= eNifVersion.VER_10_2_0_0) {
        RedMask = reader.ReadUInt32();
        GreenMask = reader.ReadUInt32();
        BlueMask = reader.ReadUInt32();
        AlphaMask = reader.ReadUInt32();
        BitsPerPixel = reader.ReadByte();
        Unkown3Bytes = new byte[3];
        for (var i = 0; i < Unkown3Bytes.Length; i++) {
          Unkown3Bytes[i] = reader.ReadByte();
        }
        Unkown8Bytes = new byte[8];
        for (var i = 0; i < Unkown8Bytes.Length; i++) {
          Unkown8Bytes[i] = reader.ReadByte();
        }
      }

      if (Version >= eNifVersion.VER_10_0_1_0
          && Version <= eNifVersion.VER_10_2_0_0) {
        UnkownInt = reader.ReadUInt32();
      }

      if (Version >= eNifVersion.VER_20_0_0_4) {
        BitsPerPixel = reader.ReadByte();
        UnkownInt2 = reader.ReadUInt32();
        UnkownInt3 = reader.ReadUInt32();
        Flags = reader.ReadByte();
        UnkownInt4 = reader.ReadUInt32();
      }

      if (Version >= eNifVersion.VER_20_3_0_6) {
        UnkownByte1 = reader.ReadByte();
      }

      if (Version >= eNifVersion.VER_20_0_0_4) {
        ChannelData = new ChannelData[4];
        for (var i = 0; i < 4; i++) {
          ChannelData[i] = new ChannelData(file, reader);
        }
      }

      Palette = new NiRef<NiPalette>(reader);
      NumMipMaps = reader.ReadUInt32();
      BytesPerPixel = reader.ReadUInt32();
      MipMaps = new MipMap[NumMipMaps];
      for (var i = 0; i < NumMipMaps; i++)
        MipMaps[i] = new MipMap(file, reader);

    }
  }
}

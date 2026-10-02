using System.IO;

namespace MNL {
  public class NiPixelData : ATextureRenderData {
    public uint NumPixels;
    public uint NumFaces;
    public byte[][] PixelData;

    public NiPixelData(NiFile file, BinaryReader reader) : base(file, reader) {
      NumPixels = reader.ReadUInt32();
      if (Version >= eNifVersion.VER_20_0_0_4) {
        NumFaces = reader.ReadUInt32();
        PixelData = new byte[NumFaces][];

        for (var i = 0; i < NumFaces; i++) {
          PixelData[i] = new byte[NumPixels];
          for (var j = 0; j < NumPixels; j++) {
            PixelData[i][j] = reader.ReadByte();
          }
        }
      }

      if (Version <= eNifVersion.VER_10_2_0_0) {
        NumFaces = 1;
        PixelData = new byte[NumFaces][];

        for (var i = 0; i < NumFaces; i++) {
          PixelData[i] = new byte[NumPixels];
          for (var j = 0; j < NumPixels; j++) {
            PixelData[i][j] = reader.ReadByte();
          }
        }
      }
    }
  }
}

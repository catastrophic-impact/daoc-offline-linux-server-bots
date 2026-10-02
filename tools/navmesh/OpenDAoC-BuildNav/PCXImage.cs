using System.IO;

namespace CEM.Client {
  /// <summary>
  /// Reads simple DAoC .pcx images
  /// </summary>
  public static class PCXImage {
    public static byte[,] Read(Stream s) {
      return Read(new BinaryReader(s));
    }

    public static byte[,] Read(BinaryReader bs) {
      bs.ReadBytes(4);
      ushort xmin = bs.ReadUInt16(); //x_min
      ushort ymin = bs.ReadUInt16(); //y_min
      ushort xmax = bs.ReadUInt16(); //x_max
      ushort ymax = bs.ReadUInt16(); //y_max
      bs.ReadBytes(4 + 48 + 1); //dpi + palette + reserved
      byte cp = bs.ReadByte(); //color planes
      ushort bl = bs.ReadUInt16(); //bytes/line
      bs.ReadBytes(6); //pl/size
      bs.ReadBytes(54); //empty

      int xsize = xmax - xmin + 1;
      int ysize = ymax - ymin + 1;

      int lineBytes = bl*cp;

      //start reading
      var buffer = new byte[xsize,ysize];

      for (int y = 0; y < ysize; y++) {
        int index = 0;

        do {
          byte b = bs.ReadByte();

          if ((b & 0xC0) == 0xC0) // top two bits
          {
            int count = b & 0x3F; // return lowest six bits in b
            byte b2 = bs.ReadByte();

            for (int a = 1; a <= count; a++) buffer[index++, y] = b2;
          }
          else buffer[index++, y] = b;
        } while (index < lineBytes);
      }

      bs.Close();

      return buffer;
    }

    public static void Save(string path, byte[,] data) {
      Stream s = new FileStream(path, FileMode.Create, FileAccess.Write);
      var bs = new BinaryWriter(s);

      bs.Write((byte) 10); //manufactor
      bs.Write((byte) 5); //version
      bs.Write((byte) 1); //encoding
      bs.Write((byte) 8); //bpp
      bs.Write((ushort) 0); //xmin
      bs.Write((ushort) 0); //ymin
      bs.Write((ushort) (data.GetLength(0) - 1)); //xmax
      bs.Write((ushort) (data.GetLength(1) - 1)); //ymax
      bs.Write((ushort) 0); //hdpi
      bs.Write((ushort) 0); //vdpi
      bs.Write(new byte[48]); //palette
      bs.Write((byte) 0); //reserved
      bs.Write((byte) 1); //color planes
      bs.Write((ushort) data.GetLength(1)); //bits per line
      bs.Write((ushort) 0); //palette info
      bs.Write((ushort) data.GetLength(0)); //hscreen
      bs.Write((ushort) data.GetLength(1)); //vscreen
      bs.Write(new byte[54]); //empty

      //start writing
      for (int y = 0; y < data.GetLength(1); y++) {
        int x = 0;
        while (x < data.GetLength(0)) {
          byte b = data[x, y];
          int count = 1;

          while (count < 63 && x + count < data.GetLength(1) && data[x + count, y] == b) count++;

          if (count > 1 || b >= 192) // top two bits
          {
            bs.Write((byte) (count | 0xC0)); //count 1
            bs.Write(b);
          }
          else bs.Write(b);

          x += count;
        }
      }
      bs.Write((byte) 12); //bild ende

      bs.Flush();
      bs.Close();

      //DEBUG
      /*Bitmap bmp = new Bitmap(256, 256, PixelFormat.Format24bppRgb);

			for (int x = 0; x < 256; x++)
				for (int y = 0; y < 256; y++)
					bmp.SetPixel(x, y, Color.FromArgb(data[x, y], data[x, y], data[x, y]));

			bmp.Save(path + ".bmp");*/
    }
  }
}
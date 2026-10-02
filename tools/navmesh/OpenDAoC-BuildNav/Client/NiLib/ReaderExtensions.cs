using System.IO;
using OpenTK;
using OpenTK.Graphics;

namespace MNL {
  public static class ReaderExtensions {
    public static float[] ReadFloatArray(this BinaryReader reader, int length) {
      var floats = new float[length];
      for (var i = 0; i < floats.Length; i++)
        floats[i] = reader.ReadSingle();

      return floats;
    }

    public static uint[] ReadUInt32Array(this BinaryReader reader, int length) {
      var result = new uint[length];
      for (var i = 0; i < result.Length; i++)
        result[i] = reader.ReadUInt32();

      return result;
    }

    public static ushort[] ReadUInt16Array(this BinaryReader reader, int length) {
      var result = new ushort[length];
      for (var i = 0; i < result.Length; i++)
        result[i] = reader.ReadUInt16();

      return result;
    }

    public static Vector2 ReadVector2(this BinaryReader reader) {
      return new Vector2(reader.ReadSingle(), reader.ReadSingle());
    }

    public static Vector3 ReadVector3(this BinaryReader reader) {
      return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    public static Vector4 ReadVector4(this BinaryReader reader) {
      return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    // da fuq! no color3 :(
    public static Vector3 ReadColor3(this BinaryReader reader) {
      return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    public static Color4 ReadColor4(this BinaryReader reader) {
      return new Color4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    public static Color4 ReadColor4Byte(this BinaryReader reader) {
      return new Color4(reader.ReadByte() / 255f, reader.ReadByte() / 255f, reader.ReadByte() / 255f, reader.ReadByte() / 255f);
    }

    public static Matrix4 ReadMatrix33(this BinaryReader reader) {
      var result = Matrix4.Identity;
      for (var y = 0; y < 3; y++) {
        for (var x = 0; x < 3; x++) {
          result[x, y] = reader.ReadSingle();
        }
      }
      return result;
    }
  }
}

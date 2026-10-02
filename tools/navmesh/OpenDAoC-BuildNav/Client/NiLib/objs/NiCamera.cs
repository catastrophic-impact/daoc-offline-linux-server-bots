using System.IO;

namespace MNL {
  public class NiCamera : NiAVObject {
    public ushort Unkown1;
    public float FrustrumLeft;
    public float FrustrumRight;
    public float FrustrumTop;
    public float FrustrumBottom;
    public float FrustrumNear;
    public float FrustrumFar;
    public bool UseOrthographicsProjection;
    public float ViewportLeft;
    public float ViewportRight;
    public float ViewportTop;
    public float ViewportBottom;
    public float LODAdjust;
    public NiRef<NiObject> UnkownLink;
    public uint Unkown2;
    public uint Unkown3;
    public uint Unkown4;

    public NiCamera(NiFile file, BinaryReader reader) : base(file, reader) {
      if (File.Header.Version >= eNifVersion.VER_10_1_0_0) {
        Unkown1 = reader.ReadUInt16();
      }

      FrustrumLeft = reader.ReadSingle();
      FrustrumRight = reader.ReadSingle();
      FrustrumTop = reader.ReadSingle();
      FrustrumBottom = reader.ReadSingle();
      FrustrumNear = reader.ReadSingle();
      FrustrumFar = reader.ReadSingle();
      if (File.Header.Version >= eNifVersion.VER_10_1_0_0) {
        UseOrthographicsProjection = reader.ReadBoolean();
      }
      ViewportLeft = reader.ReadSingle();
      ViewportRight = reader.ReadSingle();
      ViewportTop = reader.ReadSingle();
      ViewportBottom = reader.ReadSingle();
      LODAdjust = reader.ReadSingle();
      UnkownLink = new NiRef<NiObject>(reader);
      Unkown2 = reader.ReadUInt32();
      if (File.Header.Version >= eNifVersion.VER_4_2_1_0) {
        Unkown3 = reader.ReadUInt32();
      }
      if (File.Header.Version <= eNifVersion.VER_3_1) {
        Unkown4 = reader.ReadUInt32();
      }
    }
  }
}
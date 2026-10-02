using System.IO;

namespace MNL {
  public class NiTextureTransformController : NiFloatInterpController {
    public byte Unkown2;
    public eTexType TextureSlot;
    public eTexTransform Operation;
    public NiRef<NiFloatData> Data;

    public NiTextureTransformController(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Unkown2 = reader.ReadByte();
      TextureSlot = (eTexType)reader.ReadUInt32();
      Operation = (eTexTransform)reader.ReadUInt32();

      if (Version <= eNifVersion.VER_10_1_0_0) {
        Data = new NiRef<NiFloatData>(reader);
      }
    }
  }
}

using System.IO;

namespace MNL {
  public class NiStencilProperty : NiProperty {
    public ushort Flags;
    public bool IsStencilEnabled;
    public eStencilCompareMode StencilFunction;
    public uint StencilRef;
    public uint StencilMask;
    public eStencilAction FailAction;
    public eStencilAction ZFailAction;
    public eStencilAction PassAction;
    public eFaceDrawMode FaceDrawMode;

    public NiStencilProperty(NiFile file, BinaryReader reader) : base(file, reader) {
      if (File.Header.Version <= eNifVersion.VER_10_0_1_2) {
        Flags = reader.ReadUInt16();
      }
      if (File.Header.Version <= eNifVersion.VER_20_0_0_5) {
        IsStencilEnabled = reader.ReadBoolean();
        StencilFunction = (eStencilCompareMode)reader.ReadUInt32();
        StencilRef = reader.ReadUInt32();
        StencilMask = reader.ReadUInt32();
        FailAction = (eStencilAction)reader.ReadUInt32();
        ZFailAction = (eStencilAction)reader.ReadUInt32();
        PassAction = (eStencilAction)reader.ReadUInt32();
        FaceDrawMode = (eFaceDrawMode)reader.ReadUInt32();
      }
      if (File.Header.Version >= eNifVersion.VER_20_1_0_3) {
        Flags = reader.ReadUInt16();
        StencilRef = reader.ReadUInt32();
        StencilMask = reader.ReadUInt32();
      }
    }
  }
}

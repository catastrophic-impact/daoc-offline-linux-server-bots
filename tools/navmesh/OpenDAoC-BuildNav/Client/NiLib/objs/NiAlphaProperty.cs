using System.IO;
using OpenTK.Graphics.OpenGL;
namespace MNL {
  public class NiAlphaProperty : NiProperty {
    public ushort Flags = 0;
    public byte Threshold = 0;

    public bool AlphaBlendingEnabled = false;
    public BlendingFactorSrc SourceBlendMode;
    public BlendingFactorDest TargetBlendMode;
    public bool AlphaTestEnabled = false;
    public AlphaFunction AlphaTestMode;
    public bool DisableTriangleSorting;


    public NiAlphaProperty(NiFile file, BinaryReader reader) : base(file, reader) {
      Flags = reader.ReadUInt16();
      Threshold = reader.ReadByte();
      AlphaBlendingEnabled = (Flags & 0x01) == 1;

      SourceBlendMode = GetSrc((Flags & 0x1E) >> 1);
      TargetBlendMode = GetTarget((Flags & 0x1E0) >> 5);

      AlphaTestEnabled = (Flags & 0x200) == 0x200;
      AlphaTestMode = GetTestMode((Flags & 0x1C00) >> 10);
      DisableTriangleSorting = (Flags & 0x2000) == 0x2000;

    }

    private static BlendingFactorSrc GetSrc(int i) {
      switch (i) {
        default:
        case 0: return BlendingFactorSrc.One;
        case 1: return BlendingFactorSrc.Zero;
        case 2: return BlendingFactorSrc.SrcColor;
        case 3: return BlendingFactorSrc.OneMinusSrcColor;
        case 4: return BlendingFactorSrc.DstColor;
        case 5: return BlendingFactorSrc.OneMinusDstColor;
        case 6: return BlendingFactorSrc.SrcAlpha;
        case 7: return BlendingFactorSrc.OneMinusSrcAlpha;
        case 8: return BlendingFactorSrc.DstAlpha;
        case 9: return BlendingFactorSrc.OneMinusDstAlpha;
        case 10: return BlendingFactorSrc.SrcAlphaSaturate;
      }
    }

    private static BlendingFactorDest GetTarget(int i) {
      switch (i) {
        default:
        case 0: return BlendingFactorDest.One;
        case 1: return BlendingFactorDest.Zero;
        case 2: return BlendingFactorDest.SrcColor;
        case 3: return BlendingFactorDest.OneMinusSrcColor;
        case 4: return BlendingFactorDest.DstColor;
        case 5: return BlendingFactorDest.OneMinusDstColor;
        case 6: return BlendingFactorDest.SrcAlpha;
        case 7: return BlendingFactorDest.OneMinusSrcAlpha;
        case 8: return BlendingFactorDest.DstAlpha;
        case 9: return BlendingFactorDest.OneMinusDstAlpha;
        case 10: return BlendingFactorDest.SrcAlphaSaturate;
      }
    }

    private static AlphaFunction GetTestMode(int i) {
      switch (i) {
        default:
        case 0: return AlphaFunction.Always;
        case 1: return AlphaFunction.Less;
        case 2: return AlphaFunction.Equal;
        case 3: return AlphaFunction.Lequal;
        case 4: return AlphaFunction.Greater;
        case 5: return AlphaFunction.Notequal;
        case 6: return AlphaFunction.Gequal;
        case 7: return AlphaFunction.Never;
      }
    }
  }
}
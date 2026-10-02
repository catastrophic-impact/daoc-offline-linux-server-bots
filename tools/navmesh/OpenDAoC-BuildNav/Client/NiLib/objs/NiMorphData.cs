using System.IO;

namespace MNL {
  public class NiMorphData : NiObject {
    public uint NumMorphs;
    public uint NumVertices;
    public byte RelativeTargets;
    public Morph[] Morphs;

    public NiMorphData(NiFile file, BinaryReader reader)
        : base(file, reader) {

      NumMorphs = reader.ReadUInt32();
      NumVertices = reader.ReadUInt32();
      RelativeTargets = reader.ReadByte();
      Morphs = new Morph[NumMorphs];
      for (var i = 0; i < NumMorphs; i++) {
        Morphs[i] = new Morph(file, reader, NumVertices);
      }
    }
  }
}

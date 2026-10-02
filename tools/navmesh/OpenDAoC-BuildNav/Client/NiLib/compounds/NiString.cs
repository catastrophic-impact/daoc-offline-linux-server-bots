using System.IO;

namespace MNL {
  public class NiString {
    public string Value;

    public NiString(NiFile file, BinaryReader reader) {
      // TODO: make version dependent?
      Value = new string(reader.ReadChars((int)reader.ReadUInt32()));
    }

    public override string ToString() {
      return Value;
    }
  }
}

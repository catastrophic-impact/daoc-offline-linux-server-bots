using System.IO;

namespace MNL {
  public class NiRef<T> where T : NiObject {
    public T Object { get; private set; }
    public uint RefId { get; private set; }

    public NiRef(uint refId) {
      RefId = refId;
    }

    public NiRef(BinaryReader reader) : this(reader.ReadUInt32()) {

    }

    public bool IsValid() {
      return RefId != NiFile.INVALID_REF;
    }

    public void SetRef(NiFile file) {
      if (IsValid())
        Object = (T)file.ObjectsByRef[RefId];
    }
  }
}

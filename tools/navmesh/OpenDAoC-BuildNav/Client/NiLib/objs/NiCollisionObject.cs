using System.IO;

namespace MNL {
  public class NiCollisionObject : NiObject {
    public NiRef<NiAVObject> Target;

    public NiCollisionObject(NiFile file, BinaryReader reader) : base(file, reader) {
      Target = new NiRef<NiAVObject>(reader);
    }
  }
}

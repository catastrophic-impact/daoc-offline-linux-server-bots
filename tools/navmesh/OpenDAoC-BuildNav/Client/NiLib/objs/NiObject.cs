using System.IO;

namespace MNL {
  public class NiObject {
    public NiFile File;

    // conv. function :)
    public eNifVersion Version {
      get { return File.Version; }
    }

    public NiObject(NiFile file, BinaryReader reader) {
      File = file;
    }
  }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace MNL {
  public class NiLODData : NiObject {
    public NiLODData(NiFile file, BinaryReader reader)
        : base(file, reader) {

    }
  }
}
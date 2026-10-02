using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MNL
{
  class NiShadeProperty : NiProperty
  {
    public NiShadeProperty(NiFile file, BinaryReader reader) : base(file, reader) {
      ushort flags = reader.ReadUInt16();
    }
  }
}

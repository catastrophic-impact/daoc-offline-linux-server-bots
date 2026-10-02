using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CEM.Client.CSVTypes
{
  /// <summary>skycity.chunk</summary>
  [StructLayout(LayoutKind.Sequential)]
  public class SkyCityZoneCityChunk : ICsvParseable
  {
    public string NifName;

    public override string ToString()
    {
      return string.Format("[SkyCityChunk] NifName="+NifName);
    }
  }
}

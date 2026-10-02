using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CEM.Client.CSVTypes
{
  /// <summary>dungeon.chunk</summary>
  [StructLayout(LayoutKind.Sequential)]
  public class DungeonZoneDungeonChunk : ICsvParseable
  {
    public string NifName;

    public override string ToString()
    {
      return string.Format("[DungeonChunk] NifName="+NifName);
    }
  }
}

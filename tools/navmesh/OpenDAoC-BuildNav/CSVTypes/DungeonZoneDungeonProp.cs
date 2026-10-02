using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CEM.Client.CSVTypes
{
  /// <summary>Fixture from dungeon.prop</summary>
  [StructLayout(LayoutKind.Sequential)]
  public class DungeonZoneDungeonProp : ICsvParseable
  {
    public int ChunkIndex;
    public float X;
    public float Y;
    public float Z;
    public float Axis3D;
    public float AxisX;
    public float AxisY;
    public float AxisZ;
    public int Unknown;
    public int ParentID;

    // unknowns

    [NonSerialized]
    public DungeonZoneDungeonChunk Chunk;


    [NonSerialized]
    public DungeonZoneDungeonPlace Parent;

    public override string ToString()
    {
      return string.Format("[DungeonProp] chunkIndex="+ChunkIndex+" X="+X+" Y="+Y+" Z="+Z+" ParentID="+ParentID);
    }
  }
}

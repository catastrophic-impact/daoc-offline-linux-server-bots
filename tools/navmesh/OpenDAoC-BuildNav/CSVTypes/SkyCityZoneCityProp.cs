using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CEM.Client.CSVTypes
{
  /// <summary>Fixture from dungeon.prop</summary>
  [StructLayout(LayoutKind.Sequential)]
  public class SkyCityZoneCityProp : ICsvParseable
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
    public SkyCityZoneCityChunk Chunk;


    [NonSerialized]
    public SkyCityZoneCityPlace Parent;

    public override string ToString()
    {
      return string.Format("[SkyCityProp] chunkIndex="+ChunkIndex+" X="+X+" Y="+Y+" Z="+Z+" chunk="+Chunk+" parent="+Parent+" parentID="+ParentID);
    }
  }
}

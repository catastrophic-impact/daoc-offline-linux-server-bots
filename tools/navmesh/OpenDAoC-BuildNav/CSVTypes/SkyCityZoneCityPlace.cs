using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CEM.Client.CSVTypes
{
  /// <summary>skycity.place</summary>
  [StructLayout(LayoutKind.Sequential)]
  public class SkyCityZoneCityPlace : ICsvParseable
  {
    public int ChunkIndex;
    public float X;
    public float Y;
    public float Z;
    public float Axis3D;
    public float AxisX;
    public float AxisY;
    public float AxisZ;

    // unknowns

    [NonSerialized]
    public SkyCityZoneCityChunk Chunk;

    public override string ToString()
    {
      return string.Format("[SkyCityPlace] ChunkIndex="+ChunkIndex+" X="+X+" Y="+Y+" Z="+Z+" chunk="+Chunk);
    }
  }
}

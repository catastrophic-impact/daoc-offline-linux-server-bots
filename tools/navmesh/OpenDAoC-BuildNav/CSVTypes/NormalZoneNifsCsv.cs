using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CEM.Client.CSVTypes
{
  /// <summary>NIF from nifs.csv</summary>
  [StructLayout(LayoutKind.Sequential)]
  public class NormalZoneNifsCsv : ICsvParseable
  {
    // Grid Nifs,,,Ambient,Merlin Data
    // NIF,Textual Name,Filename,Only,Shadow,Color,Animate,Collide,Ground,MinAngle,MaxAngle,MinScale,MaxScale,Radius,LOD 1,LOD 2,LOD 3,LOD 4,Ref Height,Ref Width,Unique,Local,Terrain
    // 401,Farm House,bfarmhouse.nif,0,0,16711935,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0
    // 402,Guard tower,b-rndtw2.nif,0,0,16711935,0,1,0,0,0,100,100,0,0,0,0,0,1024,768,0,0,0
    public int ID;
    public string TextualName;
    public string FileName;
    public bool AmbientOnly;
    public int MerlinDataShadow;
    public int Color;
    public bool Animate;
    public int Collide;
    public bool OnGround;
    public float MinAngle;
    public float MaxAngle;
    public float MinScale;
    public float MaxScale;
    public float CollideRadius;
    public int LOD1;
    public int LOD2;
    public int LOD3;
    public int LOD4;
    public float RefHeight;
    public float RefWidth;
    public bool Unique;
    public bool Local;
    public bool Terrain;

    public override string ToString()
    {
      return string.Format("[Nif] {0}: {1}", ID, FileName);
    }
  }
}

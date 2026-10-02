using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CEM.Client.CSVTypes
{
  /// <summary>Fixture from fixtures.csv</summary>
  [StructLayout(LayoutKind.Sequential)]
  public class NormalZoneFixturesCsv : ICsvParseable
  {
    // Fixtures,,,,,,,,NIF,Collide,,On
    // ID,NIF #,Textual Name,X,Y,Z,A,Scale,Collide,Radius,Animate,Ground,Flip,Cave,Unique ID,3D Angle,3D Axis X,3D Axis Y,3D Axis Z
    // 1,406,Keep,25344.00,47104.00,2896.00,0,100,5120,0,0,1,0,0,1,0.000000,1.000000,0.000000,0.000000
    // 2,407,Circle,51392.00,46912.00,3192.00,0,100,1,0,0,0,0,0,2,0.000000,1.000000,0.000000,0.000000
    public int ID;
    public int NifID;
    public string TextualName;
    public float X;
    public float Y;
    public float Z;
    public float Angle;
    public float Scale;
    public bool NIFCollide;
    public float CollisionRadius;
    public bool Animate;
    public bool OnGround;
    public bool Flip;
    public bool Cave;
    public int UniqueID;
    public float Angle3D = float.MinValue;
    public float AxisX = float.MinValue;
    public float AxisY = float.MinValue;
    public float AxisZ = float.MinValue;


    [NonSerialized]
    public NormalZoneNifsCsv Nif;

    public override string ToString()
    {
      return string.Format("[Fixture] ID="+ID+" NifID="+NifID+" Name="+TextualName);
    }
  }
}

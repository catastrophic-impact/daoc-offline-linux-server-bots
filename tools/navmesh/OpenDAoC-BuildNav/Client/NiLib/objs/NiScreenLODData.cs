using System.IO;
using OpenTK;

namespace MNL {
  public class NiScreenLODData : NiLODData {
    public Vector3 BoundCenter;
    public float BoundRadius;
    public Vector3 WorldCenter;
    public float WorldRadius;
    public float[] ProportionLevels;


    public NiScreenLODData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      BoundCenter = reader.ReadVector3();
      BoundRadius = reader.ReadSingle();
      WorldCenter = reader.ReadVector3();
      WorldRadius = reader.ReadSingle();
      var propCount = reader.ReadUInt32();
      ProportionLevels = new float[propCount];
      for (var i = 0; i < propCount; i++) {
        ProportionLevels[i] = reader.ReadSingle();
      }

    }
  }
}
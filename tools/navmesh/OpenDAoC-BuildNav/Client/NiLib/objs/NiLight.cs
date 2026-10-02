using System.IO;
using OpenTK;

namespace MNL {
  public class NiLight : NiDynamicEffect {
    public float Dimmer;
    public Vector3 AmbientColor;
    public Vector3 DiffuseColor;
    public Vector3 SpecularColor;

    public NiLight(NiFile file, BinaryReader reader)
        : base(file, reader) {
      Dimmer = reader.ReadSingle();
      AmbientColor = reader.ReadColor3();
      DiffuseColor = reader.ReadColor3();
      SpecularColor = reader.ReadColor3();
    }
  }
}

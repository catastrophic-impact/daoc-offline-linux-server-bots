using System.IO;

namespace MNL {
  public class NiTriBasedGeometry : NiGeometry {

    // helpers for rendering
    public CEM.Graphic.NiMesh Mesh;
    public OpenTK.Matrix4 Transform;

    public NiTriBasedGeometry(NiFile file, BinaryReader reader)
        : base(file, reader) {

    }
  }
}

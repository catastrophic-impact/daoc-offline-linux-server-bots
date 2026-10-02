using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class RenderState { }
public class BoundingBox
{
  public BoundingBox(object a, object b) { }
}

public static class Extensions
{
  public static CEM.Datastructures.Vector2 ToVector2(this OpenTK.Vector3 v)
  {
    return new CEM.Datastructures.Vector2(v.X, v.Y);
  }
}

namespace CEM.Graphic
{
  public class NiMesh {
    public static void Begin() { }
    public void BeginRender() { }
    public void Render() { }
    public void EndRender() { }
    public void End() { }
  }
  public class NiTriStripsMesh : NiMesh
  {
    public NiTriStripsMesh(object o) { }
  }
  public class NiTriShapeMesh : NiMesh
  {
    public NiTriShapeMesh(object o) { }
  }
}

namespace CEM.Graphic.Geometry
{
  public class Empty { }
}
namespace CEM.Graphic.Rendering
{
  public class Empty { }
}
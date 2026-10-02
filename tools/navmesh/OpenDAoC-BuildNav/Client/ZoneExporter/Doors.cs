using System.Text.RegularExpressions;
using Emgu.CV;
using Emgu.CV.Structure;
using MNL;
using OpenTK;

namespace CEM.Client.ZoneExporter
{
  /// <summary>
  /// Doors
  /// </summary>
  partial class Zone2Obj
  {
    private static readonly Regex[] DoorRegex = [new("^door([0-9])+")];

    private void ExtractDoor(NiFile model, Matrix4 worldMatrix, int fixtureid)
    {
      // take everything that is under a doorX node and use it for the hull...
      if (model == null) return;

      var doorVertices = new Dictionary<string, List<Vector3>>();

      // find all trimeshs and tristrips
      foreach (var obj in model.ObjectsByRef.Values)
      {
        var avNode = obj as NiAVObject;
        if (avNode == null) continue;

        if (!IsMatched(avNode, new[] { "visible", "collidee", "collide" }))
          continue;

        var doorName = FindMatchRegex(avNode, DoorRegex);

        if (doorName == string.Empty) continue;

        Vector3[] vertices = null;
        Triangle[] triangles = null;

        TryExtractTriShape(obj, worldMatrix, false, false, ref vertices, ref triangles);

        if (vertices == null)
        {
          TryExtractTriStrips(obj, worldMatrix, false, false, ref vertices, ref triangles);
        }

        if (vertices == null)
          continue;

        if (!doorVertices.ContainsKey(doorName))
        {
          doorVertices.Add(doorName, new List<Vector3>());
        }
        doorVertices[doorName].AddRange(vertices);
      }


      foreach (var key in doorVertices.Keys)
      {
        float hullMinZ = float.MaxValue;
        float hullMaxZ = float.MinValue;

        var verts = doorVertices[key].ToArray();

        var pts = new List<PointF>();
        foreach (Vector3 vert in verts)
        {
          hullMinZ = Math.Min(vert.Z, hullMinZ);
          hullMaxZ = Math.Max(vert.Z, hullMaxZ);
          pts.Add(new PointF(vert.X, vert.Y));
        }

        RotatedRect box = CvInvoke.MinAreaRect(pts.ToArray());

        float maxSize = box.Size.Width;
        maxSize = Math.Max(maxSize, box.Size.Height);
        maxSize = Math.Max(maxSize, hullMaxZ - hullMinZ);

        // There are some weird door ids in e.g. Jordheim (z120): "door01:0" e.g. -- how do they translate to IDs?
        var doorID = Regex.Match(key.Replace(":", ""), "([0-9]+)").Groups[1].Value;
        var heading = ((box.Angle + (box.Size.Width < box.Size.Height ? 0.0 : 90.0 + 90.0)) * DEGREES_TO_HEADING) % 0x1000;
        if (heading < 0)
        {
          heading += 0x1000;
        }
        DoorWriter.WriteDoor(Zone.ID * 1000000 + fixtureid * 100 + int.Parse(doorID), model.FileName, (int)box.Center.X, (int)box.Center.Y, (int)hullMinZ, (int)heading, (int)maxSize);


        // Make sure we have a min of 20f for doors on width/height
        box.Size.Width = Math.Max(20f, box.Size.Width);
        box.Size.Height = Math.Max(20f, box.Size.Height);

        // Make sure the door touches the ground...
        hullMinZ -= 16.0f;

        var boxVertices = box.GetVertices();
        GeomSetWriter.WriteConvexVolume(boxVertices.Length, hullMinZ, hullMaxZ, CEM.GeomSetWriter.eAreas.Door);

        foreach (var vert in boxVertices)
          GeomSetWriter.WriteConvexVolumeVertex(new Vector3(vert.X, vert.Y, hullMinZ)); // debug
      }
    }
  }
}

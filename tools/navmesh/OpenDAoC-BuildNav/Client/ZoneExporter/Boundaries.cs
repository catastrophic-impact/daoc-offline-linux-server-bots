using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CEM.Utils;
using MNL;
using OpenTK;

namespace CEM.Client.ZoneExporter
{
  /// <summary>
  /// Zone Boundaries
  /// </summary>
  partial class Zone2Obj
  {

    void ExportBounds()
    {
      // Parse bounds from file
      var stream = ClientData.FindCSV(Zone, "bound.csv");
      if (stream == null)
      {
        Log.Warn("No bound.csv found for zone id " + ZoneID);
        return;
      }

      List<List<Vector2>> zoneBounds = new List<List<Vector2>>();
      using (TextReader reader = new StreamReader(stream)) {
        string input;

        while ((input = reader.ReadLine()) != null)
        {
          if (input.Trim() == string.Empty) continue;
          string[] data = input.Split(',');
          var points = new List<Vector2>();

          // should be x, y, x, y, ...
          Debug.Assert(data.Length % 2 == 0);
          Debug.Assert(data.Length >= 6); // atleast 6 values needed (id, count, x1, y1, x2, y2)?

          var cnt = int.Parse(data[1]);

          for (int i = 2; i < cnt; i += 2)
          {
            points.Add(new Vector2(float.Parse(data[i + 0]) + Zone.XOffset, float.Parse(data[i + 1]) + Zone.YOffset));
          }

          zoneBounds.Add(points);
        }
      }

      // Generate walls
      float minZ = 0.0f;
      float maxZ = (Zone.OffsetMapScaleFactor * 255 + Zone.TerrainMapScaleFactor * 255) + 1000;
      foreach (var boundary in zoneBounds)
      {
        for (int i = 0; i < boundary.Count - 1; i++)
        {
          MakeBoundingQuad(boundary[i], boundary[i + 1], minZ, maxZ);
        }
      }
    }

    private void MakeBoundingQuad(Vector2 p1, Vector2 p2, float minZ, float maxZ)
    {
      var vertices = new Vector3[4];
      var triangles = new Triangle[2];

      // bottom left
      vertices[0] = new Vector3(p1.X, p1.Y, minZ);
      // top right
      vertices[1] = new Vector3(p2.X, p2.Y, maxZ);
      // top left
      vertices[2] = new Vector3(p1.X, p1.Y, maxZ);
      // bottom right
      vertices[3] = new Vector3(p2.X, p2.Y, minZ);

      triangles[0] = new Triangle(0, 1, 2);
      triangles[1] = new Triangle(0, 3, 1);

      ObjWriter.AddMesh(vertices, triangles);
    }
  }
}

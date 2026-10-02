using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CEM.Datastructures;
using MNL;
using OpenTK;

namespace CEM.Client {
  /// <summary>
  /// .obj
  /// </summary>
  internal class WavefrontObjFile {
    private readonly List<Triangle[]> _indices;
    private readonly List<OpenTK.Vector3[]> _meshs;

    internal WavefrontObjFile(string file) {
      Scale = 1.0f;
      _meshs = new List<Vector3[]>();
      _indices = new List<Triangle[]>();
      File = file;
    }

    /// <summary>
    /// File
    /// </summary>
    public string File { get; private set; }

    /// <summary>
    /// Scale of all meshes in this file
    /// </summary>
    public float Scale { get; set; }

    /// <summary>
    /// True if empty
    /// </summary>
    internal bool Empty {
      get { return _meshs.Count == 0; }
    }

    internal void AddMesh(OpenTK.Vector3[] vertices, Triangle[] indices) {
      if (vertices.SelectMany(v => new[] { v.X, v.Y, v.Z }).Any(x => float.IsNaN(x) || float.IsInfinity(x)))
        throw new InvalidDataException("Invalid mesh.");
      _meshs.Add(vertices);
      _indices.Add(indices);
    }

    internal void Save() {
      using (TextWriter writer = new StreamWriter(File, false)) {
        int offset = 1;
        for (int index = 0; index < _meshs.Count; index++) {
          Vector3[] m = _meshs[index];

          writer.WriteLine("g object " + index);
          foreach (var vSrc in m) {
            var v = new Vector3(vSrc.X*Scale, vSrc.Y*Scale, vSrc.Z*Scale);

            writer.WriteLine("v {0} {1} {2}", 
                             v.X.ToString(CultureInfo.InvariantCulture),
                             v.Z.ToString(CultureInfo.InvariantCulture),
                             v.Y.ToString(CultureInfo.InvariantCulture));
          }

          Triangle[] i = _indices[index];
          foreach (var t in i) {
            writer.WriteLine("f {0} {1} {2}", offset + t.X, offset + t.Y, offset + t.Z);
          }
          offset += _meshs[index].Length;
        }
        writer.Flush();
        writer.Close();
      }
    }
  }
}
using System.Globalization;
using System.Text.RegularExpressions;
using CEM.Utils;
using CEM.World;
using MNL;
using OpenTK;

namespace CEM.Client.ZoneExporter
{
    /// <summary>
    /// Converts a DAOC Zone into the .obj format
    /// [not threadsafe]
    /// </summary>
    internal sealed partial class Zone2Obj : IDisposable
    {
        /// <summary>
        /// Converts degrees into DAoC Headings
        /// </summary>
        public const double DEGREES_TO_HEADING = (4096.0 / 360.0);

        public Zone2Obj(Zone2 zone)
        {
            Zone = zone;
            if (!Directory.Exists("zones"))
                Directory.CreateDirectory("zones");
            string filename = Path.Combine("zones", string.Format("zone{0:D3}", zone.ID));
            ZoneFilePrefix = filename;
            DoorWriter = new DoorWriter(filename + ".doors");
            GeomSetWriter = new GeomSetWriter(filename + ".gset");
            ObjWriter = new WavefrontObjFile(filename + ".obj") { Scale = NavmeshMgr.CONVERSION_FACTOR };
            GeomSetWriter.WriteLoadMesh(filename + ".obj");
            LadderDefinitions = new();
        }

        private string ZoneFilePrefix { get; }

        /// <summary>
        /// Ladder instances collected during export (links placed after Recast pass 1).
        /// </summary>
        public List<LadderDefinition> LadderDefinitions { get; }

        /// <summary>
        /// Zone
        /// </summary>
        public Zone2 Zone { get; private set; }

        /// <summary>
        /// Zone ID
        /// </summary>
        public ushort ZoneID { get { return Zone.ID; } }

        /// <summary>
        /// Door writer
        /// </summary>
        private DoorWriter DoorWriter { get; set; }

        /// <summary>
        /// Geomset writer
        /// </summary>
        private GeomSetWriter GeomSetWriter { get; set; }

        /// <summary>
        /// .obj exporter
        /// </summary>
        private WavefrontObjFile ObjWriter { get; set; }

        public void Dispose()
        {
            if (ObjWriter == null)
                return;

            DoorWriter.Flush();
            DoorWriter.Dispose();

            if (GeomSetWriter.OffMeshConnectionCount > NavmeshMgr.RecastOffMeshConnectionLimit)
                Log.Warn($"Zone {Zone} wrote {GeomSetWriter.OffMeshConnectionCount} off-mesh connections but limit is {NavmeshMgr.RecastOffMeshConnectionLimit}.");
            else if (GeomSetWriter.OffMeshConnectionCount > 0)
                Log.Normal($"Zone {Zone} wrote {GeomSetWriter.OffMeshConnectionCount} off-mesh connections.");

            if (LadderDefinitions.Count > 0)
                LadderDefinitionWriter.Write($"{ZoneFilePrefix}.ladders.json", Zone.ID, LadderDefinitions);

            GeomSetWriter.Flush();
            GeomSetWriter.Dispose();
            ObjWriter.Save();
            if (ObjWriter.Empty)
                File.Delete(ObjWriter.File);
            ObjWriter = null;
        }

        private static readonly Vector3[] CylinderVertices = {
            new Vector3(0f, -0.4f, -0.1f),
            new Vector3(0f, -0.4f, 2f),
            new Vector3(0.380423f, -0.123607f, -0.1f),
            new Vector3(0.380423f, -0.123607f, 2f),
            new Vector3(0.235114f, 0.323607f, -0.1f),
            new Vector3(0.235114f, 0.323607f, 2f),
            new Vector3(-0.235114f, 0.323607f, -0.1f),
            new Vector3(-0.235114f, 0.323607f, 2f),
            new Vector3(-0.380423f, -0.123607f, -0.1f),
            new Vector3(-0.380423f, -0.123607f, 2f),
        };

        private static readonly Triangle[] CylinderIndices = {
            new Triangle(0, 1, 3),
            new Triangle(0, 3, 2),
            new Triangle(2, 3, 5),
            new Triangle(2, 5, 4),
            new Triangle(4, 5, 7),
            new Triangle(4, 7, 6),
            new Triangle(6, 7, 9),
            new Triangle(6, 9, 8),
            new Triangle(1, 0, 8),
            new Triangle(1, 8, 9),
         };

        /// <summary>
        /// Creates an object file of the specified zone
        /// </summary>
        public void Export()
        {
            Log.Normal("Zone {0} is a {1}", Zone.Name, Zone.Type);

            var proxyValues = ParseProxyFile();

            switch (Zone.Type)
            {
                default:
                    ExtractOutdoorZone();
                    break;
                case eZoneType.City:
                    ExtractCity();
                    break;
                case eZoneType.Dungeon:
                    ExtractDungeon(proxyValues);
                    break;
                case eZoneType.SkyCity:
                    ExtractSkyCity(proxyValues);
                    break;
            }

            Dispose(); // bit dirty
        }

        #region ExtractZone
        private void ExtractOutdoorZone()
        {
            // make heightmap meshes
            ExportHeightmap();

            // add all the nifs!
            ExportNifs();

            // add zone boundaries
            ExportBounds();
        }

        Dictionary<string, string> ParseProxyFile()
        {
            var proxyStream = ClientData.FindCSV(Zone, "nifproxy.csv");
            var proxyValues = new Dictionary<string, string>();
            if (proxyStream == null)
            {
                return proxyValues;
            }

            using (TextReader reader = new StreamReader(proxyStream))
            {
                string input;
                while ((input = reader.ReadLine()) != null)
                {
                    var values = input.Split(',');
                    if (values.Length != 3)
                        continue;
                    proxyValues.Add(values[0], values[1]); // values[2] is unknown?
                }
            }
            return proxyValues;
        }

        private void ExportNifs()
        {
            Dictionary<int, NiFile> nifs = ReadNifEntries();
            using TextReader fixtureCsv = new StreamReader(ClientData.FindCSV(Zone, "fixtures.csv"));
            fixtureCsv.ReadLine();
            fixtureCsv.ReadLine();

            string input;

            while ((input = fixtureCsv.ReadLine()) != null)
            {
                if (input.Trim() == string.Empty)
                    continue;
                string[] fixture = input.Split(',');
                int fixid = int.Parse(fixture[0], CultureInfo.InvariantCulture);
                int nifid = int.Parse(fixture[1], CultureInfo.InvariantCulture);
                string name = fixture[2];

                float x = float.Parse(fixture[3], CultureInfo.InvariantCulture);
                float y = float.Parse(fixture[4], CultureInfo.InvariantCulture);
                float z = float.Parse(fixture[5], CultureInfo.InvariantCulture);
                float a = (float) (short.Parse(fixture[6], CultureInfo.InvariantCulture) / 180f * Math.PI);
                float scale = float.Parse(fixture[7], CultureInfo.InvariantCulture);

                if (Math.Abs(scale - 0) > 0.00001f)
                    scale = (scale / 100f);
                else
                    scale = 1;

                bool collide = fixture[8] != "0";
                int radius = int.Parse(fixture[9], CultureInfo.InvariantCulture);
                bool ground = fixture[11] == "1";
                if (ground)
                {
                    z = Zone.GetNearestGround(x, y, z).Z;
                }

                bool flip = fixture[12] == "1";
                if (flip)
                {
                    throw new Exception("flipping is not implemented!");
                }

                int uniqueid = int.Parse(fixture[14]);

                float? a3d = null;
                float ax = float.MinValue;
                float ay = float.MinValue;
                float az = float.MinValue;

                if (fixture.Length > 16)
                {
                    a3d = float.Parse(fixture[15], CultureInfo.InvariantCulture);
                    ax = float.Parse(fixture[16], CultureInfo.InvariantCulture);
                    ay = float.Parse(fixture[17], CultureInfo.InvariantCulture);
                    az = float.Parse(fixture[18], CultureInfo.InvariantCulture);
                }

                Matrix4 worldMatrix = Matrix4.Identity;
                worldMatrix *= Matrix4.CreateScale(new Vector3(scale, -scale, scale));
                if (a3d != null)
                    worldMatrix *= Matrix4.CreateFromAxisAngle(new Vector3(ax, ay, -az), a3d.Value);
                else
                    worldMatrix *= Matrix4.CreateRotationZ(a);

                worldMatrix *= Matrix4.CreateTranslation(new Vector3(x, y, z) + Zone.OffsetVector);

                // There are many zones with keys that do not exist - Zone64 key 404 for example.
                if (!nifs.ContainsKey(nifid))
                {
                    Log.Debug("Fixture {0} with name '{1}' is missing NIF {2} ", fixid, name, nifid);
                    continue;
                }

                // Contrary to trees in World's End for example, Iarnwood and Mighty Oak don't seem to contain an invisible "collision box", despite having a radius of 0.
                // So the whole mesh is used, including branches, and this makes the navigation mesh particularly big.
                // To prevent that, we replace the mesh with a cylinder ourselves.
                // Maybe the nif contains info about this.
                if (name is "Iarnwood" or "Mighty Oak" or "Mighty Oak Smaller")
                    radius = 64;

                // Use a cylinder instead of the model if a radius is provided.
                if (radius != 0)
                {
                    AddCylinder(Matrix4.CreateScale(new Vector3(-1, 1, 1)) * worldMatrix, radius);
                }
                else if (collide)
                {
                    AddModelToObj(nifs[nifid], worldMatrix, ["collide", "collidee", "collision"], true, fixtureId: fixid);
                }

                ExtractDoor(nifs[nifid], worldMatrix, uniqueid == 0 ? fixid : uniqueid);
                ExtractLadder(nifs[nifid], worldMatrix);
            }
        }

        private Dictionary<int, NiFile> ReadNifEntries()
        {
            var result = new Dictionary<int, NiFile>();

            var str = ClientData.FindCSV(Zone, "nifs.csv");
            using (TextReader reader = new StreamReader(str))
            {
                reader.ReadLine();
                reader.ReadLine();

                string input;
                while ((input = reader.ReadLine()) != null)
                {
                    if (input.Trim() == string.Empty)
                        continue;
                    string[] data = input.Split(',');

                    NiFile model = null;
                    var id = int.Parse(data[0]);
                    string nif = data[2].Trim();

                    if (Program.NifIgnorelist.Contains(nif))
                    {
                        continue; // Skip invalid nifs
                    }

                    Stream stream = ClientData.FindNIF(Zone, nif);
                    if (stream != null)
                    {
                        using (var br = new BinaryReader(stream))
                            model = new NiFile(br, nif);

                        if (!model.Loaded)
                            continue;

                        result.Add(id, model);
                    }
                }
            }

            return result;
        }
        #endregion

        #region NIF Extraction
        private bool TryExtractTriShape(NiObject obj, Matrix4 worldMatrix, bool invertTris, bool both, ref Vector3[] vertices, ref Triangle[] outtriangles)
        {
            var shape = obj as NiTriShape;
            if (shape == null)
                return false;

            eFaceDrawMode mode = FindDrawMode(shape);
            if (mode == eFaceDrawMode.DRAW_CCW_OR_BOTH)
                mode = both ? eFaceDrawMode.DRAW_BOTH : eFaceDrawMode.DRAW_CCW;

            Matrix4 myMatrix = ComputeWorldMatrix(shape) * worldMatrix;

            var data = shape.Data.Object as NiTriShapeData;
            if (data.Triangles.Length == 0)
                return false;

            vertices = new Vector3[data.Vertices.Length];

            for (int i = 0; i < data.Vertices.Length; i++)
            {
                vertices[i] = Vector3.Transform(data.Vertices[i], myMatrix);
            }

            var triangles = new List<Triangle>();

            // CCW
            if (mode == eFaceDrawMode.DRAW_BOTH || (mode == eFaceDrawMode.DRAW_CCW) != invertTris)
            {
                for (int i = 0; i < data.Triangles.Length; i++)
                    triangles.Add(new Triangle(data.Triangles[i].Z, data.Triangles[i].Y, data.Triangles[i].X));
            }

            // CC
            if (mode == eFaceDrawMode.DRAW_BOTH || (mode == eFaceDrawMode.DRAW_CW) != invertTris)
            {
                triangles.AddRange(data.Triangles);
            }

            outtriangles = triangles.ToArray();
            return true;
        }

        private bool TryExtractTriStrips(NiObject obj, Matrix4 worldMatrix, bool invertTris, bool both, ref Vector3[] vertices, ref Triangle[] outtriangles)
        {
            var strips = obj as NiTriStrips;
            if (strips == null)
                return false;

            eFaceDrawMode mode = FindDrawMode(strips);
            if (mode == eFaceDrawMode.DRAW_CCW_OR_BOTH)
                mode = both ? eFaceDrawMode.DRAW_BOTH : eFaceDrawMode.DRAW_CCW;

            Matrix4 myMatrix = ComputeWorldMatrix(strips) * worldMatrix;

            var data = strips.Data.Object as NiTriStripsData;

            if (data.Vertices.Length == 0)
                return false;

            vertices = new Vector3[data.Vertices.Length];

            for (int i = 0; i < data.Vertices.Length; i++)
            {
                vertices[i] = Vector3.Transform(data.Vertices[i], myMatrix);
            }

            var triangles = new List<Triangle>();
            foreach (var points in data.Points)
            {
                // CCW
                if (mode == eFaceDrawMode.DRAW_BOTH || (mode == eFaceDrawMode.DRAW_CCW) != invertTris)
                {
                    ushort a = points[0];
                    ushort b = points[0];
                    ushort c = points[1];
                    bool flip = false;
                    for (int s = 2; s < points.Length; s++)
                    {
                        a = b;
                        b = c;
                        c = points[s];
                        if (a != b && b != c && c != a)
                        {
                            triangles.Add(!flip ? new Triangle(a, c, b) : new Triangle(a, b, c));
                        }
                        flip = !flip;
                    }
                }

                // CC
                if (mode == eFaceDrawMode.DRAW_BOTH || (mode == eFaceDrawMode.DRAW_CW) != invertTris)
                {
                    ushort a = points[0];
                    ushort b = points[0];
                    ushort c = points[1];
                    bool flip = false;
                    for (int s = 2; s < points.Length; s++)
                    {
                        a = b;
                        b = c;
                        c = points[s];
                        if (a != b && b != c && c != a)
                        {
                            triangles.Add(!flip ? new Triangle(a, b, c) : new Triangle(a, c, b));
                        }
                        flip = !flip;
                    }
                }
            }

            outtriangles = triangles.ToArray();
            return true;
        }

        /// <summary>
        /// Average of vectors
        /// </summary>
        /// <param name="vecs"></param>
        /// <returns></returns>
        private Vector3 Average(IEnumerable<Vector3> vecs)
        {
            Vector3 sum = Vector3.Zero;
            int cnt = 0;
            foreach (var v in vecs)
            {
                cnt++;
                sum += v;
            }
            return sum / (cnt == 0 ? 1f : cnt);
        }

        private static string[] _collisionsMask = ["collisionswitch", "tree coll"];

        private void AddModelToObj(NiFile model, Matrix4 worldMatrix, string[] filter, bool invertTris = false, int fixtureId = 0, bool both = false)
        {
            if (model == null)
            {
                return;
            }

            // This is mainly to handle "tree coll", which is also a "collisionswitch". If it's defined, we need to ignore other "collisionswitch".
            // This specifically allows the built-in collision "cylinders" around big trees in World's End and a few other areas, while ignoring the tree itself.
            // There has to be a simple way to figure out this.
            List<string> collisionMask = null;

            foreach (var obj in model.ObjectsByRef.Values)
            {
                var netObj = obj as NiObjectNET;

                if (netObj == null)
                    continue;

                for (int i = 0; i < _collisionsMask.Length; i++)
                {
                    if (netObj.Name.Value.Equals(_collisionsMask[i], StringComparison.OrdinalIgnoreCase))
                    {
                        collisionMask ??= new(1);

                        if (collisionMask.Count < i + 1)
                            collisionMask.Add(_collisionsMask[i]);
                    }
                }
            }

            // find all trimeshs and tristrips
            bool foundNode = false;
            foreach (var obj in model.ObjectsByRef.Values)
            {
                var avNode = obj as NiAVObject;
                if (avNode == null)
                    continue;

                if (collisionMask != null)
                {
                    if (!IsMatched(avNode, collisionMask.ToArray(), true))
                        continue;
                }

                // Ignore LoD stuff and shadowcaster I guess.
                if (IsMatched(avNode, ["!LoD_cullme", "shadowcaster", "far"]))
                    continue;

                if (filter.Length > 0)
                {
                    // filter doors
                    if (!IsMatched(avNode, filter) || FindMatchRegex(avNode, DoorRegex) != string.Empty)
                        continue;
                }

                foundNode = true;

                Vector3[] vertices = null;
                Triangle[] triangles = null;

                var foundMesh = TryExtractTriShape(obj, worldMatrix, invertTris, both, ref vertices, ref triangles);
                if (!foundMesh)
                {
                    foundMesh = TryExtractTriStrips(obj, worldMatrix, invertTris, both, ref vertices, ref triangles);
                }

                if (!foundMesh)
                    continue;

                var filteredTriangles = new List<Triangle>(triangles.Length);
                foreach (var tri in triangles)
                {
                    Vector3 v1 = vertices[tri.X];
                    Vector3 v2 = vertices[tri.Y];
                    Vector3 v3 = vertices[tri.Z];

                    float waterLevel = GetWaterLevelAt(v1.X, v1.Y);
                    if (waterLevel == float.MinValue)
                    {
                        filteredTriangles.Add(tri);
                        continue;
                    }

                    float cullZ = waterLevel - UNDERWATER_CULL_DEPTH;
                    if (v1.Z < cullZ && v2.Z < cullZ && v3.Z < cullZ)
                    {
                        continue;
                    }

                    filteredTriangles.Add(tri);
                }

                if (filteredTriangles.Count == 0)
                {
                    continue;
                }

                try
                {
                    ObjWriter.AddMesh(vertices, filteredTriangles.ToArray());
                }
                catch (InvalidDataException)
                {
                    Log.Error($"Invalid mesh, skipping. (File: {model.FileName})");
                    continue;
                }
            }

            var f = filter.FirstOrDefault();
            if (!foundNode && f == "pickee")
                AddModelToObj(model, worldMatrix, new[] { "collide", "collidee", "collision" }, invertTris, both: both);
            else if (!foundNode && f == "collide")
                AddModelToObj(model, worldMatrix, new[] { "visible" }, invertTris, both: both);
            else if (!foundNode && f == "visible")
                AddModelToObj(model, worldMatrix, new string[0], invertTris, both: both); // use whole mesh
            else if (!foundNode)
            {
                if (!Program.NifIgnorelist.Contains(model.FileName))
                {
                    Log.Error("Did not find collision node for fixture with id=" + fixtureId + " (nif=" + model.FileName + " nodes=" + model.ObjectsByRef.Values.Count + ", nifPtr=" + model.GetHashCode() + ")");
                    throw new ArgumentException(model.FileName);
                }
            }
        }


        /// <summary>
        /// Flattens the set of transformation matrixes in the current tree path into a single World Matrix.
        /// </summary>
        private Matrix4 ComputeWorldMatrix(NiAVObject obj)
        {
            var pathToRoot = new List<NiAVObject>();
            NiAVObject current = obj;
            while (current != null)
            {
                pathToRoot.Add(current);
                current = current.Parent;
            }
            var worldMatrix = Matrix4.Identity;
            foreach (NiAVObject node in pathToRoot)
            {
                worldMatrix *= node.Rotation;
                worldMatrix *= Matrix4.CreateScale(node.Scale, node.Scale, node.Scale);
                worldMatrix *= Matrix4.CreateTranslation(node.Translation.X, node.Translation.Y, node.Translation.Z);
            }

            return worldMatrix;
        }

        /// <summary>
        /// True if the current tree matches any or all of the specified filters
        /// </summary>
        private bool IsMatched(NiAVObject obj, string[] filters, bool all = false)
        {
            int matchCount = 0;
            NiAVObject current = obj;

            while (current != null)
            {
                if (all)
                {
                    if (filters.Any(filter => string.IsNullOrEmpty(filter) || current.Name.Value.StartsWith(filter, StringComparison.OrdinalIgnoreCase)))
                    {
                        matchCount++;
                        current = current.Parent;
                        continue;
                    }
                }
                else
                {
                    if (filters.Any(filter => string.IsNullOrEmpty(filter) || current.Name.Value.StartsWith(filter, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }

                current = current.Parent;
            }

            return matchCount == filters.Length;
        }

        /// <summary>
        /// True if the current tree matches any of the specified regexes. Scans the tree towards the root
        /// and returns the first matching node name.
        /// </summary>
        private string FindMatchRegex(NiAVObject obj, Regex[] filter)
        {
            // Check if matched
            NiAVObject current = obj;
            current = obj;
            while (current != null)
            {
                if (filter.Any(x => x.IsMatch(current.Name.Value.ToLowerInvariant())))
                    return current.Name.Value;
                current = current.Parent;
            }

            return string.Empty;
        }

        /// <summary>
        /// Scans the NIFTree towards the root and finds the next FaceDrawMode.
        /// </summary>
        private eFaceDrawMode FindDrawMode(NiAVObject obj)
        {
            NiAVObject current = obj;
            while (current != null)
            {
                foreach (var propRef in current.Properties)
                {
                    var sprop = propRef.Object as NiStencilProperty;
                    if (sprop == null)
                        continue;
                    return sprop.FaceDrawMode;
                }

                current = current.Parent;
            }

            return eFaceDrawMode.DRAW_CCW_OR_BOTH;
        }

        /// <summary>
        /// Adds a 2D Cylinder to the ObjectWriter
        /// </summary>
        private void AddCylinder(Matrix4 worldMatrix, int scale)
        {
            var vertices = new Vector3[CylinderVertices.Length];

            Matrix4 myWorldMatrix = Matrix4.Identity;
            myWorldMatrix *= Matrix4.CreateScale(10, 10, 50);
            myWorldMatrix *= Matrix4.CreateScale(scale, scale, scale);
            myWorldMatrix = myWorldMatrix * worldMatrix;

            for (int i = 0; i < CylinderVertices.Length; i++)
            {
                vertices[i] = Vector3.Transform(CylinderVertices[i], myWorldMatrix);
            }

            ObjWriter.AddMesh(vertices, CylinderIndices);
        }
        #endregion
    }
}

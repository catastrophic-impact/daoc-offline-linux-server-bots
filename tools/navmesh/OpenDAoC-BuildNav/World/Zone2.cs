using System.Globalization;
using CEM.Client;
using CEM.Datastructures;
using CEM.Utils;
using OpenTK;

namespace CEM.World
{
    /// <summary>
    /// Single zone
    /// </summary>
    internal class Zone2
    {
        private HeightMap _heightmap;
        private int? _offsetScaleFactor;
        private int? _terrainScaleFactor;
        private List<List<Vector3>> _rivers;
        private int[] _waterHeights;

        private Zone2(Region region, ushort id, string name, int xoffset, int yoffset, int width, int height, eZoneType type, ushort proxyZone)
        {
            Region = region;
            ID = id;
            Name = name.Trim();
            XOffset = xoffset;
            YOffset = yoffset;
            Width = width;
            Height = height;
            Type = type;
            OffsetVector = new Vector3(XOffset, YOffset, 0);
            ProxyZone = proxyZone;
        }

        /// <summary>
        /// Loads all zones and associates them with the appropriate regions
        /// </summary>
        /// <param name="regions"></param>
        /// <returns></returns>
        public static IEnumerable<Zone2> LoadZones(Dictionary<ushort, Region> regions)
        {
            IniFile ini;
            using (Stream fs = ClientData.ZonesDat)
                ini = new IniFile(fs);

            // Load Zones
            var newZones = from entry in ini.Topics
                           where entry.Name.StartsWith("zone") && entry.Items["enabled"] != "0"
                           select new { ID = ushort.Parse(entry.Name.Substring("zone".Length)), Data = entry.Items };
            int zones = 0;
            foreach (var entry in newZones)
            {
                ushort regionID = ushort.Parse(entry.Data["region"]);

                if (!regions.ContainsKey(regionID))
                    regions.Add(regionID, new Region(regionID, "")); // some zones miss region entries
                Region region = regions[regionID];

                ushort zoneID = entry.ID;
                string name = entry.Data["name"];
                ushort proxy = 0;
                if (entry.Data.ContainsKey("proxy_zone"))
                {
                    proxy = ushort.Parse(entry.Data["proxy_zone"]);
                }
                int w = int.Parse(entry.Data["width"]) * 8192;
                int h = int.Parse(entry.Data["height"]) * 8192;
                int xoff = int.Parse(entry.Data["region_offset_x"]) * 8192;
                int yoff = int.Parse(entry.Data["region_offset_y"]) * 8192;
                var type = (eZoneType) int.Parse(entry.Data.ContainsKey("type") ? entry.Data["type"] : "0");
                var zone = new Zone2(region, zoneID, name, xoff, yoff, w, h, type, proxy);
                region.Add(zoneID, zone);
                yield return zone;
                zones++;
            }
            Log.Normal("Loaded {0} zones in {1} regions!", zones, regions.Count);
        }

        /// <summary>
        /// Region to which this zone belongs
        /// </summary>
        public Region Region { get; private set; }

        /// <summary>
        /// Zone Height
        /// </summary>
        public int Height { get; private set; }

        /// <summary>
        /// Zone Width
        /// </summary>
        public int Width { get; private set; }

        /// <summary>
        /// Zone ID
        /// </summary>
        public ushort ID { get; private set; }

        /// <summary>
        /// Proxy Zone for this zone
        /// </summary>
        public ushort ProxyZone { get; private set; }

        /// <summary>
        /// Zone Name
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Zone X Offset
        /// </summary>
        public int XOffset { get; private set; }

        /// <summary>
        /// Zone Y Offset
        /// </summary>
        public int YOffset { get; private set; }

        /// <summary>
        /// Offset Vector
        /// </summary>
        public Vector3 OffsetVector { get; private set; }

        /// <summary>
        /// Offset Vector
        /// </summary>
        public CEM.Datastructures.Vector2 OffsetVector2 { get { return OffsetVector.ToVector2(); } }

        /// <summary>
        /// Returns the axis-aligned rectangle describing this zone
        /// </summary>
        public AxisAlignedRectangle Rectangle
        {
            get
            {
                var topLeft = OffsetVector.ToVector2();
                return new AxisAlignedRectangle(topLeft, topLeft + new CEM.Datastructures.Vector2(Width, Height));
            }
        }

        /// <summary>
        /// Zone Type
        /// </summary>
        public eZoneType Type { get; set; }

        /// <summary>
        /// True if this zone contains the specified vector
        /// </summary>
        /// <param name="vec"></param>
        /// <returns></returns>
        public bool Contains(Vector3 vec)
        {
            return vec.X >= XOffset && vec.Y >= YOffset &&
                   vec.X <= XOffset + Width && vec.Y <= YOffset + Height;
        }

        /// <summary>
        /// Zone-Loc -> Global Loc
        /// </summary>
        public Vector3 GetGlobalPosition(float x, float y, float z)
        {
            return new Vector3(x + XOffset, y + YOffset, z);
        }

        /// <summary>
        /// Global loc -> Zone loc
        /// </summary>
        /// <param name="newPosition"></param>
        /// <returns></returns>
        public Vector3 GetZonePosition(Vector3 newPosition)
        {
            return newPosition - OffsetVector;
        }

        /// <summary>
        /// True if this zone contains the point
        /// </summary>
        public bool Contains(Datastructures.IPoint3D vec)
        {
            return Contains(vec.Position);
        }

        public override string ToString()
        {
            return string.Format("{0} [Z{1:D3}/{2}]", Name, ID, Region);
        }

        /// <summary>
        /// Finds the nearest possible ground below the current inputZ
        /// </summary>
        /// <param name="zoneX"></param>
        /// <param name="zoneY"></param>
        /// <param name="inputZ"></param>
        /// <returns></returns>
        public Vector3 GetNearestGround(float zoneX, float zoneY, float inputZ)
        {
            if (HasHeightmap)
            {
                const int maxHmapDelta = 256; /* z distance in which to stick to the ground */
                ushort hmap = Heightmap[zoneX, zoneY];
                if (inputZ <= hmap || inputZ - hmap <= maxHmapDelta)
                    inputZ = (ushort) (hmap - 1); // small offset to prevent flying
                else
                    inputZ -= 2; // "gravity"
            }

            return new Vector3(zoneX, zoneY, inputZ > 0 ? inputZ : 3000);
        }

        /// <summary>
        /// Fins the nearest possible ground below the current z
        /// </summary>
        /// <param name="zonePosition"></param>
        /// <returns></returns>
        public Vector3 GetNearestGround(Vector3 zonePosition)
        {
            return GetNearestGround(zonePosition.X, zonePosition.Y, zonePosition.Z);
        }

        /// <summary>
        /// Fins the nearest possible ground below the current z
        /// </summary>
        /// <param name="zonePosition"></param>
        /// <returns></returns>
        public Vector3 GetNearestGround(CEM.Datastructures.Vector2 zonePosition)
        {
            return GetNearestGround(zonePosition.X, zonePosition.Y, 0);
        }

        #region Recast Pathing

        /// <summary>
        /// Zone data .obj file
        /// </summary>
        public string ObjFile
        {
            get { return IO.MakePath("zones", string.Format("zone{0:D3}.obj", ID)); }
        }

        /// <summary>
        /// Zone data .nav file
        /// </summary>
        public string NavFile
        {
            get { return IO.MakePath("zones", string.Format("zone{0:D3}.nav", ID)); }
        }

        #endregion

        #region Heightmaps

        /// <summary>
        /// True if this zone has a heightmap
        /// </summary>
        public bool HasHeightmap
        {
            get { return Type == eZoneType.Normal; }
        }

        /// <summary>
        /// Heightmap
        /// Lazily loaded.
        /// </summary>
        public HeightMap Heightmap
        {
            get { return !HasHeightmap ? null : (_heightmap ?? (_heightmap = new HeightMap(this))); }
        }

        /// <summary>
        /// Terrain Map scale factor
        /// </summary>
        public int TerrainMapScaleFactor
        {
            get
            {
                if (_terrainScaleFactor == null)
                    LoadSectorDat();
                return _terrainScaleFactor ?? 0;
            }
        }

        /// <summary>
        /// Offset Map scale factor
        /// </summary>
        public int OffsetMapScaleFactor
        {
            get
            {
                if (_offsetScaleFactor == null)
                    LoadSectorDat();
                return _offsetScaleFactor ?? 0;
            }
        }

        /// <summary>
        /// Opens a file in the zoneXXX/datXXX.mpk/
        /// </summary>
        public Stream OpenDatFile(string file)
        {
            string path = string.Format("zones/zone{0:D3}/dat{0:D3}.mpk/{1}", ID, file);
            return ClientData.OpenFirst(ClientData.BuildPathPermutations(ClientData.AddonPaths(this), new[] { path }));
        }

        /// <summary>
        /// Loads the terrain map
        /// </summary>
        /// <returns></returns>
        internal byte[,] LoadTerrainMap()
        {
            return PCXImage.Read(OpenDatFile("terrain.pcx"));
        }

        /// <summary>
        /// Loads the offset map
        /// </summary>
        /// <returns></returns>
        internal byte[,] LoadOffsetMap()
        {
            return PCXImage.Read(OpenDatFile("offset.pcx"));
        }

        /// <summary>
        /// Loads water map
        /// </summary>
        /// <returns></returns>
        internal byte[,] LoadWaterMap()
        {
            return PCXImage.Read(OpenDatFile("water.pcx"));
        }

        internal List<List<Vector3>> GetRiverPoints()
        {
            return _rivers;
        }

        internal int[] GetWaterHeights()
        {
            return _waterHeights;
        }

        private void LoadSectorDat()
        {
            int terFac = 0;
            int offFac = 0;
            List<List<Vector3>> rivers = new List<List<Vector3>>();
            int[] waterHeights = new int[0];

            // Load sector.dat
            using (Stream fs = OpenDatFile("sector.dat"))
            {
                if (fs != null)
                {
                    var ini = new IniFile(fs);
                    IniFile.Topic terrain = ini["terrain"];
                    if (terrain != null)
                    {
                        terFac = int.Parse(terrain["scalefactor"] ?? "0");
                        offFac = int.Parse(terrain["offsetfactor"] ?? "0");
                    }
                    IniFile.Topic waterDefs = ini["waterdefs"];
                    if (waterDefs != null)
                    {
                        int numWater = int.Parse(waterDefs["num"] ?? "0");
                        waterHeights = new int[numWater];
                        for (int i = 0; i < numWater; i++)
                        {
                            IniFile.Topic riverSection = ini["river" + i.ToString("D2")];
                            if (riverSection != null)
                            {
                                List<Vector3> bankPoints = new List<Vector3>();
                                waterHeights[i] = int.Parse(riverSection["height"] ?? "0");
                                int points = int.Parse(riverSection["bankpoints"] ?? "0");

                                for (int p = 0; p < points; p++)
                                {
                                    float z = waterHeights[i] * NavmeshMgr.CONVERSION_FACTOR;
                                    string[] leftData = riverSection["left" + p.ToString("D2")].Split(',');
                                    float lx = (float.Parse(leftData[0], CultureInfo.InvariantCulture) * 256 + this.XOffset);
                                    float ly = (float.Parse(leftData[1], CultureInfo.InvariantCulture) * 256 + this.YOffset);

                                    string[] rightData = riverSection["right" + p.ToString("D2")].Split(',');
                                    float rx = (float.Parse(rightData[0], CultureInfo.InvariantCulture) * 256 + this.XOffset);
                                    float ry = (float.Parse(rightData[1], CultureInfo.InvariantCulture) * 256 + this.YOffset);

                                    bankPoints.Add(new Vector3(lx, ly, z));
                                    bankPoints.Add(new Vector3(rx, ry, z));
                                }

                                rivers.Add(bankPoints);
                            }
                        }
                    }
                }
            }
            _waterHeights = waterHeights;
            _rivers = rivers;
            _terrainScaleFactor = terFac;
            _offsetScaleFactor = offFac;
        }

        #endregion
    }

    public enum eZoneType
    {
        Normal = 0,
        City = 1,
        Dungeon = 2,
        SkyCity = 4,
    }
}

using CEM.Utils;
using MNL;
using OpenTK;

namespace CEM.Client.ZoneExporter
{
    /// <summary>
    /// HeightMap
    /// </summary>
    partial class Zone2Obj
    {
        private byte[,] _waterMap;
        private int[] _waterHeights;
        private const float UNDERWATER_CULL_DEPTH = 50.0f;
        private const float WATER_SURFACE_DEPTH = 16.0f;

        private void ExportHeightmap()
        {
            int[,] heightmap = Zone.Heightmap.ToIntArray();

            #region rivers
            // Export rivers
            List<List<Vector3>> riverPoints = Zone.GetRiverPoints();
            int numWater = riverPoints.Count;
            _waterHeights = Zone.GetWaterHeights();
            for (int i = 0; i < numWater; i++)
            {
                int points = riverPoints[i].Count / 2;
                int index = 0;
                while (index < points)
                {
                    int toWrite = Math.Min(points - index, 2);
                    GeomSetWriter.WriteConvexVolume(toWrite * 2, 0, _waterHeights[i], CEM.GeomSetWriter.eAreas.Water);

                    for (int j = 0; j < toWrite; j++)
                    {
                        float x = riverPoints[i][(index + j) * 2].X;
                        float y = riverPoints[i][(index + j) * 2].Y;
                        float z = _waterHeights[i];
                        GeomSetWriter.WriteConvexVolumeVertex(new Vector3(x, y, z));
                    }

                    for (int j = toWrite - 1; j >= 0; j--)
                    {
                        float x = riverPoints[i][(index + j) * 2 + 1].X;
                        float y = riverPoints[i][(index + j) * 2 + 1].Y;
                        float z = _waterHeights[i];

                        GeomSetWriter.WriteConvexVolumeVertex(new Vector3(x, y, z));
                    }

                    if (points - index > toWrite)
                        index += toWrite - 1;
                    else
                        index += toWrite;
                }
            }
            #endregion

            // Export Heightmap (but pay attention to water map)
            _waterMap = Zone.LoadWaterMap();
            for (int sx = 0; sx < 8; sx++)
            {
                for (int sy = 0; sy < 8; sy++)
                {
                    Matrix4 myWorldMatrix = Matrix4.CreateTranslation(Zone.OffsetVector);
                    myWorldMatrix *= Matrix4.CreateTranslation(8192 * (sx), 8192 * (sy), 0);
                    const int xVectors = 33;
                    const int yVectors = 33;

                    var myTriangles = new List<Triangle>();
                    var myVertices = new List<Vector3>(); // no water.Z

                    for (int y = 0; y < yVectors; y++)
                    {
                        for (int x = 0; x < xVectors; x++)
                        {
                            int z = 0;
                            var waterZ = -1;
                            if (sx == 7 && x == (xVectors - 1))
                            {
                                if (sy == 7 && y == (yVectors - 1))
                                {
                                    z = heightmap[sx * 32 + (x - 1), sy * 32 + (y - 1)];
                                    waterZ = _waterMap [sx * 32 + (x - 1), sy * 32 + (y - 1)];
                                }
                                else
                                {
                                    z = heightmap[sx * 32 + (x - 1), sy * 32 + y];
                                    waterZ = _waterMap [sx * 32 + (x - 1), sy * 32 + y];
                                }
                            }
                            else if (sy == 7 && y == (yVectors - 1))
                            {
                                z = heightmap[sx * 32 + x, sy * 32 + (y - 1)];
                                waterZ = _waterMap [sx * 32 + x, sy * 32 + (y - 1)];
                            }
                            else
                            {
                                z = heightmap[sx * 32 + x, sy * 32 + y];
                                waterZ = _waterMap [sx * 32 + x, sy * 32 + y];
                            }
                            Vector3 vector = Vector3.Transform(new Vector3(x * 256, y * 256, z), myWorldMatrix);

                            if (waterZ != 255 && waterZ < _waterHeights.Length && _waterHeights[waterZ] > vector.Z)
                            {
                                vector.Z = _waterHeights[waterZ] - WATER_SURFACE_DEPTH;
                            }

                            myVertices.Add(vector);

                            if (y == yVectors - 1 || x == xVectors - 1)
                                continue;

                            myTriangles.Add(new Triangle((ushort) (x + ((y + 1) * xVectors)), (ushort) (x + 1 + (y * xVectors)),
                                                         (ushort) (x + (y * xVectors))));
                            myTriangles.Add(new Triangle((ushort) (x + ((y + 1) * xVectors)), (ushort) (x + 1 + ((y + 1) * xVectors)),
                                                         (ushort) (x + 1 + (y * xVectors))));
                        }
                    }

                    try
                    {
                        ObjWriter.AddMesh(myVertices.ToArray(), myTriangles.ToArray());
                    }
                    catch (InvalidDataException)
                    {
                        Log.Error($"Invalid water map, skipping. (Zone: {Zone})");
                        continue;
                    }
                }
            }
        }

        private float GetWaterLevelAt(float x, float y)
        {
            if (_waterMap == null || _waterHeights == null)
                return float.MinValue;

            Vector3 localPos = new Vector3(x, y, 0) - Zone.OffsetVector;

            const float sectorSize = 8192.0f;
            const float cellSize = 256.0f;

            int sx = (int) (localPos.X / sectorSize);
            int sy = (int) (localPos.Y / sectorSize);

            int mapX = (int) (localPos.X / cellSize);
            int mapY = (int) (localPos.Y / cellSize);

            mapX = Math.Max(0, Math.Min(255, mapX));
            mapY = Math.Max(0, Math.Min(255, mapY));

            int waterTypeIndex = _waterMap[mapX, mapY];

            if (waterTypeIndex == 255 || waterTypeIndex >= _waterHeights.Length)
                return float.MinValue;

            return _waterHeights[waterTypeIndex];
        }
    }
}

using System;
using CEM.Utils;

namespace CEM.World {
  /// <summary>
  /// DAoC heightmap
  /// </summary>
  internal class HeightMap {
    private readonly float _loc2Px;
    private readonly int _offsetFactor;
    private readonly byte[,] _offsetMap;
    private readonly int _size;
    private readonly int _terrainFactor;
    private readonly byte[,] _terrainMap;

    /// <summary>
    /// Creates a new heightmap
    /// </summary>
    /// <param name="zone"></param>
    public HeightMap(Zone2 zone) {
      _terrainMap = zone.LoadTerrainMap();
      _terrainFactor = zone.TerrainMapScaleFactor;
      _offsetMap = zone.LoadOffsetMap();
      _offsetFactor = zone.OffsetMapScaleFactor;
      _loc2Px = 1.0f*(_size = _terrainMap.GetLength(0))/zone.Width;
      Log.Debug("Loaded heightmap for {0}", zone);
    }

    /// <summary>
    /// Returns the height at the given zone locs
    /// </summary>
    public ushort this[float zoneX, float zoneY] {
      get {
        return
          (ushort) Util.Clip((int) Math.Round(GetInterpolatedPixelHeight(zoneX*_loc2Px, zoneY*_loc2Px)), 100, 65000);
      }
    }

    private int GetPixelHeight(int py, int px) {
      /* watch for swapped x and y!! */
      px = Util.Clip(px, 0, _size-1);
      py = Util.Clip(py, 0, _size-1);
      return _terrainMap[px, py]*_terrainFactor + _offsetMap[px, py]*_offsetFactor;
    }

    private float GetInterpolatedPixelHeight(float px, float py) {
      var left = (int) px;
      var right = (int) (px + 1);
      var top = (int) py;
      var bottom = (int) (py + 1);
      float xPercent = px%1.0f;
      float yPercent = py%1.0f;

      // Subtile layout:
      /* ----       origin = top left
       * | /|
       * |/ |
       * ---- */

      float bottomLeft = GetPixelHeight(bottom, left);
      float topRight = GetPixelHeight(top, right);
      bool firstTri = (xPercent + yPercent) <= 1.0f;
      if (firstTri) {
        float edge = GetPixelHeight(top, left); // create a --- line shifted by yPercent
        float y1 = MathUtil.Lerp(edge, bottomLeft, yPercent);
        float y2 = MathUtil.Lerp(topRight, bottomLeft, yPercent);
        return MathUtil.Lerp(y1, y2, xPercent); // get the x point on that line
      }
      else {
        xPercent = 1.0f - xPercent;
        yPercent = 1.0f - yPercent;
        float edge = GetPixelHeight(bottom, right);
        float y1 = MathUtil.Lerp(edge, topRight, yPercent);
        float y2 = MathUtil.Lerp(bottomLeft, topRight, yPercent);
        return MathUtil.Lerp(y1, y2, xPercent);
      }
    }

    /// <summary>
    /// Returns the heightmap as an int array
    /// </summary>
    /// <returns></returns>
    public int[,] ToIntArray() {
      var res = new int[_size,_size];
      for (int x = 0; x < _size; x++)
        for (int y = 0; y < _size; y++)
          res[x, y] = GetPixelHeight(y, x);
      return res;
    }
  }
}
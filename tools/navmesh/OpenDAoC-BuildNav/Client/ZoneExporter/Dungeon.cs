using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CEM.Utils;
using MNL;
using OpenTK;

namespace CEM.Client.ZoneExporter
{
  /// <summary>
  /// Dungeon
  /// </summary>
  partial class Zone2Obj
  {
    private void ExtractDungeon(Dictionary<string, string> proxyValues)
    {
      // Is offset internally (ExtractMeshesFromPlace)

      NiFile[] models = ParseChunk("dungeon.chunk", proxyValues);
      Matrix4[] matrices = ExtractMeshesFromPlace("dungeon.place", models, null, true, both: false);
      // hack: the place file looks identical to the prop file, atleast the first entries that I use 
      ExtractMeshesFromPlace("dungeon.prop", models, matrices, true, both: false);
    }

    private NiFile[] ParseChunk(string name, Dictionary<string, string> proxyValues)
    {
      var models = new List<NiFile>();
      using (TextReader reader = new StreamReader(ClientData.FindCSV(Zone, name)))
      {
        string input;
        while ((input = reader.ReadLine()) != null)
        {
          // Locate the file
          string file = input;
          if (proxyValues.ContainsKey(file))
          {
            file = proxyValues[file];
          }
          Stream stream = ClientData.FindDNIF(Zone, file);
          if (stream == null)
          {
            Log.Error("Dungeon Model not found: {0}", file);
            models.Add(null);
            continue;
          }

          // Load the model
          NiFile model;
          using (var br = new BinaryReader(stream))
            model = new NiFile(br, file);

          if (!model.Loaded)
            continue;

          models.Add(model);
        }
      }
      return models.ToArray();
    }

    private Matrix4[] ExtractMeshesFromPlace(string dungeonPlacementFile, NiFile[] models, Matrix4[] oldWorldMatrices, bool invertTris = false, bool both = false)
    {
      //hack: return if there is no prop file (it happens)
      var stream = ClientData.FindCSV(Zone, dungeonPlacementFile);
      if (dungeonPlacementFile.EndsWith(".prop") && stream == null)
      {
        return new Matrix4[0];
      }

      var worldMatrices = new List<Matrix4>();
      using (TextReader reader = new StreamReader(stream))
      {
        // find the file
        string input;
        int nifIndex = 0;
        while ((input = reader.ReadLine()) != null)
        {
          if (input.Trim() == string.Empty) continue;

          string[] split = input.Split(',');
          int modelid = int.Parse(split[0], CultureInfo.InvariantCulture);
          float x = float.Parse(split[1], CultureInfo.InvariantCulture);
          float y = float.Parse(split[2], CultureInfo.InvariantCulture);
          float z = float.Parse(split[3], CultureInfo.InvariantCulture);

          if (z < -65536) continue; // Trollheim has a broken piece at -72k that screws up recast
          float angle = float.Parse(split[4], CultureInfo.InvariantCulture);
          float ax = float.Parse(split[5], CultureInfo.InvariantCulture);
          float ay = float.Parse(split[6], CultureInfo.InvariantCulture);
          float az = float.Parse(split[7], CultureInfo.InvariantCulture);
          //unkown?
          int parentid = 0;
          if (dungeonPlacementFile.EndsWith(".prop"))
          {
            parentid = int.Parse(split[9], CultureInfo.InvariantCulture);
          }

          NiFile model = models[modelid];

          if (model == null)
          {
            Log.Warn("Model with id " + modelid + " is missing!");
          }

          Matrix4 worldMatrix = Matrix4.Identity;
          worldMatrix *= Matrix4.CreateFromAxisAngle(new Vector3(ax, ay, -az), angle);
          worldMatrix *= Matrix4.CreateTranslation(x, y, z);

          if (!dungeonPlacementFile.EndsWith(".prop"))
          {
            worldMatrix *= Matrix4.CreateScale(-1, 1, 1);
            worldMatrix *= Matrix4.CreateTranslation(8192 * 4 - 768, 8192 * 4 - 768, 16000);
            worldMatrices.Add(worldMatrix);
          }
          else
          {
            worldMatrix = worldMatrix * oldWorldMatrices[parentid];
          }

          // pickee is not good enough for recast (since it generates it's own optimized navmesh from the collision layer)
          AddModelToObj(model, worldMatrix, /*new[] {"pickee"} */ new[] { "collide", "collidee", "collision" }, invertTris, both: both);
          ExtractDoor(model, worldMatrix, nifIndex);
          ExtractLadder(model, worldMatrix);

          nifIndex++;
        }
      }

      return worldMatrices.ToArray();
    }
  }
}

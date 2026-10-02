using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CEM.Utils;
using MNL;
using OpenTK;

namespace CEM.Client.ZoneExporter
{
  /// <summary>
  /// City
  /// </summary>
  partial class Zone2Obj
  {
    #region ExtactCity
    // this is more or less simple
    private void ExtractCity()
    {
      // Is offset internally (ExtractMeshesFromPlace)

      string modelDir = string.Format("zones/zone{0:D3}/nifs/", ZoneID);
      Matrix4 worldMatrix = Matrix4.Identity;
      worldMatrix *= Matrix4.CreateScale(-1, 1, 1);
      worldMatrix *= Matrix4.CreateTranslation(8192 * 4 - 768, 8192 * 4 - 768, 8000);
      foreach (var file in ClientData.FindFiles(modelDir, "*.npk"))
      {
        var name = Path.GetFileNameWithoutExtension(file);
        var id = int.Parse(Regex.Match(name, "([0-9]+)").Groups[1].Value); // find camXX.mpk where XX = id of chunk
        string nif = name + ".nif";
        string fullPath = file + "/" + nif;
        fullPath = fullPath.Replace('\\', '/');
        NiFile model;
        try
        {
          using (var br = new BinaryReader(ClientData.Find(Zone, fullPath)))
            model = new NiFile(br, nif);

          if (!model.Loaded)
            continue;

          AddModelToObj(model, worldMatrix, new[] { "collide", "collidee", "collision" }, true);
          ExtractDoor(model, worldMatrix, id);
          ExtractLadder(model, worldMatrix);
        }
        catch (Exception ex)
        {
          Log.Error("Could not extract city model {0}: {1}", fullPath, ex.Message);
        }
      }
    }
    #endregion

    #region ExtractSkyCity
    // this works exactly like a dungeon :)
    private void ExtractSkyCity(Dictionary<string, string> proxyValues)
    {
      // Is offset internally (ExtractMeshesFromPlace)

      NiFile[] models = ParseChunk("skycity.chunk", proxyValues);
      Matrix4[] matrices = ExtractMeshesFromPlace("skycity.place", models, null);
      // hack: the place file looks identical to the prop file, atleast the first entries that I use 
      ExtractMeshesFromPlace("skycity.prop", models, matrices);
    }
    #endregion
  }
}

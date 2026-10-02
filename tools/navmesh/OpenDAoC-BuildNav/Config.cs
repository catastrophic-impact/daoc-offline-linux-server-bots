using System.IO;
using CEM.Client;
using CEM.Utils;

namespace CEM {
  /// <summary>
  /// Configuration class
  /// </summary>
  internal class Config {
    public CEMConfig CEM { get; private set; }

    public Config() {
      CEM = new CEMConfig();
    }

    #region Save & Load

    private static readonly Json _json = new Json {SerializeFieldNames = true, Pretty = true};

    /// <summary>
    /// Saves a json config file
    /// </summary>
    /// <param name="file"></param>
    public void Save(string file) {
      File.WriteAllText(file, _json.ToJson(this));
    }

    /// <summary>
    /// Loads a config from file
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    public static Config Load(string file) {
      return _json.ParseJson<Config>(File.ReadAllText(file));
    }

    #endregion
  }

  internal class CEMConfig {
    public string GamePath;
  }
}
/*
    LITS Game Engine
    Copyright (C) 2007-2008 Metty, Schaf and all other developers participating in this project

    This file is part of LITS.
    LITS is free software: you can redistribute it and/or modify
    it under the terms of the Lesser GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    LITS is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    Lesser GNU General Public License for more details.

    You should have received a copy of the Lesser GNU General Public License
    along with this engine.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CEM.Utils;

namespace CEM.Core {
  /// <summary>
  /// CVar Manager
  /// </summary>
  internal static class CVars {
    private const string CVAR_FORMAT = "{1}.{2}";

    private static readonly SortedDictionary<string, PropertyInfo> _cvars =
      new SortedDictionary<string, PropertyInfo>();

    /// <summary>
    /// Inits the CVar Manager and scans all loaded assemblies
    /// </summary>
    public static void Init() {
      _cvars.Clear();

      //Log.Normal("Initialising CVar System..");
      foreach (var a in Reflector.GetApplicationAssemblies()) {
        foreach (var t in a.GetTypes()) {
          foreach (var p in t.GetProperties(BindingFlags.Static | BindingFlags.Public)) {
            var attr = (CVarAttribute) p.GetCustomAttributes(typeof (CVarAttribute), false).FirstOrDefault();
            if (attr == null)
              continue;

            // Check for default values
            if (attr.Default != null)
              p.SetValue(null, attr.Default, null);
            _cvars.Add(attr.Name ?? Resolve(p), p);
          }
        }
      }

      //Log.Debug("=> Found " + _cvars.Count + " CVars!");
    }

    /// <summary>
    /// Saves all CVars
    /// </summary>
    /// <param name="w"></param>
    public static void Save(StreamWriter w) {
      Log.Normal("Saving CVars to Config...");
      int cnt = 0;

      foreach (var kv in _cvars) {
        PropertyInfo pi = kv.Value;
        var attr = (CVarAttribute) pi.GetCustomAttributes(typeof (CVarAttribute), false)[0];

        if (attr.ReadOnly)
          continue;

        string name = kv.Key;
        string val = Convert.ToString(kv.Value.GetValue(null, null));
        w.WriteLine(name + "\t=\t" + val);
        cnt++;
      }
      w.Flush();

      Log.Debug("=> Saved " + cnt + " CVar's!");
    }

    /// <summary>
    /// Saves all CVars
    /// </summary>
    /// <param name="fs"></param>
    public static void Save(Stream fs) {
      Save(new StreamWriter(fs));
    }

    /// <summary>
    /// Saves all CVars
    /// </summary>
    /// <param name="file"></param>
    public static void Save(string file) {
      var fs = new FileStream(file, FileMode.Create, FileAccess.Write);
      Save(fs);
      fs.Close();
    }

    /// <summary>
    /// Loads CVars
    /// </summary>
    /// <param name="r"></param>
    public static void Load(StreamReader r) {
      Log.Normal("Loading CVars from Config...");
      int cnt = 0;

      while (!r.EndOfStream) {
        string line = r.ReadLine();
        string[] split = line.Split(new[] {'='}, 2, StringSplitOptions.None);

        string name = split[0].ToLower().Trim('\t');
        string val = split[1].Trim('\t');

        if (!_cvars.ContainsKey(name))
          continue;

        PropertyInfo pi = _cvars[name];
        var attr = (CVarAttribute) pi.GetCustomAttributes(typeof (CVarAttribute), false)[0];

        if (attr.ReadOnly)
          continue;

        pi.SetValue(null, Types.Convert(val, pi.PropertyType), null);
        cnt++;
      }

      Log.Debug("=> Loaded " + cnt + " CVar's!");
    }

    /// <summary>
    /// Loads CVars
    /// </summary>
    /// <param name="fs"></param>
    public static void Load(Stream fs) {
      Load(new StreamReader(fs));
    }

    /// <summary>
    /// Loads CVars
    /// </summary>
    /// <param name="file"></param>
    public static void Load(string file) {
      if (!File.Exists(file))
        return;

      var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
      Load(fs);
      fs.Dispose();
    }

    /// <summary>
    /// Lists all cvars
    /// </summary>
    /// <param name="format">{0} = cvar, {1} = value</param>
    /// <param name="begin"></param>
    /// <returns></returns>
    public static string[] ListCVars(string format, string begin) {
      var result = new List<string>(_cvars.Count);
      string cmp = (string.IsNullOrEmpty(begin) ? null : begin.ToLower());

      foreach (var kv in _cvars) {
        if (cmp != null && !kv.Key.StartsWith(cmp))
          continue;
        object val = kv.Value.GetValue(null, null);
        result.Add(string.Format(format, kv.Key, Convert.ToString(val)));
      }

      return result.ToArray();
    }

    /// <summary>
    /// Changes the cvar to the specified value
    /// </summary>
    /// <param name="cvar"></param>
    /// <param name="value"></param>
    public static bool ChangeCVar(string cvar, object value) {
      cvar = cvar.ToLower();

      if (!_cvars.ContainsKey(cvar))
        return false;

      PropertyInfo pi = _cvars[cvar];
      var attr = (CVarAttribute) pi.GetCustomAttributes(typeof (CVarAttribute), false)[0];

      if (attr.ReadOnly)
        return false;

      try {
        pi.SetValue(null, Types.Convert(value, pi.PropertyType), null);
      }
      catch (Exception e) {
        Log.Error("Error: " + e.Message);
        return false;
      }
      return true;
    }

    /// <summary>
    /// Resolves the cvar to the appropriate string format
    /// </summary>
    /// <param name="cvar"></param>
    /// <returns></returns>
    private static string Resolve(PropertyInfo cvar) {
      Type declare = cvar.DeclaringType;
      string res = string.Format(CVAR_FORMAT, declare.Namespace, declare.Name, cvar.Name);
      return res.ToLower();
    }
  }
}
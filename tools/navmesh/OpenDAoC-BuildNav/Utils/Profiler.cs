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
using System.Text;

namespace CEM.Utils {
  /// <summary>
  /// Performance Profiler
  /// </summary>
  public class Profiler {
    private readonly Dictionary<string, Profiler> m_SubProfilers = new Dictionary<string, Profiler>();
    private readonly float[] m_Values = new float[30];
    private string m_Name = "";
    private ProfilerResult m_Result;
    private float m_Start = -1;

    public ProfilerResult Result {
      get { return m_Result; }
    }

    public string Name {
      get { return m_Name; }
      set { m_Name = value; }
    }

    public void StartRound(string name) {
      m_Start = GetCurrentMS();
      m_Name = name;
    }

    public ProfilerResult EndRound() {
      if (m_Start == -1)
        return new ProfilerResult();

      float end = GetCurrentMS();

      var res = new ProfilerResult();
      res.Name = m_Name;

      float cur = 0;
      for (int a = 0; a < m_Values.Length; a++) {
        if (a + 1 < m_Values.Length)
          cur += m_Values[a] = m_Values[a + 1];
        else
          cur += m_Values[a] = (end - m_Start);
      }
      res.NeededTime = cur/m_Values.Length;
      res.Percent = 100;

      var subresults = new List<ProfilerResult>();
      foreach (var sub in m_SubProfilers.Values) {
        ProfilerResult subres = sub.Result;
        subres.Percent = subres.NeededTime/(res.NeededTime == 0 ? 1 : res.NeededTime)*100;
        subresults.Add(subres);
      }
      res.Subresults = subresults.ToArray();

      m_Start = -1;
      m_Result = res;
      return res;
    }

    public Profiler StartSubprofiler(string name) {
      if (m_SubProfilers.ContainsKey(name)) {
        m_SubProfilers[name].StartRound(name);
        return m_SubProfilers[name];
      }

      var p = new Profiler();
      p.StartRound(name);
      m_SubProfilers.Add(name, p);
      return p;
    }

    public Profiler GetSubprofiler(string name) {
      if (m_SubProfilers.ContainsKey(name))
        return m_SubProfilers[name];
      else {
        var p = new Profiler();
        p.Name = name;
        m_SubProfilers.Add(name, p);
        return p;
      }
    }

    public ProfilerResult EndSubprofiler(string name) {
      if (!m_SubProfilers.ContainsKey(name))
        throw new ArgumentException("Subprofiler not found!");

      Profiler sub = m_SubProfilers[name];
      return sub.EndRound();
    }

    /// <summary>
    /// Clears all data
    /// </summary>
    public void ClearData() {
      m_SubProfilers.Clear();
    }

    /// <summary>
    /// Sets the needed time manually (calls end round)
    /// </summary>
    /// <param name="needed"></param>
    public void SetValue(float needed) {
      m_Start = GetCurrentMS() - needed;
      EndRound();
    }

    private static float GetCurrentMS() {
      return Util.TicksD;
    }
  }

  /// <summary>
  /// Result of the profiler operation
  /// Created when the profiler round is ended
  /// </summary>
  public struct ProfilerResult {
    /// <summary>
    /// Defines an empty, standard Profiler Result
    /// </summary>
    public static ProfilerResult Empty = new ProfilerResult();

    /// <summary>
    /// Name of this profiler
    /// </summary>
    public string Name;

    /// <summary>
    /// Required time in milliseconds
    /// </summary>
    public float NeededTime;

    /// <summary>
    /// Percentage of performance needed relative to the parent profiler
    /// </summary>
    public float Percent;

    /// <summary>
    /// All sub profiler results
    /// </summary>
    public ProfilerResult[] Subresults;

    /// <summary>
    /// Returns the whole profiler result (result + subresults) in form of a multiline string
    /// </summary>
    /// <returns></returns>
    public new string ToString() {
      if (string.IsNullOrEmpty(Name))
        return "";

      var s = new StringBuilder();

      s.AppendLine((Name.PadRight(12) + "\t") + "  " + (Percent.ToString("F2") + "%").PadLeft(8) + "\t  " +
                   (NeededTime.ToString("F1") + "ms").PadLeft(10));

      foreach (var sub in Subresults) {
        string[] split = sub.ToString().Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries);

        foreach (var p in split)
          s.AppendLine("+ " + p);
      }

      return s.ToString();
    }
  }
}
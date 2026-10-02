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
using System.Diagnostics;
using System.Drawing;
using System.Linq;

namespace CEM.Utils {
  /// <summary>
  /// General Utility class
  /// </summary>
  public static class Util {
    private static readonly Random _random = new Random();
    private static readonly Stopwatch _watch = new Stopwatch();

    static Util() {
      _watch.Start();
    }

    /// <summary>
    /// Random Number generator
    /// </summary>
    public static Random Random {
      get { return _random; }
    }

    /// <summary>
    /// Current Ticks in milliseconds
    /// </summary>
    public static long Ticks {
      get { return _watch.ElapsedMilliseconds; }
    }

    /// <summary>
    /// Current Ticks in milliseconds
    /// </summary>
    public static float TicksD {
      get { return _watch.Elapsed.Ticks/1000.0f/10.0f; }
    }


    /// <summary>
    /// Returns the serialised form of the list formatted by format
    /// </summary>
    /// <param name="dic"></param>
    /// <param name="format"></param>
    /// <returns></returns>
    public static string[] GetSerialised(IDictionary<string, string> dic, string format) {
      var data = new List<string>();

      foreach (var k in dic)
        data.Add(string.Format(format, k.Key, k.Value));

      return data.ToArray();
    }

    /// <summary>
    /// Parses a int (safe)
    /// </summary>
    /// <param name="v"></param>
    /// <returns></returns>
    public static int ParseI(string v) {
      int res;
      int.TryParse(v, out res);
      return res;
    }

    /// <summary>
    /// Parses a float (safe)
    /// </summary>
    /// <param name="v"></param>
    /// <returns></returns>
    public static float ParseF(string v) {
      float res;
      float.TryParse(v, out res);
      return res;
    }

    /// <summary>
    /// Returns the distance between two points
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    public static float GetDistance(PointF a, PointF b) {
      float relX = a.X - b.X;
      float relY = a.Y - b.Y;

      return (float) Math.Sqrt((relX*relX) + (relY*relY));
    }

    /// <summary>
    /// Random Chance
    /// </summary>
    /// <param name="perc"></param>
    /// <returns></returns>
    public static bool Chance(float perc) {
      return Random.Next(1, 100) <= perc;
    }

    /// <summary>
    /// Random Chance (promil)
    /// </summary>
    /// <param name="pro"></param>
    /// <returns></returns>
    public static bool ChanceP(float pro) {
      return Random.Next(1, 1000) <= pro;
    }

    /// <summary>
    /// Returns a power-of-two value of the specified value
    /// </summary>
    /// <param name="val"></param>
    /// <returns></returns>
    public static int PowerOfTwo(int val) {
      return (int) Math.Pow(2, Math.Ceiling(Math.Log(val, 2)));
    }

    /// <summary>
    /// Counts how many occurences of needle are found in haystack
    /// </summary>
    /// <param name="haystack"></param>
    /// <param name="needle"></param>
    /// <returns></returns>
    public static int CountStr(string haystack, string needle) {
      int cnt = 0;
      int pos = 0;

      while ((pos = haystack.IndexOf(needle, pos)) != -1) {
        pos++; //indexof + 1
        cnt++;
      }

      return cnt;
    }

    /// <summary>
    /// Clips (max, min) the value
    /// </summary>
    public static int Clip(int val, int min, int max) {
      return Math.Min(Math.Max(val, min), max);
    }

    /// <summary>
    /// Clips (max, min) the value
    /// </summary>
    public static float Clip(float val, float min, float max) {
      return Math.Min(Math.Max(val, min), max);
    }

    /// <summary>
    /// Joins two objects by a space
    /// </summary>
    public static string Space(object a, object b) {
      return a + " " + b;
    }
  }
}
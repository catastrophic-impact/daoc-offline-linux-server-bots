/*
Copyright 2011 Google Inc

Licensed under the Apache License, Version 2.0f(the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Collections.Generic;
using CEM.Datastructures;
using CEM.World;

namespace CEM.Utils {
  /// <summary>
  /// Extension method container class.
  /// </summary>
  public static class Extensions {
    /// <summary>
    /// Trims the string to the specified length, and replaces the end with "..." if trimmed.
    /// </summary>
    public static string TrimByLength(this string str, int maxLength) {
      if (maxLength < 3) {
        throw new ArgumentException("Please specify a maximum length of at least 3", "maxLength");
      }

      if (str.Length <= maxLength) {
        return str; // Nothing to do.
      }

      return str.Substring(0, maxLength - 3) + "...";
    }

    /// <summary>
    /// str -> int
    /// </summary>
    public static int ToInt(this string str) {
      return int.Parse(str);
    }


    /// <summary>
    /// float to int, rounded
    /// </summary>
    public static int ToInt(this float d) {
      return (int) Math.Round(d);
    }

    /// <summary>
    /// String to lines
    /// </summary>
    /// <param name="str"></param>
    /// <returns></returns>
    public static IEnumerable<string> ToLines(this string str) {
      foreach (var line in str.Split(new[] {Environment.NewLine}, StringSplitOptions.None))
        yield return line;
    }
  }

  /// <summary>
  /// Enum Extensions
  /// </summary>
  public static class EnumExtensions {
    /// <summary>
    /// True if the specified flag is set
    /// </summary>
    public static bool Has<T>(this T val, T flag) where T : struct {
      int allI = Convert.ToInt32(val);
      int flagI = Convert.ToInt32(flag);
      return (allI & flagI) == flagI;
    }
  }
}
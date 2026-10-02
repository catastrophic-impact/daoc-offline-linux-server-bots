using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CEM.Core
{
  /// <summary>
  /// Type conversions
  /// </summary>
  internal static class Types
  {
    /// <summary>
    /// Converts the object to the specified type
    /// </summary>
    public static object Convert(object o, Type destType) {
      try {
        if (o is string) {
          var str = (string) o;
          str = str.Trim();
          if (string.IsNullOrEmpty(str))
            str = null;
          o = str;
        }
        if (destType.IsGenericType && destType.GetGenericTypeDefinition() == typeof (Nullable<>)) {
          if (o == null)
            return null;
          destType = destType.GetGenericArguments().First();
        }
        return System.Convert.ChangeType(o, destType);
      }
      catch (Exception) {
        throw new ArgumentException("Could not convert value '" + o + "' to " + destType);
      }
    }

    /// <summary>
    /// Converts the object to the specified type
    /// </summary>
    public static T Convert<T>(object o) {
      return (T)Convert(o, typeof (T));
    }
  }
}

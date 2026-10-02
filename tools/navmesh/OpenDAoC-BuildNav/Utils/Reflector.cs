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
using System.Reflection;

namespace CEM.Utils {
  /// <summary>
  /// Reflection Helper Class
  /// </summary>
  public static class Reflector {
    /// <summary>
    /// Retrieves all Assemblies used by the application
    /// EXCLUDES system libraries (System.Windows.Forms, ...)
    /// </summary>
    /// <returns></returns>
    public static Assembly[] GetApplicationAssemblies() {
      var res = new List<Assembly>();
      foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) {
        if (a == null || a.GlobalAssemblyCache)
          continue; //sys assembly
        if (a.GetName().Name.StartsWith("System")) continue;
        if (a.GetName().Name.StartsWith("Microsoft")) continue;
        if (a.GetName().Name.StartsWith("mscorlib")) continue;

        res.Add(a);
      }
      return res.ToArray();
    }

    /// <summary>
    /// Gets all types with the specified base type
    /// (Interfaces and Abstract types EXcluded)
    /// </summary>
    /// <param name="baseClass"></param>
    /// <returns></returns>
    public static Type[] GetTypes(Type baseClass) {
      var res = new List<Type>();

      foreach (var a in GetApplicationAssemblies()) {
        foreach (var t in a.GetTypes()) {
          if (t.IsAbstract || t.IsInterface)
            continue;

          if (baseClass.IsAssignableFrom(t)) res.Add(t);
        }
      }

      return res.ToArray();
    }

    /// <summary>
    /// Gets all the types with the specified attribute
    /// </summary>
    /// <param name="attribute"></param>
    /// <returns></returns>
    public static Type[] GetTypesWithAttribute(Type attribute) {
      var res = new List<Type>();

      foreach (var a in GetApplicationAssemblies()) {
        foreach (var t in a.GetTypes()) {
          if (t.IsAbstract || t.IsInterface)
            continue;

          if (t.GetCustomAttributes(attribute, false).Length > 0) res.Add(t);
        }
      }

      return res.ToArray();
    }

    /// <summary>
    /// Finds the specified type
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public static Type FindType(string name) {
      foreach (var a in GetApplicationAssemblies()) {
        foreach (var t in a.GetTypes()) {
          if (t.Name == name)
            return t;
        }
      }
      return null;
    }

    /// <summary>
    /// Creates an instance of the specified type
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="t"></param>
    /// <returns></returns>
    public static T Instance<T>(Type t) {
      return (T) Activator.CreateInstance(t);
    }
  }
}
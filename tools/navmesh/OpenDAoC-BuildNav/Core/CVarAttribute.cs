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

namespace CEM.Core {
  /// <summary>
  /// CVar Attribute
  /// Defines that a property is used as a C[onfig]Var[iable]
  /// </summary>
  [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
  public class CVarAttribute : Attribute {
    public CVarAttribute() {
      ReadOnly = false;
    }

    /// <summary>
    /// Is the cvar readonly?
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Name of the cvar, or null if property name
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Default value
    /// </summary>
    public object Default { get; set; }
  }
}
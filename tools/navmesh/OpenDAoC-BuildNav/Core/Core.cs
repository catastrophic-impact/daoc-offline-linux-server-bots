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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using CEM.Utils;

namespace CEM.Core {
  /// <summary>
  /// Core
  /// </summary>
  internal static class Core {
    private const string ROOT_DIR = "base";

    /// <summary>
    /// Initialises the core
    /// </summary>
    public static void Init() {
      // Set working dir
      Environment.CurrentDirectory = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), ROOT_DIR);
      //Win32.SetDllDirectory(IntPtr.Size == 8 ? "x64" : "x86");
      AppDomain.CurrentDomain.AssemblyResolve += (o, rargs) => {
        Assembly loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.FullName == rargs.Name);
        if (loaded != null)
          return loaded;
        string file = new AssemblyName(rargs.Name).Name + ".dll";
        file = Path.GetFullPath(Path.Combine("lib", file));
        return File.Exists(file) ? Assembly.LoadFile(file) : null;
      };

      // Handle Exceptions
#if !DEBUG
      AppDomain.CurrentDomain.UnhandledException += UnhandledException;
#endif

      // Setup culture
      var i = (CultureInfo) CultureInfo.InvariantCulture.Clone();
      i.NumberFormat.NumberGroupSeparator = " ";
      i.NumberFormat.NumberDecimalSeparator = ".";
      Thread.CurrentThread.CurrentCulture = i;

      // Init subsystems
      CLI.Init();
      CVars.Init();
    }

    private static void UnhandledException(object sender, UnhandledExceptionEventArgs args) {
      Log.Fatal("Unhandled Exception: \r\n\r\n" + args.ExceptionObject);
      Log.Flush();
    }
  }
}
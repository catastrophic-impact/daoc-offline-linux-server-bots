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
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using CEM.Core;

namespace CEM.Utils {
  /// <summary>
  /// Log
  /// </summary>
  public static class Log {
    /// <summary>
    /// The Stream where the log is saved
    /// </summary>
    public static TextWriter LogFile { get; set; }

    /// <summary>
    /// Opens a new log
    /// </summary>
    /// <param name="file">Logfile</param>
    /// <param name="append">Append to the old logfile?</param>
    public static void Open(string file, bool append) {
      var fs =
        new FileStream(file, (append ? FileMode.OpenOrCreate : FileMode.Create), FileAccess.Write,
                       FileShare.ReadWrite);
      LogFile = new StreamWriter(fs);
      lock (LogFile) {
        ((StreamWriter) LogFile).AutoFlush = true;
        WriteLine(Color.ForestGreen, "Log", "Log file '" + file + "' opened");
      }
    }

    /// <summary>
    /// Closes and saves the log
    /// </summary>
    public static void Close() {
      lock (LogFile) {
        WriteLine(Color.ForestGreen, "Log", "Log file closed");
        LogFile.Flush();
        LogFile.Close();
      }
    }

    /// <summary>
    /// Writes a line into the log
    /// </summary>
    /// <param name="c"></param>
    /// <param name="type"></param>
    /// <param name="text"></param>
    private static void WriteLine(Color c, string type, string text) {
      string line =
        string.Format("{0} - {1} => [{2}] {3}", DateTime.Now.ToShortDateString(),
                      DateTime.Now.ToLongTimeString(), type,
                      text);

      //Write into the system console
      //System.Console.ForegroundColor = ConsoleColor.White;
      //System.Console.WriteLine(text);

      //Write into core console
      if (string.IsNullOrEmpty(type))
        CLI.LogLine(c, text);
      else {
        CLI.LogLine(c, "[" + type + "] " + text);
      }

      //Write the line into the logfile
      if (LogFile != null) {
        try {
          LogFile.WriteLine(line);
        }
        catch (ObjectDisposedException) {}
      }
    }

    #region Constants

    public static bool LOG_DEBUG = false;
    public static bool LOG_ERROR = true;
    public static bool LOG_NORMAL = true;
    public static bool LOG_WARNING = true;

    #endregion

    #region Logging methods

    /// <summary>
    /// Logs a 'normal' log message
    /// </summary>
    /// <param name="text">content</param>
    public static void Normal(string text, params object[] args) {
      if (LOG_NORMAL)
        WriteLine(Color.White, "", args.Length == 0 ? text : string.Format(text, args));
    }

    /// <summary>
    /// Logs a 'debug' log message
    /// </summary>
    /// <param name="text">content</param>
    public static void Debug(string text, params object[] args) {
      if (LOG_DEBUG)
        WriteLine(Color.Gray, "Debug", args.Length == 0 ? text : string.Format(text, args));
    }

    /// <summary>
    /// Logs a 'warn' log message
    /// </summary>
    /// <param name="text">content</param>
    public static void Warn(string text, params object[] args) {
      if (LOG_WARNING)
        WriteLine(Color.Yellow, "Warn", args.Length == 0 ? text : string.Format(text, args));
    }

    /// <summary>
    /// Logs a 'error' log message
    /// </summary>
    /// <param name="text">content</param>
    public static void Error(string text, params object[] args) {
      if (LOG_ERROR)
        WriteLine(Color.Red, "Error", args.Length == 0 ? text : string.Format(text, args));
    }

    /// <summary>
    /// Logs a 'error' log message
    /// </summary>
    /// <param name="text">content</param>
    public static void Error(Exception ex) {
      if (LOG_ERROR)
        WriteLine(Color.Red, "Error", ex.ToString());
    }

    /// <summary>
    /// Logs a 'error' log message
    /// </summary>
    /// <param name="text">content</param>
    public static void Fatal(string text, params object[] args) {
      text = args.Length == 0 ? text : string.Format(text, args);
      WriteLine(Color.Red, "FATAL", text);
      MessageBox.Show(text, "Meh :(", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// Saves the log file
    /// </summary>
    public static void Flush() {
      LogFile.Flush();
    }

    #endregion
  }
}
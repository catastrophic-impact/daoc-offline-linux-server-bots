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

namespace CEM.Core {
  /// <summary>
  /// Holds a single command
  /// </summary>
  internal abstract class Command {
    /// <summary>
    /// Processes the entered args
    /// </summary>
    /// <param name="args"></param>
    public abstract int OnCommand(string[] args);

    /// <summary>
    /// => Console.LogLine()
    /// </summary>
    /// <param name="c">Color</param>
    /// <param name="line">Line</param>
    protected static int Log(Color c, string line) {
      CLI.LogLine(c, line);
      return 0;
    }

    /// <summary>
    /// => Console.LogLine()
    /// </summary>
    /// <param name="line">Line</param>
    protected static int Log(string line) {
      CLI.LogLine(Color.LightGray, line);
      return 0;
    }

    /// <summary>
    /// Logs an error
    /// </summary>
    /// <param name="line"></param>
    /// <returns></returns>
    protected static int Error(string line) {
      return Log(Color.Red, line);
    }
  }

  /// <summary>
  /// The attribute for the commandhandler
  /// used for recognition of the command
  /// </summary>
  [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
  public class CmdAttribute : Attribute {
    /// <summary>
    /// Command Handler Attribute
    /// </summary>
    /// <param name="command">The first arg on which the processor reacts to</param>
    public CmdAttribute(string command) {
      Command = command;
    }

    /// <summary>
    /// The Command (first arg), e.g.: "print" or "quit"
    /// </summary>
    public string Command { get; private set; }
  }
}
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
using System.Drawing;
using CEM.Utils;

namespace CEM.Core {
  /// <summary>
  /// Command Line Interface
  /// </summary>
  internal static class CLI {
    #region Message handling

    /// <summary>
    /// Message handler
    /// </summary>
    /// <param name="l"></param>
    public delegate void MessageHandler(ConsoleLine l);

    /// <summary>
    /// Message event
    /// </summary>
    public static event MessageHandler OnMessage;

    /// <summary>
    /// Just logs an normal line
    /// </summary>
    /// <param name="c">Color of the line</param>
    /// <param name="line">Content of the line</param>
    public static void LogLine(Color c, string line, params object[] args) {
      if (args.Length > 0)
        line = string.Format(line, args);
      foreach (var l in line.Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries)) {
        var ll = new ConsoleLine(c, l);

        var cc = ConsoleColor.Gray;

        if (c == Color.Red)
          cc = ConsoleColor.Red;
        if (c == Color.Green)
          cc = ConsoleColor.Green;
        if (c == Color.White)
          cc = ConsoleColor.White;
        if (c == Color.Turquoise)
          cc = ConsoleColor.Cyan;
        if (c == Color.Yellow)
          cc = ConsoleColor.Yellow;

        ConsoleColor old = Console.ForegroundColor;
        Console.ForegroundColor = cc;
        Console.WriteLine(l);
        Console.ForegroundColor = old;

        if (OnMessage != null)
          OnMessage(ll);
      }
    }

    #endregion

    #region Command Handling

    private static readonly SortedDictionary<string, Command> _commands =
      new SortedDictionary<string, Command>();

    /// <summary>
    /// List of all loaded commands
    /// </summary>
    public static SortedDictionary<string, Command> Commands {
      get { return _commands; }
    }

    /// <summary>
    /// Enters a command into the console and processes it
    /// </summary>
    /// <param name="line">The command line</param>
    /// <param name="silent">Defines whether the command line itself is displayed or not</param>
    public static void EnterCommand(string line, bool silent = false) {
      if (!silent)
        LogLine(Color.White, "> " + line);

      //Parse
      string[] cmd = ParseCommandLine(line);

      //Check if valid
      if (cmd.Length == 0)
        return; //invalid

      //First, seek by exact name
      Command h;
      if (_commands.ContainsKey(cmd[0].ToLower()))
        h = _commands[cmd[0].ToLower()];
      else {
        //Seek matches
        KeyValuePair<string, Command>[] matches = GuessCommand(cmd[0]);

        //No matches found
        if (matches.Length == 0) {
          LogLine(Color.Red, "=> no such command: " + cmd[0]);
          return;
        }
          //Only one match
        else if (matches.Length == 1)
          h = matches[0].Value;
          //Multiple matches => display
        else {
          foreach (var m in matches)
            LogLine(Color.White, m.Key);
          return;
        }
      }

      //Execute command
      if (h != null) {
        cmd[0] = line; /* hack for &code */
        h.OnCommand(cmd);
      }
      else LogLine(Color.Red, "→ null cmd: " + cmd[0]);
    }

    /// <summary>
    /// Splits the command and args into an array
    /// </summary>
    /// <param name="line">Input command</param>
    /// <returns>Array of args</returns>
    private static string[] ParseCommandLine(string line) {
      var args = new List<string>();
      int step = 0;
      string current = "";

      foreach (var c in line) {
        if (step == 0) {
          if (c == ' ')
            continue; //skip spaces

          if (c == '"') {
            step = 2;
            continue;
          }
          step = 1;
        }

        if (step == 1 || step == 2) {
          if ((c == ' ' && step == 1) ||
              (c == '"' && step == 2)) {
            //arg end
            args.Add(current);
            current = "";
            step = 0;
          }
          else
            current += c;
        }
      }

      if (step != 0)
        args.Add(current);

      return args.ToArray();
    }

    /// <summary>
    /// Guesses the command from the chars entered
    /// </summary>
    /// <param name="cmd">command</param>
    /// <returns>array of matches</returns>
    private static KeyValuePair<string, Command>[] GuessCommand(string cmd) {
      cmd = cmd.Trim().ToLower();

      var matches = new List<KeyValuePair<string, Command>>();

      foreach (var handler in _commands) {
        if (handler.Key.StartsWith(cmd))
          matches.Add(handler);
      }

      return matches.ToArray();
    }

    #endregion

    public static void Init() { 
      Log.Normal("Console.Init()");

      //Search through assemblys
      foreach (var a in Reflector.GetApplicationAssemblies()) {
        foreach (var t in a.GetTypes()) {
          if (!t.IsClass || t.IsAbstract || t.IsInterface)
            continue;

          object[] attributes = t.GetCustomAttributes(typeof (CmdAttribute), false);

          if (attributes.Length == 0)
            continue;

          foreach (CmdAttribute cmd in attributes) {
            string c = cmd.Command.ToLower();

            if (_commands.ContainsKey(c)) {
              Log.Warn("Console.Init() - Command '" + c + "' already loaded!");
              continue;
            }

            object inst = Activator.CreateInstance(t);
            _commands.Add(c, (Command) inst);
            Log.Normal("Console.Init() - Command '" + c + "' loaded");
          }
        }
      }
    }
  }

  /// <summary>
  /// Container class for console messages
  /// </summary>
  public class ConsoleLine {
    public ConsoleLine() {}

    public ConsoleLine(Color c, string text) {
      Color = c;
      Text = text;
    }

    /// <summary>
    /// The Color in which the line is displayed
    /// </summary>
    public Color Color { get; set; }

    /// <summary>
    /// The content of the line
    /// </summary>
    public string Text { get; set; }
  }
}
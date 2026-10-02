using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;
using System.Globalization;
using CEM.Utils;

namespace CEM
{
  /// <summary>
  /// Door writer
  /// </summary>
  public class DoorWriter : StreamWriter 
  {
    public DoorWriter(string file) : base(file, false) { 
      AutoFlush = true;
      Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
    }

    public void WriteDoor(int doorID, string doorName, int x, int y, int z, int heading, int doorSize)
    {
      var query = "INSERT INTO door (DoorID, ClientNif, X, Y, Z, Heading, DoorSize, LastModified) VALUES({0}, \"{1}\", {2}, {3}, {4}, {5}, {6}, NOW()) ON DUPLICATE KEY UPDATE ClientNif = VALUES(ClientNif), X = VALUES(X), Y = VALUES(Y), Z = VALUES(Z), Heading = VALUES(Heading), DoorSize = VALUES(DoorSize), LastModified = VALUES(LastModified);";
      var line = string.Format(query, doorID, doorName, x, y, z, heading, doorSize);
      WriteLine(line);
    }
  }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CEM.Core;
using CEM.Utils;
using System.CodeDom;
using System.Linq;

namespace CEM.Client
{
  /// <summary>
  /// CSV parseable class
  /// </summary>
  public interface ICsvParseable {}

  /// <summary>
  /// CSV Reader
  /// </summary>
  internal class CsvFile<T> : IDisposable where T : class, ICsvParseable, new() {
    private readonly FieldInfo[] _fields;
    private readonly StreamReader _reader;

    public CsvFile(Stream stream) : this(new StreamReader(stream)) {}
    public CsvFile(StreamReader reader) {
      _reader = reader;
      //File.WriteAllText("/tmp/csv", reader.ReadToEnd()); // DEBUG

      // Create our int -> field map
      _fields = typeof (T).GetFields().Where(field => field.GetCustomAttributes(typeof (NonSerializedAttribute), true).Length <= 0).ToArray();
    } 

    /// <summary>
    /// Skips a line
    /// </summary>
    public void SkipLine() {
      _reader.ReadLine();
    }

    /// <summary>
    /// True if at the end of the stream
    /// </summary>
    public bool EndOfStream {
      get { return _reader.EndOfStream; }
    }

    /// <summary>
    /// Reads all lines from the csv
    /// </summary>
    /// <returns></returns>
    public IEnumerable<T> ReadAll() {
      while (!EndOfStream) {
        T val = ReadLine();
        if (val != null)
          yield return val;
      }
    }

    /// <summary>
    /// Reads a single entry from the csv
    /// </summary>
    /// <returns></returns>
    public T ReadLine() {
      if (EndOfStream)
        return default(T);
      T obj = new T();
      string line = _reader.ReadLine();
      if (string.IsNullOrEmpty(line.Trim(',', ' ')))
        return ReadLine();
      string[] split = line.Split(',');
      for (int i = 0; i < Math.Min(_fields.Length, split.Length); i++) {
        string valu = split[i];
        var field = _fields[i];
        try {
          if (!string.IsNullOrEmpty(valu))
          {
            if (field.FieldType == typeof (bool))
            {
              field.SetValue(obj, valu != "0");
            } else
            {
              var value2 = Types.Convert(valu, field.FieldType);
              field.SetValue(obj, value2);
            }
          } 
          else
          {
            Log.Warn("Empty fields detected: "+line);
          }
        }
        catch (Exception ex) {
          Log.Error("Failed to parse CSV line: "+line+"\n"+ex);
          return null;
        }
      }
      return obj;
    }

    public void Dispose() {
      _reader.Dispose();
    }
  }
}

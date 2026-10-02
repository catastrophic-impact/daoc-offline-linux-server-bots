using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CEM.World;

namespace CEM.Client {
  /// <summary>
  /// Simple JSON Conversion Utility
  /// @author mlinder
  /// </summary>
  internal class Json {
    /// <summary>
    /// Default Json instance
    /// </summary>
    public static readonly Json Default = new Json() { SerializeFieldNames = true };

    public Json() {
      SerializeFieldNames = true;
      Pretty = false;
    }

    #region Object -> Json

    /// <summary>
    /// Converts the specified object into an json string
    /// </summary>
    /// <param name="o"></param>
    /// <returns></returns>
    public string ToJson(object o) {
      if (o == null)
        return "null";
      Type type = o.GetType();

      switch (Type.GetTypeCode(type)) {
        case TypeCode.Boolean:
          return (bool) o ? "true" : "false";
        case TypeCode.String:
        case TypeCode.Char:
        case TypeCode.DateTime:
          return "\"" + EscapeString(Convert.ToString(o, CultureInfo.InvariantCulture)) + "\"";
        case TypeCode.DBNull:
        case TypeCode.Empty:
          throw new NotImplementedException();
        case TypeCode.Object:
          return ObjectToJson(type, o);
        default: // Number
          if (type.IsEnum)
            return ((int) o).ToString();
          return Convert.ToString(o, CultureInfo.InvariantCulture);
      }
    }

    private string ObjectToJson(Type type, object obj) {
      var str = new StringBuilder();
      if (type.IsArray) {
        str.Append("[");
        foreach (var o in (Array) obj) {
          if (str.Length > 1)
            str.Append(",");
          str.Append(ToJson(o));
        }
        str.Append("]");
        return str.ToString();
      }

      // If this is a class/struct, only serialize fields
      str.Append("{");
      if (Pretty) str.Append(Environment.NewLine);
      int i = 0;
      foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
        if (str.Length > 1)
          str.Append(",");
        str.Append(JsonField((SerializeFieldNames ? field.Name : (object) i), field.GetValue(obj)));
        if (Pretty) str.Append(Environment.NewLine);
        i++;
      }
      foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
        if (prop.GetIndexParameters().Any())
          continue; // Skip arrays
        if (str.Length > 1)
          str.Append(",");
        str.Append(JsonField((SerializeFieldNames ? prop.Name : (object) i), prop.GetValue(obj, null)));
        if (Pretty) str.Append(Environment.NewLine);
        i++;
      }
      str.Append("}");
      if (Pretty) str.Append(Environment.NewLine);
      return str.ToString();
    }

    private string JsonField(object name, object value) {
      return ToJson(name) + ":" + ToJson(value);
    }

    private static string EscapeString(string str) {
      str = str.Replace("\\", "\\\\"); // replace \ with \\
      str = str.Replace("\"", "\\\""); // replace " with \"
      return str;
    }

    #endregion

    #region Json -> Object

    /// <summary>
    /// Parses the specified object as an json string
    /// </summary>
    public T ParseJson<T>(string json) {
      object res = ParseJson(json, typeof (T));
      return res == null ? default(T) : T<T>(res);
    }

    /// <summary>
    /// Parses the specified object as an json string
    /// </summary>
    public object ParseJson(string json, Type type) {
      if (json == "null" || json == null)
        return null;
      json = json.Trim();
      switch (Type.GetTypeCode(type)) {
        case TypeCode.DateTime:
          return DateTime.Parse(ParseJsonString(json), CultureInfo.InvariantCulture);
        case TypeCode.Object:
          if (Nullable.GetUnderlyingType(type) != null)
            return ParseJson(json, Nullable.GetUnderlyingType(type));
          if (type.IsArray)
            return ParseArray(json, type.GetElementType());
          if (type == typeof (object) && !json.StartsWith("{")) {
            // value type
            if (json.StartsWith("\"")) return ParseJson(json, typeof (string));
            if (json.StartsWith("[")) return ParseJson(json, typeof (object[]));
            if (json.Contains(".")) return ParseJson(json, typeof (double));
            if (json == "true" || json == "false") return ParseJson(json, typeof (bool));
            return ParseJson(json, typeof (int));
          }
          return ParseClass(json, type);
        case TypeCode.String:
          return ParseJsonString(json);
        default:
          if (type.IsEnum)
            return Enum.Parse(type, json.Trim('"', ' '));
          return Convert.ChangeType(json, type);
      }
    }

    /// <summary>
    /// Parses the specified object as an json string
    /// </summary>
    public void ParseJson(string json, ref object output) {
      output = ParseJson(json, output.GetType());
    }

    /// <summary>
    /// Parses the specified object as an json string
    /// </summary>
    public void ParseJson(Stream stream, ref object output)
    {
      output = ParseJson(new StreamReader(stream).ReadToEnd(), output.GetType());
    }

    /// <summary>
    /// Parses the specified object as an json string
    /// </summary>
    public T ParseJson<T>(Stream json)
    {
      return ParseJson<T>(new StreamReader(json).ReadToEnd());
    }

    private object ParseClass(string json, Type clazz) {
      if (!json.StartsWith("{") || !json.EndsWith("}"))
        throw new ArgumentException("Expected class but got: " + json);
      json = json.Substring(1, json.Length - 2);

      if (clazz == typeof (object)) clazz = typeof (Hashtable);

      object instance = Activator.CreateInstance(clazz);

      foreach (var fieldjson in FindTokens(json, ',')) {
        string[] field = FindTokens(fieldjson, ':').ToArray();
        var key = ParseJson<object>(field[0]);
        string value = field[1];

        // Check if we have a field with that name
        FieldInfo fi;
        PropertyInfo pi;

        if ((fi = GetField(clazz, key)) != null) {
          fi.SetValue(instance, ParseJson(value, fi.FieldType));
        }
        else if ((pi = GetProperty(clazz, key)) != null) {
          pi.SetValue(instance, ParseJson(value, pi.PropertyType), null);
        }
        else if (typeof (Hashtable).IsAssignableFrom(clazz)) {
          ((Hashtable) instance).Add(key, ParseJson<object>(value));
        }
      }

      return instance;
    }

    private static FieldInfo GetField(Type clazz, object key) {
      if (key is string) {
        return clazz.GetField((string) key, BindingFlags.Instance | BindingFlags.Public);
      }
      if (key is int) {
        FieldInfo[] fields = clazz.GetFields(BindingFlags.Instance | BindingFlags.Public);
        if ((int) key >= fields.Length)
          return null;
        return fields[(int) key];
      }

      return null;
    }

    private static PropertyInfo GetProperty(Type clazz, object key) {
      if (key is string) {
        return clazz.GetProperty((string) key, BindingFlags.Instance | BindingFlags.Public);
      }
      if (key is int) {
        PropertyInfo[] props = clazz.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        int ind = (int) key - clazz.GetFields(BindingFlags.Instance | BindingFlags.Public).Length;
        if (ind >= props.Length)
          return null;
        return props[ind];
      }

      return null;
    }

    private Array ParseArray(string json, Type valueType) {
      if (!json.StartsWith("[") || !json.EndsWith("]"))
        throw new ArgumentException("Expected array but got: " + json);
      json = json.Substring(1, json.Length - 2);

      var list = new ArrayList();
      foreach (var token in FindTokens(json, ','))
        list.Add(ParseJson(token, valueType));
      return list.ToArray(valueType);
    }

    /// <summary>
    /// Find tokens on the same logic level (brackets, ..)
    /// </summary>
    private IEnumerable<string> FindTokens(string json, char separator) {
      int tokenStart = 0;
      int? tokenEnd;

      do {
        tokenEnd = FindNextToken(json, tokenStart, separator);
        string token = json.Substring(tokenStart, (tokenEnd ?? json.Length) - tokenStart).Trim();

        if (!string.IsNullOrEmpty(token))
          yield return token;
        if (tokenEnd != null) {
          tokenStart = (int) tokenEnd + 1;
        }
      } while (tokenEnd != null);
    }

    private int? FindNextToken(string str, int startPos, char token) {
      // Find the next non-string token
      bool inString = false;
      bool escape = false;
      int bracketLevel = 0;
      char c;
      do {
        if (startPos >= str.Length)
          return null;
        c = str[startPos++];

        if (inString) {
          if (escape) escape = false;
          else if (c == '\\') escape = true;
          else if (c == '"') inString = false;
        }
        else {
          if (c == '"') inString = true;
          else if (c == '{' || c == '[') bracketLevel++;
          else if (c == '}' || c == ']') bracketLevel--;
        }
      } while ((c != token || inString || bracketLevel != 0));
      return startPos - 1;
    }

    private TK T<TK>(object o) {
      Type type = typeof (TK);
      return type == typeof (object) ? (TK) Convert.ChangeType(o, o.GetType()) : (TK) Convert.ChangeType(o, type);
    }

    private string ParseJsonString(string json) {
      if (!json.StartsWith("\"") || !json.EndsWith("\""))
        throw new ArgumentException("Expected string but got: " + json);
      json = json.Substring(1, json.Length - 2);
      json = json.Replace("\\\"", "\"");
      json = json.Replace("\\\\", "\\");
      return json;
    }

    #endregion

    /// <summary>
    /// True if field names are serialized as strings, or false to use indexes only
    /// </summary>
    public bool SerializeFieldNames { get; set; }

    /// <summary>
    /// True to use pretty formatting
    /// </summary>
    public bool Pretty { get; set; }
  }
}
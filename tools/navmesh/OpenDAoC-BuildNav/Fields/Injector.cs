using System;
using System.Reflection;

namespace CEM.Fields {
  /// <summary>
  /// Field injector -- initalizes lazy fields
  /// </summary>
  internal static class Injector {
    /// <summary>
    /// Injects all fields in the specified object
    /// </summary>
    /// <param name="o"></param>
    public static void Inject(object o) {
      if (o == null) return;
      foreach (var prop in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
        if (typeof (IField).IsAssignableFrom(prop.PropertyType))
          Inject(o, prop);
      }
    }

    private static void Inject(object o, PropertyInfo prop) {
      Type type = prop.PropertyType;
      object val = Activator.CreateInstance(type);

      // Check for special attributes
      foreach (var attr in prop.GetCustomAttributes(typeof (FieldAttribute), true)) {
        // Get all fields, and transport them on the target
        foreach (var field in attr.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) {
          type.GetProperty(field.Name, BindingFlags.Public | BindingFlags.Instance)
              .SetValue(val, field.GetValue(attr), null);
        }
      }

      prop.SetValue(o, val, null);
    }
  }
}
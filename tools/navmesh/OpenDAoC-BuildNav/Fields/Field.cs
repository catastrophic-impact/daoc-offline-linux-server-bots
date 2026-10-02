using System;

namespace CEM.Fields {
  /// <summary>
  /// A lazy field with value change events
  /// </summary>
  /// <typeparam name="T"></typeparam>
  internal class Field<T> : IField {
    private T _value;

    /// <summary>
    /// The value belonging to this field
    /// </summary>
    public T Value {
      get { return _value; }
      set {
        T newVal = GetClippedValue(value);
        if (Equals(_value, newVal))
          return;
        T old = _value;
        _value = newVal;
        if (OnValueChanged != null)
          OnValueChanged(old, newVal);
      }
    }

    /// <summary>
    /// Called whenever this value is changed. (old, new)
    /// </summary>
    public event Action<T, T> OnValueChanged;

    public static implicit operator T(Field<T> f) {
      return f.Value;
    }

    protected virtual T GetClippedValue(T input) {
      return input;
    }

    public override string ToString() {
      return Value == null ? "(null)" : Value.ToString();
    }
  }

  internal interface IField {}

  [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
  internal abstract class FieldAttribute : Attribute {}
}
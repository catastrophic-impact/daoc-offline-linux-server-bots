using System;
using System.IO;

namespace MNL {
  public class KeyGroup<T> where T : BaseKey {
    public eKeyType Interpolation;
    public T[] Values;

    public KeyGroup(BinaryReader reader) {
      Values = new T[reader.ReadUInt32()];
      if (Values.Length != 0) {
        Interpolation = (eKeyType)reader.ReadUInt32();
      }

      for (var i = 0; i < Values.Length; i++) {
        Values[i] = (T)Activator.CreateInstance(typeof(T), new object[] { reader, Interpolation });
      }
    }
  }
}
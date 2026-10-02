namespace CEM.Fields {
  /// <summary>
  /// A range defined by a min, max, and percentage value.
  /// </summary>
  internal class Range : Field<float> {
    public Range() {
      Min = new Field<float> {Value = 0};
      Max = new Field<float> {Value = 100.0f};
    }

    /// <summary>
    /// Percent value
    /// </summary>
    public int Percent {
      get { return (int) (Value*100f/Max); }
      set { Value = (Max - Min)*value/100.0f + Min; }
    }

    /// <summary>
    /// Min Value
    /// </summary>
    public float Min { get; set; }

    /// <summary>
    /// Max
    /// </summary>
    public float Max { get; set; }
  }

  internal class RangeAttribute : FieldAttribute {
    /// <summary>
    /// Maximum value
    /// </summary>
    public readonly float Max;

    /// <summary>
    /// Minimum value
    /// </summary>
    public readonly float Min;

    /// <summary>
    /// Specifies a value range
    /// </summary>
    /// <param name="min">Minimum Value</param>
    /// <param name="max">Maximum Value</param>
    public RangeAttribute(float min, float max) {
      Min = min;
      Max = max;
    }
  }
}
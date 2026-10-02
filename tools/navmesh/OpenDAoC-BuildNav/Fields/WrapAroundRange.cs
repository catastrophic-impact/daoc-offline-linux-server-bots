namespace CEM.Fields {
  /// <summary>
  /// A range defined by a min, max, and percentage value.
  /// </summary>
  internal class WrapAroundRange : Range {
    protected override float GetClippedValue(float input) {
      float range = Max - Min;
      input = (input - Min)%(range);
      while (input < 0) input += range;
      return input + Min;
    }
  }
}
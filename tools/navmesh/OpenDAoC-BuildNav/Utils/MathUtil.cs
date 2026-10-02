namespace CEM.Utils {
  /// <summary>
  /// Math utility
  /// </summary>
  internal static class MathUtil {
    /// <summary>
    /// Linear interpolation
    /// </summary>
    public static float Lerp(float start, float end, float percent) {
      return start*(1.0f - percent) + end*(percent);
    }
  }
}
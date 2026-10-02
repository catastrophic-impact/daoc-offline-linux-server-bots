using CEM.Utils;

namespace CEM.Core {
  /// <summary>
  /// Simple elapsed ticks counter; similar to StopWatch
  /// </summary>
  internal class MilliTimer {
    private float _initTicks = Util.TicksD;

    /// <summary>
    /// Returns the elapsed milliseconds
    /// </summary>
    public float Elapsed {
      get { return Util.TicksD - _initTicks; }
    }

    /// <summary>
    /// Resets this timer
    /// </summary>
    /// <returns></returns>
    public float Reset() {
      float elapsed = Elapsed;
      _initTicks = Util.TicksD;
      return elapsed;
    }
  }
}
using System;

namespace CEM.Core {
  /// <summary>
  /// Simple elapsed ticks counter
  /// </summary>
  internal class ReuseTimer {
    private readonly float _defaultCost;
    private readonly bool _multipleUses;
    private float _money;

    /// <summary>
    /// Default cost
    /// </summary>
    public float DefaultCost {
      get { return _defaultCost; }
    }

    public ReuseTimer(float defaultCost = 0, bool startsUsable = true, bool multipleUses = false) {
      _defaultCost = defaultCost;
      _multipleUses = multipleUses;
      if (startsUsable)
        _money = defaultCost;
    }

    /// <summary>
    /// Returns the time remaining before this timer can be used again
    /// </summary>
    /// <param name="cost"></param>
    /// <returns></returns>
    public float Remaining(float cost = float.NaN) {
      if (float.IsNaN(cost)) cost = _defaultCost;
      return Math.Max(0, cost - _money);
    }

    /// <summary>
    /// Sets the money value manually
    /// </summary>
    /// <param name="money"></param>
    public void SetMoney(float money) {
      _money = money;
    }

    /// <summary>
    /// Takes a tick from this reusetimer
    /// </summary>
    public void Use(float cost) {
      if (float.IsNaN(cost)) cost = _defaultCost;
      _money -= cost;
      if (!_multipleUses)
        _money = 0;
    }

    /// <summary>
    /// Same as Tick(), but will reset the ticks flag
    /// </summary>
    /// <param name="elapsed"></param>
    /// <returns></returns>
    public bool TryReuse(float elapsed, float cost = float.NaN) {
      if (Tick(elapsed, cost)) {
        Use(cost);
        return true;
      }
      return false;
    }

    /// <summary>
    /// Advances this timer and returns true if this timer is reusable, but won't reset the ticks
    /// </summary>
    /// <param name="diff"></param>
    /// <returns></returns>
    public bool Tick(float diff, float cost = 0) {
      _money += diff;
      return Remaining(cost) <= 0;
    }
  }
}
using System;
using System.Collections.Generic;

namespace DOL.GS;

public sealed class AutonomousFrontierThreatPolicy
{
    public const int VisibilityBudget = 8;
    private long _nextScan;
    private int _cursor;
    private bool _secondaryPending;

    // Defenders finish a bounded sweep for people/pets before considering an
    // engine. A visible ram must not reset the scan and hide later combatants.
    public IReadOnlyList<T> VisiblePriority<T>(IReadOnlyList<T> primary, IReadOnlyList<T> secondary, Func<T, bool> visible)
    {
        var result = new List<T>(VisibilityBudget);
        int remaining = VisibilityBudget;
        if (!_secondaryPending && primary.Count > 0)
        {
            int start = _cursor % primary.Count;
            int count = Math.Min(remaining, primary.Count - start);
            for (int i = 0; i < count; i++)
                if (visible(primary[start + i])) result.Add(primary[start + i]);
            remaining -= count;
            if (result.Count > 0) { _cursor = 0; return result; }
            if (start + count < primary.Count) { _cursor = start + count; return result; }
            _cursor = 0;
            if (remaining == 0) { _secondaryPending = true; return result; }
        }
        _secondaryPending = false;
        for (int i = 0; i < Math.Min(remaining, secondary.Count); i++)
            if (visible(secondary[i])) result.Add(secondary[i]);
        return result;
    }

    public bool Due(long now, long key)
    {
        if (now < _nextScan) return false;
        _nextScan = now + 1000 + (int)(unchecked((ulong)key) % 250);
        return true;
    }

    // Rotate after a blocked window so a few enemies behind a wall cannot
    // permanently hide visible opponents farther down the nearby list.
    public IReadOnlyList<T> Visible<T>(IReadOnlyList<T> nearest, Func<T, bool> visible)
    {
        var result = new List<T>(VisibilityBudget);
        if (nearest.Count == 0) { _cursor = 0; return result; }
        int start = _cursor % nearest.Count;
        int count = Math.Min(VisibilityBudget, nearest.Count);
        for (int i = 0; i < count; i++)
        {
            T candidate = nearest[(start + i) % nearest.Count];
            if (visible(candidate)) result.Add(candidate);
        }
        _cursor = result.Count > 0 ? 0 : (start + count) % nearest.Count;
        return result;
    }
}

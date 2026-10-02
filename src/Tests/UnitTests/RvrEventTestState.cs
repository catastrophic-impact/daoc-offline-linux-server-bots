using System.Reflection;

namespace DOL.GS.Tests;

internal static class RvrEventTestState
{
    // Production callers supply partial candidate lists. Tests must explicitly
    // isolate process-wide event state, not rely on missing rows cancelling
    // every event created by the previous fixture.
    internal static void Clear()
    {
        foreach (string name in new[] { "Events", "CarrierEvents", "CarrierTargets", "Cooldowns", "ReleasedForces", "SelectedKeeps", "SelectedRelics", "DefenseAlarms", "DefenseWarnings", "KeepCombatPressure" })
        {
            object value = typeof(AutonomousRvrEventLayer).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            value.GetType().GetMethod("Clear").Invoke(value, null);
        }
        typeof(AutonomousRvrEventLayer).GetField("_nextStragglerSweep", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0L);
        typeof(AutonomousRvrEventLayer).GetField("_nextDefensePulse", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0L);
    }
}

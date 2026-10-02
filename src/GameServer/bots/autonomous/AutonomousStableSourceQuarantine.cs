using System.Numerics;

namespace DOL.GS;

/// <summary>
/// Remembers a failed walking component, not an individual stable master. It
/// deliberately has no timer: another master cannot make the same disconnected
/// source reachable. Real movement, a zone/region change, successful boarding,
/// or watchdog relocation clears it.
/// </summary>
public sealed class AutonomousStableSourceQuarantine
{
    private const int MeaningfulEscapeDistance = 1200;
    private bool _active;
    private ushort _regionId;
    private ushort _zoneId;
    private Vector3 _origin;

    public bool IsActive(ushort regionId, ushort zoneId, Vector3 current)
    {
        if (!_active)
            return false;
        if (_regionId != regionId || _zoneId != zoneId ||
            Vector3.DistanceSquared(_origin, current) > MeaningfulEscapeDistance * MeaningfulEscapeDistance)
        {
            _active = false;
            return false;
        }
        return true;
    }

    public void Mark(ushort regionId, ushort zoneId, Vector3 origin)
    {
        _active = true;
        _regionId = regionId;
        _zoneId = zoneId;
        _origin = origin;
    }

    public void Clear() => _active = false;
}

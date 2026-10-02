using System;
using System.Collections.Generic;
using System.Numerics;

namespace DOL.GS;

/// <summary>Per-request, cooperative KEEP-route work. No worker threads, world writes,
/// or shared-provider changes. A yield is not a failed path. Completed exact queries
/// are replayed on the next slice; partial native corridors retain their nodes.</summary>
public sealed class RvrPlanningNavigation : PathfindingMgrBase
{
    public sealed class Yield : Exception { }
    public sealed class Limit : Exception { }
    public const int SliceMilliseconds = 8;
    public const int MaximumQueries = 8192;
    private readonly IPathfindingMgr _nav;
    private readonly Func<long> _clock;
    private long _sliceStart;
    private long _activeMilliseconds;
    public const long MaximumActiveMilliseconds = 120_000;
    private int _sliceQueries, _queries;
    public int Queries => _queries;
    private readonly Dictionary<(Zone, long, Vector3, Vector3), bool> _corridors = new();
    private readonly Dictionary<(Zone, long, Vector3, float, float, float, EDtPolyFlags, EDtPolyFlags), Vector3?> _points = new();
    private readonly Dictionary<(Zone, long, Vector3, Vector3, EDtPolyFlags, EDtPolyFlags, int), (PathfindingResult, WrappedPathfindingNode[])> _partial = new();

    public RvrPlanningNavigation(IPathfindingMgr nav, Func<long> clock = null)
    { _nav = nav; _clock = clock ?? (() => Environment.TickCount64); }

    public void BeginSlice() { _sliceStart = _clock(); _sliceQueries = 0; }
    public void EndSlice() => _activeMilliseconds += Math.Max(0, _clock() - _sliceStart);
    public void Checkpoint()
    {
        if (_activeMilliseconds >= MaximumActiveMilliseconds) throw new Limit();
        // Check even during memo replay: a large cached search is still bounded.
        if (_sliceQueries > 0 && (_sliceQueries >= 32 || _clock() - _sliceStart >= SliceMilliseconds)) throw new Yield();
    }
    private void Query()
    {
        Checkpoint();
        if (_queries >= MaximumQueries) throw new Limit();
        _queries++; _sliceQueries++;
    }

    public bool CompleteCorridor(Zone zone, Vector3 from, Vector3 to, Func<bool> calculate)
    {
        Checkpoint();
        var key = (zone, NavigationGeometryRevision.Read(zone), from, to);
        if (_corridors.TryGetValue(key, out bool result)) return result;
        result = calculate(); // A yield never stores an incomplete answer as false.
        _corridors[key] = result;
        _partial.Clear();
        return result;
    }

    public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
        EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
    {
        Checkpoint();
        var key = (zone, NavigationGeometryRevision.Read(zone), start, end, filters[0], filters[1], destination.Length);
        if (_partial.TryGetValue(key, out var cached))
        { cached.Item2.AsSpan().CopyTo(destination); return cached.Item1; }
        Query();
        var result = _nav.GetPathStraight(zone, start, end, filters, destination);
        var nodes = result.NodeCount >= 0 && result.NodeCount <= destination.Length
            ? destination[..result.NodeCount].ToArray() : Array.Empty<WrappedPathfindingNode>();
        // Only the currently unfinished corridor needs full native nodes.
        if (_partial.Count >= 128) throw new Limit();
        _partial[key] = (result, nodes);
        return result;
    }
    public override Vector3? GetClosestPoint(Zone zone, Vector3 point, float x, float y, float z, EDtPolyFlags[] filters)
    {
        Checkpoint();
        var key = (zone, NavigationGeometryRevision.Read(zone), point, x, y, z, filters[0], filters[1]);
        if (_points.TryGetValue(key, out var result)) return result;
        Query(); result = float.IsNaN(x) ? _nav.GetClosestPoint(zone, point, filters)
            : _nav.GetClosestPoint(zone, point, x, y, z, filters); _points[key] = result;
        return result;
    }
    public override Vector3? GetClosestPoint(Zone zone, Vector3 point, EDtPolyFlags[] filters)
        => GetClosestPoint(zone, point, float.NaN, 0, 0, filters);
    public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters)
    { Query(); return _nav.HasLineOfSight(zone, start, end, filters); }
    public override bool IsAvailable => _nav.IsAvailable;
    public override bool HasNavmesh(Zone zone) => _nav.HasNavmesh(zone);
    public override EDtPolyFlags[] DefaultFilters => _nav.DefaultFilters;
    public override EDtPolyFlags[] BlockingDoorAvoidanceFilters => _nav.BlockingDoorAvoidanceFilters;
}

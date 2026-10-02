using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>Some outdoor zones connect through opposite capital gates, not
    /// across the mountain edge of their zone rectangles. Use real paired gates.</summary>
    public static class AutonomousCapitalTransit
    {
        public sealed record Plan(DbZonePoint Entry, DbZonePoint Exit);

        public static Plan Choose(Region outdoors, Region capital, Vector3 start, Vector3 goal,
            ushort outdoorId, ushort capitalId, IReadOnlyList<DbZonePoint> legalPoints, IPathfindingMgr nav)
        {
            if (outdoors == null || capital == null || outdoorId == capitalId) return null;
            Zone startZone = outdoors.GetZone((int)start.X, (int)start.Y);
            Zone goalZone = outdoors.GetZone((int)goal.X, (int)goal.Y);
            if (startZone == null || goalZone == null) return null;
            foreach (var entry in legalPoints.Where(p => p.SourceRegion == outdoorId && p.TargetRegion == capitalId)
                         .OrderBy(p => Vector3.DistanceSquared(start, new(p.SourceX, p.SourceY, p.SourceZ))).Take(4))
            {
                Vector3 entrance = new(entry.SourceX, entry.SourceY, entry.SourceZ);
                if (outdoors.GetZone(entry.SourceX, entry.SourceY) != startZone ||
                    !AutonomousZoneItinerary.HasCompleteCorridor(nav, startZone, start, entrance)) continue;
                Vector3 cityStart = new(entry.TargetX, entry.TargetY, entry.TargetZ);
                Zone cityZone = capital.GetZone(entry.TargetX, entry.TargetY);
                if (cityZone == null || !nav.HasNavmesh(cityZone)) continue;
                foreach (var exit in legalPoints.Where(p => p.SourceRegion == capitalId && p.TargetRegion == outdoorId)
                             .OrderBy(p => Vector3.DistanceSquared(goal, new(p.TargetX, p.TargetY, p.TargetZ))).Take(4))
                {
                    Vector3 cityEnd = new(exit.SourceX, exit.SourceY, exit.SourceZ);
                    Vector3 outside = new(exit.TargetX, exit.TargetY, exit.TargetZ);
                    Zone exitZone = outdoors.GetZone(exit.TargetX, exit.TargetY);
                    // Never return through the same disconnected outdoor area,
                    // or route around a capital indefinitely without advancing.
                    if (exitZone == null || exitZone == startZone || capital.GetZone(exit.SourceX, exit.SourceY) != cityZone ||
                        Vector3.DistanceSquared(outside, goal) >= Vector3.DistanceSquared(start, goal) ||
                        !AutonomousZoneItinerary.HasCompleteCorridor(nav, cityZone, cityStart, cityEnd)) continue;
                    bool onward = exitZone == goalZone
                        ? AutonomousZoneItinerary.HasCompleteCorridor(nav, exitZone, outside, goal)
                        : AutonomousZoneItinerary.TryNextStep(outdoors, exitZone, goalZone, outside, goal, nav, out _);
                    if (onward) return new(entry, exit);
                }
            }
            return null;
        }
    }
}

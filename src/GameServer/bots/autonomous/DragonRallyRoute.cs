using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DOL.GS;

// Expedition staging only. This is not a global navigation/aggro change.
public static class DragonRallyRoute
{
    public const int Clearance = 2200; // 800 native aggro + formation/pet/travel margin.
    public const int ApproachRadius = 14000;

    public static float SegmentDistanceSquared(Vector3 a, Vector3 b, Vector3 center)
    {
        Vector2 start = new(a.X,a.Y), end = new(b.X,b.Y), c = new(center.X,center.Y);
        Vector2 delta = end-start;
        float t = delta.LengthSquared() < 0.01f ? 0 : Math.Clamp(Vector2.Dot(c-start,delta)/delta.LengthSquared(),0,1);
        return Vector2.DistanceSquared(start+delta*t,c);
    }

    public static bool Outside(Vector3 point, Vector3 center) =>
        Vector2.DistanceSquared(new(point.X,point.Y),new(center.X,center.Y)) >= Clearance*Clearance;

    public static bool TryBuild(IPathfindingMgr nav, Zone zone, Vector3 start, Vector3 goal, Vector3 home, out Vector3[] route)
    {
        route = [];
        if (nav == null || zone == null || !nav.IsAvailable || !Outside(goal,home)) return false;
        bool Leg(Vector3 a, Vector3 b)
        {
            return nav is RvrPlanningNavigation work
                ? work.CompleteCorridor(zone,a,b,()=>CalculateLeg(a,b)) : CalculateLeg(a,b);
        }
        bool CalculateLeg(Vector3 a, Vector3 b)
        {
            if (!Outside(b,home)) return false;
            var nodes = new WrappedPathfindingNode[512];
            var result = nav.GetPathStraight(zone,a,b,nav.DefaultFilters,nodes);
            if (result.Status != PathfindingStatus.PathFound || result.NodeCount <= 0 ||
                Vector3.DistanceSquared(nodes[result.NodeCount-1].Position,b) > 80*80) return false;
            Vector3 previous = a;
            for (int i=0;i<result.NodeCount;i++)
            {
                if (!SafeSegment(previous,nodes[i].Position,home)) return false;
                previous=nodes[i].Position;
            }
            return SafeSegment(previous,b,home);
        }
        if (Leg(start,goal)) { route=[goal]; return true; }
        // Waypoints, not just endpoints, must go around the encounter. Reusing
        // a bare "walkable" path here used to cut through the sleeping dragon.
        const int count=24;
        double angle=Math.Atan2(start.Y-home.Y,start.X-home.X);
        int nearest=((int)Math.Round(angle/(2*Math.PI/count))+count)%count;
        foreach(int radius in new[]{3600,4800,6000})
        {
            var ring=new Vector3?[count];
            for(int i=0;i<count;i++)
            {
                double theta=i*2*Math.PI/count;
                Vector3 raw=home+new Vector3((float)Math.Cos(theta)*radius,(float)Math.Sin(theta)*radius,0);
                var floor=nav.GetClosestPoint(zone,raw,96,96,4096,nav.DefaultFilters);
                if(floor.HasValue && Outside(floor.Value,home)) ring[i]=floor;
            }
            foreach(int offset in new[]{0,1,-1,2,-2,3,-3,4,-4,6,-6,12})
            foreach(int direction in new[]{1,-1})
            {
                var steps=new List<Vector3>();
                Vector3 previous=start;
                for(int step=0;step<count;step++)
                {
                    int index=(nearest+offset+direction*step+count*2)%count;
                    if(!ring[index].HasValue || !Leg(previous,ring[index].Value)) break;
                    previous=ring[index].Value; steps.Add(previous);
                    if(Leg(previous,goal)) { steps.Add(goal); route=steps.ToArray(); return true; }
                }
            }
        }
        return false;
    }

    // A bot displaced inside the margin by genuine combat can still leave it.
    // Each segment must move outward, never closer to the encounter.
    public static bool SafeSegment(Vector3 a, Vector3 b, Vector3 home)
    {
        if (Outside(a,home)) return SegmentDistanceSquared(a,b,home) >= Clearance*Clearance;
        Vector2 radial=new(a.X-home.X,a.Y-home.Y), delta=new(b.X-a.X,b.Y-a.Y);
        return Vector2.Dot(radial,delta)>=0;
    }

    public static bool AtAssembly(Vector3 position, IEnumerable<Vector3> posts, Vector3? dragonHome = null) =>
        (!dragonHome.HasValue || Outside(position,dragonHome.Value)) &&
        posts.Any(post => Vector3.DistanceSquared(position,post) <= 600*600);
}

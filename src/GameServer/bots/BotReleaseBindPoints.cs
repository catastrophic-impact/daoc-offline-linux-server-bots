using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>Immutable region snapshots, populated by the existing world-load query.
    /// Death timers must never block on SQLite or race against character saves.</summary>
    public static class BotReleaseBindPoints
    {
        private sealed record Bind(Point3D Point, eRealm Realm);
        private static readonly ConcurrentDictionary<ushort, Bind[]> Regions = new();

        public static void Replace(ushort region, IEnumerable<DbBindPoint> points) =>
            Regions[region] = points.Select(p => new Bind(new Point3D(p.X, p.Y, p.Z), Owner(region, (eRealm)p.Realm))).ToArray();

        public static void Replace(Region region, IEnumerable<DbBindPoint> points, IPathfindingMgr nav)
        {
            // Once at startup, not every death. Reject mesh islands/prop tops
            // instead of repeatedly releasing bots onto the same bad bind.
            Regions[region.ID] = points.Select(p => new Bind(Resolve(nav, region.GetZone(p.X, p.Y),
                    new(p.X, p.Y, p.Z)), Owner(region.ID, (eRealm)p.Realm)))
                .Where(p => p.Point != null).ToArray();
        }

        // Frontier access is not permission to bind at the host realm's border
        // keep. Classic data often leaves those stones tagged Realm=None.
        // Explicit realm tags still allow the invader's own portal bindstones.
        public static eRealm Owner(ushort region, eRealm declaredRealm) => declaredRealm != eRealm.None
            ? declaredRealm : AutonomousWorldBotController.ProtectedRealm(region, ushort.MaxValue);

        public static bool IsEnemyBindPosition(ushort region, int x, int y, int z, eRealm realm) =>
            realm != eRealm.None && Regions.TryGetValue(region, out Bind[] points) &&
            points.Any(bind => bind.Realm != eRealm.None && bind.Realm != realm &&
                System.Math.Abs((long)z - bind.Point.Z) <= 128 &&
                System.Math.Pow((double)x - bind.Point.X, 2) + System.Math.Pow((double)y - bind.Point.Y, 2) <= 96 * 96);

        public static Point3D? Resolve(IPathfindingMgr nav, Zone zone, Vector3 anchor)
        {
            if (zone == null) return null;
            if (!nav.IsAvailable || !nav.HasNavmesh(zone))
                return new Point3D((int)anchor.X, (int)anchor.Y, (int)anchor.Z);
            Vector3? floor = nav.GetClosestPoint(zone, anchor, 48, 48, 128, nav.DefaultFilters);
            if (!floor.HasValue || !AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value))
                return null;
            return new Point3D((int)System.Math.Round(floor.Value.X),
                (int)System.Math.Round(floor.Value.Y), (int)System.Math.Round(floor.Value.Z));
        }

        public static Point3D? Nearest(ushort region, int x, int y, eRealm realm = eRealm.None)
        {
            if (!Regions.TryGetValue(region, out Bind[] points)) return null;
            Point3D? nearest = null;
            double best = double.MaxValue;
            foreach (Bind bind in points)
            {
                // Unclassified stones fail closed for autonomous bots. The
                // existing safe-capital fallback handles a missing friendly bind.
                if (realm != eRealm.None && bind.Realm != realm) continue;
                Point3D point = bind.Point;
                double dx = (double)x - point.X, dy = (double)y - point.Y;
                double distance = dx * dx + dy * dy;
                if (distance >= best) continue;
                best = distance;
                nearest = point;
            }
            return nearest == null ? null : new Point3D(nearest.X, nearest.Y, nearest.Z);
        }
    }
}

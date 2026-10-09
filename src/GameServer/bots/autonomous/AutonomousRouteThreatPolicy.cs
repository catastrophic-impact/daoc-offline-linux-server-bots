using System;
using System.Numerics;
using DOL.GS.ServerProperties;

namespace DOL.GS
{
    public enum RouteThreatAction { Ignore, Pull, Detour, Retarget }

    /// <summary>
    /// Route threat awareness for gamebots walking through open zones. Bots used to react to
    /// aggro only: walking to a distant camp they strolled into aggressive monsters on the way
    /// and casters died in melee. On 2026-10-03..05, 55-71% of solo attempts that had a death
    /// died before reaching their camp. Now a travelling bot (solo, or a group leader) looks a
    /// short way down its real route every few seconds and, for the first aggressive monster
    /// whose aggro range crosses it: pulls it on its own terms when it is an even fight (casters
    /// open from spell range instead of being jumped), goes around it when it is too strong or
    /// part of a pack, and as a last resort gives up the camp. Dungeons keep their own corridor
    /// logic (GuardDungeonTravel).
    /// </summary>
    public static class AutonomousRouteThreatPolicy
    {
        [ServerProperty("autonomous", "bot_route_threat_awareness",
            "Gamebots look ahead along their route in open zones and pull, avoid or route around aggressive monsters before walking into them.", true)]
        public static bool Enabled = true;

        public const int ScanMilliseconds = 2_500;
        public const float LookAhead = 1_500f;
        public const int ScanRadius = 1_800;
        public const int MeleeMargin = 80;
        public const int RangedMargin = 220;
        public const int DetourClearance = 120;
        /// <summary>Monsters this close to the destination are the camp itself; camp work handles them.</summary>
        public const float CampSkipRadius = 700f;
        public const int MaxDetoursPerThreat = 2;
        public const long RetargetCooldownMilliseconds = 10 * 60_000;

        /// <summary>
        /// What to do about the first threat on the route. An even fight is pulled; a strong
        /// monster or a pack is avoided when a way around exists (at most twice per monster);
        /// otherwise a manageable one is pulled anyway, and only a hopeless one makes the bot
        /// give up the camp (at most once per 10 minutes, else it carries on as before).
        /// </summary>
        public static RouteThreatAction Decide(ConColor con, bool grouped, int packSize, bool bossLike,
            bool detourAvailable, int detoursSoFar, bool mayRetarget)
        {
            // A grey monster is no threat (and in DAoC rarely aggroes); lower cons are fought in
            // bigger packs. Run 7 (2026-10-06): 14% of give-ups were greys 6+ levels below the
            // bot and many more were green or blue packs of three to five.
            if (con <= ConColor.GREY && !bossLike)
                return RouteThreatAction.Ignore;
            int easyPack = grouped ? con <= ConColor.BLUE ? 5 : 3
                : con <= ConColor.GREEN ? 3 : con <= ConColor.BLUE ? 2 : 1;
            int manageablePack = grouped ? con <= ConColor.BLUE ? 6 : 4
                : con <= ConColor.GREEN ? 5 : con <= ConColor.BLUE ? 3 : 2;
            bool easy = !bossLike && (grouped
                ? con <= ConColor.ORANGE && packSize <= easyPack
                : con <= ConColor.YELLOW && packSize <= easyPack);
            if (easy)
                return RouteThreatAction.Pull;
            if (detourAvailable && detoursSoFar < MaxDetoursPerThreat)
                return RouteThreatAction.Detour;
            bool manageable = !bossLike && (grouped
                ? con <= ConColor.RED && packSize <= manageablePack
                : con <= ConColor.ORANGE && packSize <= manageablePack);
            if (manageable)
                return RouteThreatAction.Pull;
            return mayRetarget ? RouteThreatAction.Retarget : RouteThreatAction.Ignore;
        }

        /// <summary>
        /// The point on the route (within the look-ahead) nearest to a position, and the route's
        /// horizontal direction there.
        /// </summary>
        public static bool NearestOnRoute(ReadOnlySpan<Vector3> route, Vector3 position, float lookAhead,
            out Vector3 point, out Vector2 direction)
        {
            point = default;
            direction = default;
            float best = float.MaxValue, travelled = 0;
            for (int i = 1; i < route.Length && travelled < lookAhead; i++)
            {
                Vector3 start = route[i - 1], delta = route[i] - start;
                float length = delta.Length();
                if (length < 0.01f) continue;
                float available = Math.Min(length, lookAhead - travelled);
                float t = Math.Clamp(Vector3.Dot(position - start, delta) / (length * length), 0, available / length);
                Vector3 nearest = start + delta * t;
                float d = Vector3.DistanceSquared(nearest, position);
                if (d < best)
                {
                    best = d;
                    point = nearest;
                    Vector2 flat = new(delta.X, delta.Y);
                    direction = flat.LengthSquared() > 0.01f ? Vector2.Normalize(flat) : Vector2.UnitY;
                }
                travelled += length;
            }
            return best < float.MaxValue;
        }

        /// <summary>
        /// The two sidestep points that keep a bot outside a monster's aggro range: beside the
        /// monster, perpendicular to the route, at aggro range plus margin plus clearance.
        /// </summary>
        public static (Vector3 Left, Vector3 Right) DetourPoints(Vector3 threat, Vector2 routeDirection,
            float aggroRange, int margin, float floorZ) =>
            (DetourPoint(threat, routeDirection, aggroRange, margin, floorZ, 1, 0),
             DetourPoint(threat, routeDirection, aggroRange, margin, floorZ, -1, 0));

        /// <summary>
        /// A sidestep point on one side of the monster (side +1 or -1), shifted along the route by
        /// <paramref name="alongFraction"/> times the sidestep distance (negative = before it).
        /// </summary>
        public static Vector3 DetourPoint(Vector3 threat, Vector2 routeDirection, float aggroRange, int margin,
            float floorZ, int side, float alongFraction)
        {
            Vector2 perpendicular = new(-routeDirection.Y, routeDirection.X);
            float distance = aggroRange + margin + DetourClearance;
            return new(threat.X + (perpendicular.X * side + routeDirection.X * alongFraction) * distance,
                       threat.Y + (perpendicular.Y * side + routeDirection.Y * alongFraction) * distance, floorZ);
        }

        /// <summary>The fractions along the route tried for each side, most direct first.</summary>
        public static readonly float[] DetourAlongFractions = [0f, -0.5f, 0.5f, -1f];

        /// <summary>
        /// Pure rule: both straight legs of a detour (start to sidestep, sidestep to the route
        /// past the monster) stay at least <paramref name="radius"/> from it horizontally.
        /// </summary>
        public static bool LegsClear(Vector3 start, Vector3 sidestep, Vector3 rejoin, Vector3 threat, float radius) =>
            SegmentDistance2D(start, sidestep, threat) >= radius && SegmentDistance2D(sidestep, rejoin, threat) >= radius;

        public static float SegmentDistance2D(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector2 a2 = new(a.X, a.Y), b2 = new(b.X, b.Y), p2 = new(p.X, p.Y), ab = b2 - a2;
            float lengthSquared = ab.LengthSquared();
            float t = lengthSquared < 0.01f ? 0 : Math.Clamp(Vector2.Dot(p2 - a2, ab) / lengthSquared, 0, 1);
            return Vector2.Distance(a2 + ab * t, p2);
        }
    }
}

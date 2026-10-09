using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DOL.GS
{
    /// <summary>
    /// Candidate spots for siege engines, following how players used them (owner 2026-10-07):
    /// - a ram cannot be rolled; it is summoned 200-500 in front of a door (its range: light 400) and only hits that
    ///   door, at most two rams per door. The outer gate needs one, the inner keep door another;
    /// - catapults (1,000-3,000) and trebuchets (2,000-5,000) lob ground-targeted shots in an arc (no line of sight),
    ///   hitting everything within 150 of the impact; catapults double against people, trebuchets triple against doors;
    /// - ballistas (50-4,000) fire directly at one target.
    /// Attackers set up on the outer side of the target (away from the keep's centre), defenders on the inner side.
    /// Pure geometry here; the caller projects each point onto the navmesh and checks the route.
    /// </summary>
    public static class SiegePlacement
    {
        /// <summary>Ram spots: distances inside the light ram's reach (200..375), straight out from the door first.</summary>
        public static readonly int[] RamDistances = [260, 300, 230, 340, 360];
        private static readonly float[] RamOffsets = [0f, 0.2f, -0.2f, 0.4f, -0.4f, 0.6f, -0.6f];

        /// <summary>The door's outward unit normal: of the two faces, the one whose side lies farther from the keep's centre.</summary>
        /// <remarks>An inner door passes <paramref name="approach"/> (the outer gate): its outer face is the one toward
        /// the gate. Owner tests 2026-10-07: a keep's centre point lies out in the courtyard, so for the keep door
        /// "away from the centre" pointed into the keep building, and every ram spot sat behind the closed keep door
        /// (Nottmoor, Sursbrooke, Erasleigh, Benowyc).</remarks>
        public static Vector2 OutwardNormal(Vector2 door, ushort heading, Vector2 keepCentre, Vector2? approach = null)
        {
            double angle = heading * 2 * Math.PI / 4096;
            var normal = new Vector2((float)-Math.Sin(angle), (float)Math.Cos(angle));
            if (approach is Vector2 from && Vector2.DistanceSquared(from, door) > 1)
                return Vector2.Dot(normal, from - door) >= 0 ? normal : -normal;
            return Vector2.Distance(door + normal * 300, keepCentre) >= Vector2.Distance(door - normal * 300, keepCentre)
                ? normal : -normal;
        }

        public static IEnumerable<Vector3> RamCandidates(Vector3 door, ushort heading, Vector2 keepCentre, Vector2? approach = null)
        {
            Vector2 outward = OutwardNormal(new(door.X, door.Y), heading, keepCentre, approach);
            foreach (int distance in RamDistances)
                foreach (float offset in RamOffsets)
                {
                    Vector2 direction = Rotate(outward, offset);
                    yield return new(door.X + direction.X * distance, door.Y + direction.Y * distance, door.Z);
                }
        }

        /// <summary>Firing ring for artillery: attackers on the side away from the keep, defenders inside it; nearest
        /// to the operator first.</summary>
        public static IEnumerable<Vector3> ArtilleryCandidates(Vector3 target, Vector2 keepCentre, Vector2 operatorPosition,
            int minRange, int maxRange, bool attacking)
        {
            Vector2 at = new(target.X, target.Y);
            float targetToCentre = Vector2.Distance(at, keepCentre);
            var points = new List<(Vector3 Point, float Score)>();
            int[] radii = RingRadii(minRange, maxRange);
            for (int bearing = 0; bearing < 24; bearing++)
            {
                double angle = bearing * Math.PI / 12;
                Vector2 direction = new((float)Math.Cos(angle), (float)Math.Sin(angle));
                foreach (int radius in radii)
                {
                    Vector2 spot = at + direction * radius;
                    bool outside = Vector2.Distance(spot, keepCentre) > targetToCentre;
                    if (outside != attacking) continue;
                    points.Add((new(spot.X, spot.Y, target.Z), Vector2.Distance(spot, operatorPosition)));
                }
            }
            return points.OrderBy(p => p.Score).Select(p => p.Point);
        }

        /// <summary>Radii comfortably inside the engine's band (clear of the minimum, short of the maximum).</summary>
        public static int[] RingRadii(int minRange, int maxRange)
        {
            // Five distances (run 24: three rings on hilly ground left a catapult crew with no level, floored spot).
            int low = minRange + 150, high = Math.Max(low, Math.Min(maxRange - 200, minRange + 1600));
            return [low, low + (high - low) / 4, (low + high) / 2, low + (high - low) * 3 / 4, high];
        }

        public static (int Min, int Max) Range(BotSiegeKind kind) => kind switch
        {
            BotSiegeKind.Trebuchet => (2000, 5000),
            BotSiegeKind.Catapult => (1000, 3000),
            BotSiegeKind.Ballista => (50, 4000),
            _ => (210, 365), // light ram: reach 400, bots fire within 375 and no closer than 200
        };

        /// <summary>Catapults and trebuchets arc their shots onto a ground target; no line of sight is needed.</summary>
        public static bool Arcing(BotSiegeKind kind) => kind is BotSiegeKind.Catapult or BotSiegeKind.Trebuchet;

        private static Vector2 Rotate(Vector2 v, float radians)
        {
            float c = MathF.Cos(radians), s = MathF.Sin(radians);
            return new(v.X * c - v.Y * s, v.X * s + v.Y * c);
        }
    }
}

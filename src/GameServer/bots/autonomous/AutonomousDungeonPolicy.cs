using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DOL.GS
{
    public static class AutonomousDungeonPolicy
    {
        public readonly record struct GroupTransitMember(ushort Region, Vector3 Position, bool OnStableRoute);

        public const float DungeonEntranceStagingRadius = 700;
        // These are the installed Old Frontiers branches, not the post-1.65
        // Passage of Conflict (244), or the unused client aliases 245/247.
        public static bool IsSharedFrontierDungeon(ushort region) => region is 246 or 248 or 276 or 277;
        public static bool IsSharedCombatDungeon(ushort region) => IsSharedFrontierDungeon(region) || region == 249;
        public static bool IsSupportedDungeonZone(ushort zone) => zone is
            19 or 21 or 22 or 23 or 24 or 50 or 60 or 61 or 62 or
            125 or 126 or 127 or 128 or 129 or 150 or 160 or 161 or
            180 or 190 or 191 or 220 or 221 or 222 or 223 or 224 or
            246 or 248 or 276 or 277 ||
            zone == 249 && AutonomousDarknessFallsNavigation.IsReady;

        public static bool IsStarterDungeonRegion(ushort region) => region is 21 or 129 or 221;

        private static readonly Vector3 CursedTombArrival = new(31700, 33000, 16000);

        public static bool IsCursedTombEntryStage(ushort leaderRegion, Vector3 leaderPosition) =>
            leaderRegion != 128 || AtCursedTombEntrance(leaderPosition);

        private static bool AtCursedTombEntrance(Vector3 point) =>
            MathF.Abs(point.Z - CursedTombArrival.Z) <= 500 &&
            Vector2.DistanceSquared(new(point.X, point.Y),
                new(CursedTombArrival.X, CursedTombArrival.Y)) <= 2_000 * 2_000;

        /// <summary>
        /// On a formed party's first Cursed Tomb assignment, favor the verified
        /// entrance-room spawn cells it can start killing immediately. In the
        /// overnight run, distant initial goals left otherwise productive
        /// parties fighting the respawning entry pack without target credit.
        /// The caller supplies only already-legal, level-valid camps and turns
        /// this preference off once real interior combat progress is observed.
        /// Other dungeons, solo goals and the later Cursed Tomb pool are intact.
        /// </summary>
        public static T[] PreferFirstCursedTombGroupCamps<T>(IEnumerable<T> candidates,
            bool formedGroup, bool hasInteriorProgress, Func<T, ushort> region,
            Func<T, Vector3> position)
        {
            T[] available = candidates?.ToArray() ?? [];
            if (!formedGroup || hasInteriorProgress || region == null || position == null)
                return available;
            bool AtEntrance(T camp) => region(camp) == 128 &&
                AtCursedTombEntrance(position(camp));
            if (!available.Any(AtEntrance)) return available;
            return available.Where(camp => region(camp) != 128 || AtEntrance(camp)).ToArray();
        }

        // Nisse's two haunt packs mix level-18 and level-29 creatures deep in
        // the same branch. The live audit showed hundreds of bots clearing
        // unrelated corridor mobs while never producing a valid haunt kill.
        // They remain real monsters and normal route threats, but are not a
        // reliable autonomous camp objective.
        public static bool IsReliableAutonomousGoal(ushort region, string name) =>
            (region != AutonomousDarknessFallsPolicy.RegionId || AutonomousDarknessFallsNavigation.IsReady) &&
            !(region == 129 && string.Equals(name, "haunt", StringComparison.OrdinalIgnoreCase)) &&
            // Cursed Tomb's two level-13/14 cave spiders are behind the
            // entrance's five level-19/20 roaming corpses. The overnight
            // run repeatedly left solo bots fighting those respawns at
            // 31700,33000 without reaching a spider. Keep the actual mobs
            // for players; exclude only this unsafe autonomous objective.
            !(region == 128 && string.Equals(name, "cave spider", StringComparison.OrdinalIgnoreCase)) &&
            // Installed entrance-to-husk corridors cross aggressive level
            // 36-43 packs. Level 10-11 husks cannot be safe XP goals for the
            // low-level bots that qualify for them. Do not alter any monsters.
            !(region == 125 && string.Equals(name, "husk", StringComparison.OrdinalIgnoreCase)) &&
            // Summoner's Hall is reachable only through Dodens Gruva, Hall of the
            // Corrupt or Marfach Caverns. Groups spent their whole task fighting
            // through those first: 1 of 268 attempts arrived in one evening run.
            // Its monsters and routes stay for players; bots never target it.
            region != SummonersHallRegion;

        public const ushort SummonersHallRegion = 248;

        // A monster may be geometrically close to the route through a wall or
        // from a disconnected room polygon.  Visibility alone is insufficient:
        // only force a route-clearing pull when both endpoints project to the
        // installed mesh and the bot can actually reach that monster.
        public static bool CanSelectRouteBlocker(bool botFloorResolved,
            bool blockerFloorResolved, bool completeCorridor) =>
            botFloorResolved && blockerFloorResolved && completeCorridor;

        // The party member at the front of a formation is not necessarily the
        // designated tank-puller.  A legal corridor blocker may be handed to
        // that tank while the current PvE roster is viable and actively
        // travelling or grinding. Attendance may already have removed one or
        // more no-shows before the dungeon task starts. Resource, casualty and
        // cohesion gates are still applied by CanInitiateNewPull.
        public static bool CanHandoffRouteBlocker(bool groupPve, string phase,
            int memberCount, bool requiredComposition, bool differentPuller,
            bool pullerAlive, bool sameRegion, bool pullerOnStableRoute) =>
            groupPve && phase is "Traveling" or "Grinding" &&
            memberCount is >= 3 and <= 8 && requiredComposition && differentPuller &&
            pullerAlive && sameRegion && !pullerOnStableRoute;

        /// <summary>
        /// Before the first party member uses a dungeon entrance, every living
        /// member must be standing on the source side near that same entrance.
        /// Once the crossing has begun, the remaining staged members may follow.
        /// </summary>
        public static bool GroupReadyForDungeonEntrance(ushort sourceRegion, ushort targetRegion,
            Vector3 source, IReadOnlyList<GroupTransitMember> members)
        {
            if (members == null || members.Count < 2)
                return true;
            if (members.Any(member => member.Region == targetRegion))
                return true;
            float radiusSquared = DungeonEntranceStagingRadius * DungeonEntranceStagingRadius;
            return members.All(member => !member.OnStableRoute && member.Region == sourceRegion &&
                Math.Abs(member.Position.Z - source.Z) <= 500 &&
                Vector2.DistanceSquared(new(member.Position.X, member.Position.Y), new(source.X, source.Y)) <= radiusSquared);
        }

        public static bool GroupReadyAtInteriorStaging(ushort targetRegion, Vector3 staging,
            IReadOnlyList<GroupTransitMember> members, float radius = 350)
        {
            if (members == null || members.Count < 2)
                return true;
            float radiusSquared = radius * radius;
            return members.All(member => !member.OnStableRoute && member.Region == targetRegion &&
                Math.Abs(member.Position.Z - staging.Z) <= 500 &&
                Vector2.DistanceSquared(new(member.Position.X, member.Position.Y),
                    new(staging.X, staging.Y)) <= radiusSquared);
        }

        public static bool CanEngageLocalOpponent(eRealm attacker, eRealm target,
            ushort attackerRegion, ushort targetRegion, bool alive, bool allowed) =>
            IsSharedCombatDungeon(attackerRegion) && attackerRegion == targetRegion &&
            attacker != eRealm.None && target != eRealm.None && attacker != target && alive && allowed;

        // A follower that was already separated by a death, reconnect, or old
        // transient-leader handoff must be able to clear a legal blocker while
        // walking back to its leader. Normal outbound dungeon pulls remain
        // leader-only, so this cannot make a healthy formation fan out and pull.
        public static bool IsFollowerRejoinDestination(bool dynamicGroup, bool isFollower,
            ushort followerRegion, ushort leaderRegion, Vector3 followerPosition,
            Vector3 destination, Vector3 leaderPosition) =>
            dynamicGroup && isFollower && followerRegion == leaderRegion &&
            Vector3.DistanceSquared(followerPosition, leaderPosition) > 650 * 650 &&
            Vector3.DistanceSquared(destination, leaderPosition) <= 220 * 220;

        // Distance to the real, winding, 3-D corridor; never a straight line
        // through the dungeon's walls/floors toward a distant camp.
        public static bool IntersectsCorridor(ReadOnlySpan<Vector3> route, Vector3 position,
            float radius, float lookAhead, out float along)
        {
            along = float.MaxValue;
            float travelled = 0;
            for (int i = 1; i < route.Length && travelled < lookAhead; i++)
            {
                Vector3 start = route[i - 1], delta = route[i] - start;
                float length = delta.Length();
                if (length < 0.01f) continue;
                float available = Math.Min(length, lookAhead - travelled);
                float t = Math.Clamp(Vector3.Dot(position - start, delta) / (length * length), 0, available / length);
                Vector3 nearest = start + delta * t;
                // Aggro across another floor must not become a route pull.
                if (Math.Abs(nearest.Z - position.Z) <= 160 && Vector3.DistanceSquared(nearest, position) <= radius * radius)
                {
                    along = travelled + t * length;
                    return true;
                }
                travelled += length;
            }
            return false;
        }
    }
}

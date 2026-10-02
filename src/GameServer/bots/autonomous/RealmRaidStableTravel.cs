using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.GS.Movement;

namespace DOL.GS
{
    /// <summary>One real ticket leg shared by an expedition's ordinary eight-person party.</summary>
    internal static class RealmRaidStableTravel
    {
        internal enum Action { Walk, Wait, Board }
        internal sealed record Order(Action Action, Vector3 Position, AutonomousStableRoutePlanner.Choice Choice);
        private sealed class Trip
        {
            public readonly object Sync = new();
            public GameBot[] Members;
            public AutonomousStableRoutePlanner.Choice Choice;
            public Vector3[] Boarding, Arrival;
            public Vector3 End;
            public ushort Region;
            public long Deadline, NextBoard, RetryAfter;
            public readonly bool[] Boarded = new bool[8];
            public bool BoardingStarted, Finished;
        }
        private static readonly ConditionalWeakTable<Group, Trip> Trips = new();
        private static readonly ConditionalWeakTable<GameStableMaster, RealmRaidBoardingQueue> DepartureLanes = new();

        public static bool TryStart(GameBot leader, AutonomousStableRoutePlanner.Choice choice, long now)
        {
            Group group = leader?.Group;
            if (group == null || choice?.Ticket == null || AutonomousRealmRaid.GetView(group) == null ||
                choice.Master?.CurrentRegion != leader.CurrentRegion || choice.Route?.Next == null) return false;
            if (Trips.TryGetValue(group, out var previous))
                lock (previous.Sync)
                {
                    if (!previous.Finished) return true;
                    if (now < previous.RetryAfter) return false;
                }
            var roster = group.GetMembersInTheGroup();
            GameBot[] members = roster.OfType<GameBot>().ToArray();
            if (members.Length != 8 || roster.Count != 8 ||
                members.Any(b => !b.IsAutonomousWorldBot || b.IsTemporaryGroupHelper || b.Level != 50 ||
                    !b.IsAlive || b.CurrentRegion != leader.CurrentRegion || b.IsOnStableMasterRoute ||
                    AutonomousBotEconomy.GetMoney(b.DatabaseID) < Math.Max(0, choice.Ticket.Price))) return false;
            PathPoint last = choice.Route;
            while (last.Next != null) last = last.Next;
            Vector3 end = new(last.X, last.Y, last.Z);
            var nav = PathfindingProvider.Instance;
            Zone startZone = leader.CurrentRegion.GetZone((int)choice.BoardingPoint.X, (int)choice.BoardingPoint.Y);
            Zone endZone = leader.CurrentRegion.GetZone(last.X, last.Y);
            lock (Trips)
            {
                if (Trips.TryGetValue(group, out var existing))
                    lock (existing.Sync)
                    {
                        if (!existing.Finished) return true;
                        if (now < existing.RetryAfter) return false;
                    }
                // Reserve posts against other active trips, including parties
                // arriving from a different stable. This work happens once per
                // ticket leg, not on every rider's movement turn.
                var occupied = new List<Vector3>();
                foreach (var pair in Trips)
                    lock (pair.Value.Sync)
                        if (!pair.Value.Finished && pair.Value.Region == leader.CurrentRegionID)
                        {
                            occupied.AddRange(pair.Value.Boarding);
                            occupied.AddRange(pair.Value.Arrival);
                        }
                if (!TryAvailablePosts(nav, startZone, choice.BoardingPoint, occupied, out Vector3[] boarding) ||
                    !TryAvailablePosts(nav, endZone, end, occupied.Concat(boarding).ToArray(), out Vector3[] arrival))
                {
                    // A full/obstructed boarding area is not worth probing on
                    // every AI turn. Normal walking/regrouping remains usable.
                    Trips.Remove(group);
                    Trips.Add(group, new Trip { Finished = true, RetryAfter = now + 60_000 });
                    return false;
                }
                var trip = new Trip { Members = members, Choice = choice, Boarding = boarding, Arrival = arrival,
                    End = end, Region = leader.CurrentRegionID, Deadline = now + 5 * 60_000 };
                Trips.Remove(group);
                Trips.Add(group, trip);
            }
            return true;
        }

        internal static bool TryPosts(IPathfindingMgr nav, Zone zone, Vector3 center, out Vector3[] posts)
            => TryAvailablePosts(nav, zone, center, Array.Empty<Vector3>(), out posts);

        internal static bool TryAvailablePosts(IPathfindingMgr nav, Zone zone, Vector3 center,
            IReadOnlyCollection<Vector3> occupied, out Vector3[] posts)
        {
            posts = new Vector3[8];
            if (zone == null || nav?.IsAvailable != true || !nav.HasNavmesh(zone)) return false;
            Vector3[] nearby = occupied.Where(p => Vector3.DistanceSquared(p, center) < 1000 * 1000).ToArray();
            if (nearby.Length >= RealmRaidRecruitmentPolicy.MaximumBots) return false;
            for (int slot = 0; slot < 8; slot++)
            {
                bool found = false;
                for (int attempt = 0; attempt < 3 && !found; attempt++)
                {
                    int ordinal = nearby.Length + slot;
                    double angle = ((nearby.Length == 0 ? slot * 45 : ordinal * 137.5) + attempt * 10) * Math.PI / 180;
                    float radius = 100 + (nearby.Length == 0 ? 0 : 45 * MathF.Sqrt(ordinal)) + attempt * 35;
                    Vector3 raw = center + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
                    Vector3? floor = nav.GetClosestPoint(zone, raw, 24, 24, 64, nav.DefaultFilters);
                    if (!floor.HasValue || Math.Abs(floor.Value.Z - center.Z) > 64 ||
                        posts.Take(slot).Any(p => Vector3.DistanceSquared(p, floor.Value) < 55 * 55) ||
                        nearby.Any(p => Vector3.DistanceSquared(p, floor.Value) < 55 * 55) ||
                        !nav.HasLineOfSight(zone, center, floor.Value, nav.DefaultFilters) ||
                        !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, center, floor.Value) ||
                        !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, floor.Value, center)) continue;
                    posts[slot] = floor.Value;
                    found = true;
                }
                if (!found) return false;
            }
            return true;
        }

        public static bool TryOrder(GameBot bot, long now, out Order order, out string failure)
        {
            order = null; failure = null;
            Group group = bot.Group;
            if (group == null || !Trips.TryGetValue(group, out var trip)) return false;
            bool activeEvent = AutonomousRealmRaid.GetView(group) != null;
            lock (trip.Sync)
            {
                if (trip.Finished) return false;
                int index = Array.IndexOf(trip.Members, bot);
                if (index < 0 || !activeEvent || now >= trip.Deadline || trip.Members.Any(b =>
                    b.Group != group || !b.IsAlive || b.ObjectState != GameObject.eObjectState.Active || b.CurrentRegionID != trip.Region))
                {
                    trip.Finished = true; trip.RetryAfter = now + 120_000;
                    failure = "Expedition ticket leg cancelled: roster, event or bounded travel deadline changed; existing riders finish their real ticket.";
                    return false;
                }
                bool Near(GameBot member, Vector3 point, int radius) => Vector3.DistanceSquared(new(member.X, member.Y, member.Z), point) <= radius * radius;
                if (trip.Boarded.All(b => b) && trip.Members.Select((b, i) => !b.IsOnStableMasterRoute &&
                    (Near(b, trip.End, 250) || Near(b, trip.Arrival[i], 65))).All(arrived => arrived))
                { trip.Finished = true; trip.RetryAfter = now + 20_000; return false; }
                if (trip.Boarded[index])
                {
                    // Each bot reaches this branch only after the ordinary taxi
                    // controller has confirmed arrival. No artificial dismount.
                    int arrivalRadius = Math.Max(500, (int)Vector3.Distance(trip.End, trip.Arrival[index]) + 100);
                    if (!Near(bot, trip.End, arrivalRadius))
                    { trip.Finished = true; trip.RetryAfter = now + 120_000; failure = "Expedition rider did not arrive at its ticket endpoint; normal regroup/recovery retained."; return false; }
                    order = new(Near(bot, trip.Arrival[index], 45) ? Action.Wait : Action.Walk, trip.Arrival[index], trip.Choice);
                    return true;
                }
                if (!trip.BoardingStarted && trip.Members.Select((b, i) => Near(b, trip.Boarding[i], 65) &&
                        !b.InCombat && !b.IsAttacking && !b.IsStunned && !b.IsMezzed &&
                        (b.Brain is not BotBrain brain || !brain.HasAggro)).All(ready => ready))
                {
                    trip.BoardingStarted = true;
                    trip.Deadline = now + (long)(trip.Choice.RideSeconds * 1000) + 4 * 60_000;
                }
                int next = Array.FindIndex(trip.Boarded, b => !b);
                bool turn = trip.BoardingStarted && next == index && now >= trip.NextBoard &&
                    trip.Members.All(b => !b.InCombat && !b.IsAttacking && (b.Brain is not BotBrain brain || !brain.HasAggro));
                // Serializing only within each party still let thirty parties
                // all board at once. Keep one shared, expiring lane at the NPC.
                if (turn && trip.Choice.Master != null)
                    turn = DepartureLanes.GetOrCreateValue(trip.Choice.Master).TryEnter(bot, now);
                Vector3 destination = turn ? trip.Choice.BoardingPoint : trip.Boarding[index];
                order = new(!Near(bot, destination, 40) ? Action.Walk : turn ? Action.Board : Action.Wait, destination, trip.Choice);
                return true;
            }
        }

        public static void Boarded(GameBot bot, bool success, long now)
        {
            if (bot.Group == null || !Trips.TryGetValue(bot.Group, out var trip)) return;
            lock (trip.Sync)
            {
                int index = Array.IndexOf(trip.Members, bot);
                if (trip.Choice.Master != null && DepartureLanes.TryGetValue(trip.Choice.Master, out var lane))
                    lane.Leave(bot, now);
                if (index < 0 || !success) { trip.Finished = true; trip.RetryAfter = now + 120_000; return; }
                trip.Boarded[index] = true;
                trip.NextBoard = now + 750;
                // Other parties share this master. Actual successful boarding
                // is progress; don't expire a productive leg using the first
                // rider's timestamp while later members wait their turn.
                trip.Deadline = Math.Max(trip.Deadline, now + (long)(trip.Choice.RideSeconds * 1000) + 4 * 60_000);
            }
        }

        public static void Cancel(Group group)
        {
            if (group != null && Trips.TryGetValue(group, out var trip))
                lock (trip.Sync) { trip.Finished = true; trip.RetryAfter = GameLoop.GameLoopTime + 120_000; }
        }
    }
}

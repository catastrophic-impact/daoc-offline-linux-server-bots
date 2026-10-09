using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;

namespace DOL.GS
{
    public sealed partial class AutonomousWorldBotController
    {
        private long _nextDungeonScanTick;
        private bool _dungeonTravelHeld;
        private GameNPC _dungeonThreatHoldBlocker;
        private long _dungeonThreatHoldSince;
        public const long DungeonThreatHoldLimitMilliseconds = 90_000;
        private Vector3[] _dungeonProbeNodes;
        private Vector3 _dungeonProbeOrigin;
        private Vector3 _dungeonProbeDestination;
        private long _dungeonProbeExpires;
        private bool _dungeonRouteResting;
        private readonly Dictionary<string, long> _rejectedDungeonCamps = new(StringComparer.Ordinal);
        private long _nextSharedDungeonEnemyScan;

        private void RejectDungeonRoute(GameBot bot, string reason)
        {
            if (_camp != null)
            {
                _rejectedDungeonCamps[_camp.Id] = GameLoop.GameLoopTime + 30 * 60_000;
                AutonomousBotGroupCoordinator.RejectDungeonCamp(bot, _camp.Id);
            }
            AbandonCamp(bot, reason);
        }

        /// <summary>True consumes this travel turn. Real combat takes over via
        /// the common BotBrain; no damage, XP, movement or loot is simulated.</summary>
        private bool GuardDungeonTravel(GameBot bot, Vector3 destination)
        {
            if (bot.CurrentZone?.IsDungeon != true || bot.Brain is not BotBrain brain ||
                bot.IsOnStableMasterRoute || brain.HasAggro || bot.InCombat || bot.IsAttacking)
                return false;
            if (_dungeonRouteResting)
            {
                if (brain.CheckHeals() || HandleCampRecovery(bot)) return true;
                _dungeonRouteResting = false;
            }
            long now = GameLoop.GameLoopTime;
            if (now < _nextDungeonScanTick) return _dungeonTravelHeld;
            _nextDungeonScanTick = now + 750 + bot.ObjectID % 250;
            _dungeonTravelHeld = false;
            IPathfindingMgr nav = PathfindingProvider.Instance;
            if (!nav.HasNavmesh(bot.CurrentZone))
            {
                _dungeonTravelHeld = true;
                RejectDungeonRoute(bot, "Dungeon navigation mesh is unavailable");
                return true;
            }

            Span<Vector3> route = stackalloc Vector3[257];
            route[0] = new(bot.X, bot.Y, bot.Z);
            int count = bot.movementComponent.CopyUpcomingPath(destination, route[1..]) + 1;
            if (count == 1)
            {
                // Cold start/after combat only. Subsequent scans read the
                // continuous movement corridor, without replotting it.
                if (_dungeonProbeNodes != null && now < _dungeonProbeExpires &&
                    Vector3.DistanceSquared(_dungeonProbeOrigin, route[0]) < 64 * 64 &&
                    Vector3.DistanceSquared(_dungeonProbeDestination, destination) < 64 * 64)
                {
                    _dungeonProbeNodes.AsSpan().CopyTo(route[1..]);
                    count += _dungeonProbeNodes.Length;
                }
                else
                {
                    Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[256];
                    PathfindingResult result = nav.GetPathStraight(bot.CurrentZone, route[0], destination, nav.DefaultFilters, nodes);
                    if (result.NodeCount == 0 || result.Status == PathfindingStatus.BufferTooSmall)
                    {
                        _dungeonTravelHeld = true;
                        RejectDungeonRoute(bot, "No navigable dungeon corridor to the next route stage");
                        return true;
                    }
                    for (int i = 0; i < Math.Min(result.NodeCount, nodes.Length); i++)
                        route[count++] = nodes[i].Position;
                    _dungeonProbeNodes = route[1..count].ToArray();
                    _dungeonProbeOrigin = route[0];
                    _dungeonProbeDestination = destination;
                    _dungeonProbeExpires = now + 10_000;
                }
            }

            GameNPC blocker = null;
            float first = float.MaxValue;
            bool botFloorResolved = AutonomousNavigationSurface.TryFloor(
                nav, bot.CurrentZone, route[0], out Vector3 botFloor);
            // The same natural aggro eligibility/radius as the NPC's existing
            // player-facing AI; do not invent larger level-based aggro radii.
            var threats = bot.GetNPCsInRadius(DungeonBlockerRadius)
                .Where(npc => IsExperienceMonster(npc) && npc.IsAlive && npc.Brain is StandardMobBrain mob &&
                    mob.AggroRange > 0 && mob.CanAggroTarget(bot))
                .OrderBy(npc => bot.GetDistanceTo(npc)).Take(24).ToArray();
            foreach (GameNPC npc in threats)
            {
                var mob = (StandardMobBrain)npc.Brain;
                Vector3 position = new(npc.X, npc.Y, npc.Z);
                if (!AutonomousDungeonPolicy.IntersectsCorridor(route[..count], position,
                        mob.AggroRange + 80, 1100, out float along) || along >= first ||
                    !nav.HasLineOfSight(bot.CurrentZone, route[0], position, nav.DefaultFilters))
                    continue;
                bool blockerFloorResolved = AutonomousNavigationSurface.TryFloor(
                    nav, bot.CurrentZone, position, out Vector3 blockerFloor);
                bool completeCorridor = botFloorResolved && blockerFloorResolved &&
                    AutonomousZoneItinerary.HasCompleteCorridor(
                        nav, bot.CurrentZone, botFloor, blockerFloor);
                if (!AutonomousDungeonPolicy.CanSelectRouteBlocker(
                        botFloorResolved, blockerFloorResolved, completeCorridor))
                    continue;
                blocker = npc;
                first = along;
            }
            if (blocker == null) return false;
            _dungeonTravelHeld = true;
            if (_dungeonThreatHoldBlocker != null && _dungeonThreatHoldBlocker != blocker) _dungeonThreatHoldBlocker = null;
            bot.StopMovingOnPath();
            bot.StopMoving();

            int size = Math.Max(1, _groupDirective?.BotMemberCount ?? 1);
            ConColor con = ConLevels.GetConColor(bot.GetConLevel(blocker));
            GameBot leader = _groupDirective?.Leader;
            bool rejoiningLeader = AutonomousDungeonPolicy.IsFollowerRejoinDestination(
                _groupDirective?.IsDynamic == true,
                leader != null && leader != bot,
                bot.CurrentRegionID,
                leader?.CurrentRegionID ?? 0,
                new Vector3(bot.X, bot.Y, bot.Z),
                destination,
                leader == null ? default : new Vector3(leader.X, leader.Y, leader.Z));
            // Every member must be allowed to heal and recover before the
            // designated puller gate is evaluated. Previously followers were
            // returned here first, so one low-resource follower could prevent
            // MembersFullyRecovered forever while the entire party stood at
            // the Mithra/Muire entrance threat.
            if (brain.CheckHeals()) return true;
            if (HandleCampRecovery(bot))
            {
                _dungeonRouteResting = true;
                return true;
            }
            if (_groupDirective?.IsDynamic == true && _groupDirective.Puller != bot &&
                AutonomousBotGroupCoordinator.TryHandoffDungeonBlocker(bot, blocker, out GameBot puller))
            {
                _routeInterruptedByCombat = true;
                SetStatus(bot, $"Clearing dungeon route: {blocker.Name}", GoalText(),
                    $"Designated tank {puller.Name} is advancing on the corridor threat; the party will defend the pull",
                    blocker.Name, _camp?.ZoneName ?? string.Empty);
                return true;
            }
            // Goal selection still owns difficulty and death-based fallback.
            // Once a live aggressive mob physically blocks the only proven
            // dungeon corridor, however, its level cannot turn that route into
            // an enter/leave loop. Stop and clear it with the normal combat and
            // group-defense systems; death handling remains authoritative if
            // the encounter really is too difficult.
            if (!rejoiningLeader && (!AutonomousBotGroupCoordinator.CanInitiateNewPull(bot, corridorBlocker: true) ||
                (_groupDirective?.IsDynamic == true && _groupDirective.Puller != bot)))
            {
                // Bounded: a puller that is dead or elsewhere never comes. After DungeonThreatHoldLimit
                // on the same blocker the member clears it; ordinary group defense joins the fight
                // (109 Summoner's Hall raid members held in Marfach Caverns, 2026-10-06).
                long holdNow = GameLoop.GameLoopTime;
                if (_dungeonThreatHoldBlocker != blocker) { _dungeonThreatHoldBlocker = blocker; _dungeonThreatHoldSince = holdNow; }
                if (holdNow - _dungeonThreatHoldSince < DungeonThreatHoldLimitMilliseconds)
                {
                    SetStatus(bot, "Holding before dungeon threat", GoalText(),
                        "Waiting for the intact group and its designated tank/leader puller before another fight");
                    return true;
                }
            }
            // Count the visible aggro pack for diagnostics. A formerly-safe
            // blocker must still be engaged: rejecting the unrelated world
            // destination here made a bot stranded inside a dungeon select and
            // reject new camps forever without moving or fighting. The earlier
            // per-blocker safety gate remains authoritative, while native aggro
            // and group-defense logic handles any adds that actually join.
            int nearbyAdds = threats.Count(other =>
            {
                if (other == blocker || Math.Abs(other.Z - blocker.Z) > 160 ||
                    other.Brain is not StandardMobBrain otherBrain)
                    return false;

                // Count mobs whose natural aggro envelopes overlap the first
                // pull, not just mobs within an arbitrary 320-unit center
                // distance. Widely based dungeon packs could otherwise all
                // acquire a solo traveler while this check saw only one.
                int sharedAggroEnvelope = Math.Max(320,
                    ((StandardMobBrain)blocker.Brain).AggroRange + otherBrain.AggroRange + 80);
                return other.GetDistanceTo(blocker) < sharedAggroEnvelope &&
                    nav.HasLineOfSight(bot.CurrentZone,
                        new(blocker.X, blocker.Y, blocker.Z),
                        new(other.X, other.Y, other.Z), nav.DefaultFilters);
            });
            if (AutonomousDefensivePull.TryBeginFlyingHandoff(bot, blocker, corridorBlocker: true)) return true;
            if (!SavageBotCombatPolicy.MustMeleePull((eCharacterClass)bot.CharacterClass.ID) &&
                AutonomousDefensivePull.TryBegin(bot, blocker)) return true;
            bot.TargetObject = blocker;
            _lastEngagedCon = con;
            _routeInterruptedByCombat = true;
            AutonomousBotGroupCoordinator.MarkCombatObserved(bot.Group);
            brain.AddToAggroList(blocker, Math.Max(25, blocker.EffectiveLevel * 10));
            brain.CommitDungeonPull(blocker);
            brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
            if (SavageBotCombatPolicy.MustMeleePull((eCharacterClass)bot.CharacterClass.ID))
                BeginSavageMeleePull(bot, blocker);
            SetStatus(bot, $"Clearing dungeon route: {blocker.Name}", GoalText(),
                nearbyAdds > 0
                    ? $"Pulling the first safe corridor threat from a visible pack of {nearbyAdds + 1}; the party will defend any member that draws adds"
                    : "Stopped the live route to clear one visible threat before advancing",
                blocker.Name, _camp?.ZoneName ?? string.Empty);
            return true;
        }

        private static bool AtRegionCrossing(GameBot bot, DOL.Database.DbZonePoint crossing, int horizontalDistance)
        {
            // A Shrouded Isles portal is used from its platform, not from the ground beside it.
            if (ShroudedIslesPortals.TryGetPad(crossing.Id, out Vector3 pad))
                return ShroudedIslesPortals.IsOnPad(pad, new(bot.X, bot.Y, bot.Z));
            if (CanUseProvenDungeonExit(PathfindingProvider.Instance, bot.CurrentZone,
                    new(bot.X, bot.Y, bot.Z), crossing.Id,
                    new(crossing.SourceX, crossing.SourceY, crossing.SourceZ)))
                return true;
            if (horizontalDistance <= ZonePointArrivalRadius && (bot.CurrentZone?.IsDungeon != true ||
                Math.Abs(bot.Z - crossing.SourceZ) <= 160 &&
                PathfindingProvider.Instance.HasLineOfSight(bot.CurrentZone, new(bot.X, bot.Y, bot.Z),
                    new(crossing.SourceX, crossing.SourceY, crossing.SourceZ), PathfindingProvider.Instance.DefaultFilters)))
                return true;

            // These five imported endpoints stop just beyond their last
            // connected polygon. Never widen every portal: only measured,
            // authoritative edges get a tightly bounded same-floor/local-exit
            // exception and still use their unchanged paired destinations.
            if (!IsAuditedShortEndpoint(crossing.Id) ||
                horizontalDistance > AuditedShortEndpointRadius(crossing.Id) || bot.CurrentZone == null)
                return false;
            Zone sourceZone = bot.CurrentRegion?.GetZone(crossing.SourceX, crossing.SourceY);
            if (sourceZone != bot.CurrentZone)
                return false;
            IPathfindingMgr nav = PathfindingProvider.Instance;
            if (!AutonomousNavigationSurface.TryFloor(nav, sourceZone, new(bot.X, bot.Y, bot.Z), out Vector3 current) ||
                Math.Abs(current.Z - crossing.SourceZ) > 160)
                return false;
            Vector3? source = nav.GetClosestPoint(sourceZone,
                new(crossing.SourceX, crossing.SourceY, crossing.SourceZ), 48, 48, 192, nav.DefaultFilters);
            return source.HasValue && CanUseAuditedShortEndpoint(crossing.Id, horizontalDistance,
                Math.Abs(current.Z - crossing.SourceZ), sourceZone == bot.CurrentZone,
                AutonomousRendezvousNavigation.HasLocalExit(nav, sourceZone, current),
                AutonomousRendezvousNavigation.HasLocalExit(nav, sourceZone, source.Value));
        }

        public static bool CanUseProvenDungeonExit(IPathfindingMgr nav, Zone zone, Vector3 actor,
            int crossingId, Vector3 portal)
        {
            // These authoritative exits end outside the eroded walkable mesh.
            // LOS to that raw endpoint is not a valid doorway test. Require a
            // tiny, bidirectionally connected approach on the SAME floor;
            // never apply this exception to arbitrary gates or frontier keeps.
            bool known = (zone?.ZoneRegion?.ID, crossingId) is (223, 57) or (126, 50);
            if (!known || Vector2.Distance(new(actor.X, actor.Y), new(portal.X, portal.Y)) > 190 ||
                Math.Abs(actor.Z - portal.Z) > 160 || !AutonomousNavigationSurface.TryFloor(nav, zone, actor, out Vector3 start))
                return false;
            Vector3? end = nav.GetClosestPoint(zone, portal, 190, 190, 160, nav.DefaultFilters);
            return end.HasValue && Vector3.Distance(start, end.Value) <= 190 &&
                Math.Abs(end.Value.Z - portal.Z) <= 160 &&
                AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, start, end.Value) &&
                AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, end.Value, start);
        }

        public static bool IsAuditedShortEndpoint(int crossingId) => crossingId is 14 or 19 or 22 or 42 or 165;

        public static int AuditedShortEndpointRadius(int crossingId) => crossingId switch
        {
            // Recurrent Connacht arrivals stop 213 units from portal 14.
            14 => 224,
            // TNN -> Lough Derg, Jordheim exit 1, Gotar -> Cursed Tomb,
            // and Mularn -> Aegir's Landing.
            19 or 22 or 42 or 165 => 208,
            _ => 0
        };

        public static bool CanUseAuditedShortEndpoint(int crossingId, int horizontalDistance, float verticalDistance,
            bool sameZone, bool currentHasExit, bool sourceHasExit) =>
            IsAuditedShortEndpoint(crossingId) &&
            horizontalDistance <= AuditedShortEndpointRadius(crossingId) && verticalDistance <= 160 &&
            sameZone && currentHasExit && sourceHasExit;

        public static bool CanUsePortal14Endpoint(int crossingId, int horizontalDistance, float verticalDistance,
            bool sameZone, bool currentHasExit, bool sourceHasExit) =>
            crossingId == 14 && CanUseAuditedShortEndpoint(crossingId, horizontalDistance,
                verticalDistance, sameZone, currentHasExit, sourceHasExit);
    }
}

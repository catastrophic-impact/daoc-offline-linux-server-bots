using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;

namespace DOL.GS
{
    public sealed partial class AutonomousWorldBotController
    {
        private long _nextRouteThreatScanTick;
        private long _lastRouteThreatRetargetTick = long.MinValue / 2;
        private readonly Dictionary<GameNPC, int> _routeThreatDetours = new();
        // Monsters next to the route that cannot be walked to (a ledge above the road): they
        // cannot reach the route either, so they are passed by instead of pulled. Run 8: route
        // pulls on such monsters left bots standing until the 15-minute watchdog.
        private readonly HashSet<GameNPC> _unreachableRouteThreats = new();
        // The last route pull: the same monster found again, still out of combat, means the pull
        // never landed. After three it is passed by like an unreachable one.
        private GameNPC _lastRouteThreatPull;
        private int _routeThreatPullRepeats;
        private long _lastRouteThreatPullTick;
        private const int RouteThreatPullAttempts = 3;

        /// <summary>
        /// Open-world route threat awareness (see AutonomousRouteThreatPolicy). Called right before
        /// a travelling bot issues its walk order. True consumes this travel turn (a pull started,
        /// the bot is holding, or the camp was given up); false lets the walk continue, with a
        /// detour waypoint set when the bot is going around a monster.
        /// </summary>
        private bool GuardOpenWorldTravel(GameBot bot, Vector3 destination)
        {
            if (!AutonomousRouteThreatPolicy.Enabled || bot.Brain is not BotBrain brain ||
                bot.CurrentZone == null || bot.CurrentZone.IsDungeon || bot.CurrentRegion?.IsDungeon == true ||
                bot.IsOnStableMasterRoute || bot.IsPlayerLedGroup || brain.HasAggro || bot.InCombat || bot.IsAttacking ||
                AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR) ||
                AutonomousRealmRaid.GetView(bot.Group) != null)
                return false;
            bool grouped = _groupDirective?.IsDynamic == true;
            if (grouped && _groupDirective.Leader != bot)
                return false;
            long now = GameLoop.GameLoopTime;
            if (now < _nextRouteThreatScanTick)
                return false;
            // Keep looking while walking a detour: the way to the sidestep is scanned too, and a
            // new monster there is fought or makes the bot give up (no detour inside a detour).
            bool detouring = _routeRecoveryWaypoint.HasValue;
            Vector3 scanTarget = _routeRecoveryWaypoint ?? destination;
            _nextRouteThreatScanTick = now + AutonomousRouteThreatPolicy.ScanMilliseconds + bot.ObjectID % 500;

            IPathfindingMgr nav = PathfindingProvider.Instance;
            Zone zone = bot.CurrentZone;
            if (!nav.IsAvailable || !nav.HasNavmesh(zone))
                return false;

            Span<Vector3> route = stackalloc Vector3[257];
            route[0] = new(bot.X, bot.Y, bot.Z);
            int count = bot.movementComponent.CopyUpcomingPath(scanTarget, route[1..]) + 1;
            if (count < 2)
            {
                // Before the first path exists: the straight leg toward the target.
                route[1] = scanTarget;
                count = 2;
            }
            ReadOnlySpan<Vector3> corridor = route[..count];

            eCharacterClass characterClass = (eCharacterClass)(bot.CharacterClass?.ID ?? 0);
            bool ranged = BotSpellPower.IsOffensiveCaster(bot) ||
                          characterClass is eCharacterClass.Scout or eCharacterClass.Hunter or eCharacterClass.Ranger;
            int margin = ranged ? AutonomousRouteThreatPolicy.RangedMargin : AutonomousRouteThreatPolicy.MeleeMargin;

            GameNPC[] threats = bot.GetNPCsInRadius((ushort)AutonomousRouteThreatPolicy.ScanRadius)
                .Where(npc => IsExperienceMonster(npc) && npc.IsAlive && !npc.InCombat &&
                              npc.Brain is StandardMobBrain mob && mob.AggroRange > 0 && mob.CanAggroTarget(bot))
                .OrderBy(npc => bot.GetDistanceTo(npc)).Take(24).ToArray();
            GameNPC blocker = null;
            float first = float.MaxValue;
            foreach (GameNPC npc in threats)
            {
                var mob = (StandardMobBrain)npc.Brain;
                Vector3 position = new(npc.X, npc.Y, npc.Z);
                if (Vector2.Distance(new(position.X, position.Y), new(destination.X, destination.Y)) <=
                    AutonomousRouteThreatPolicy.CampSkipRadius)
                    continue;
                // The monster this detour is already going around.
                if (detouring && _routeThreatDetours.ContainsKey(npc))
                    continue;
                if (_unreachableRouteThreats.Contains(npc))
                    continue;
                if (AutonomousDungeonPolicy.IntersectsCorridor(corridor, position, mob.AggroRange + margin,
                        AutonomousRouteThreatPolicy.LookAhead, out float along) && along < first)
                {
                    blocker = npc;
                    first = along;
                }
            }
            if (blocker == null)
            {
                PruneRouteThreatDetours();
                return false;
            }

            var blockerBrain = (StandardMobBrain)blocker.Brain;
            Vector3 blockerPosition = new(blocker.X, blocker.Y, blocker.Z);
            int pack = 1 + threats.Count(other =>
                other != blocker && Math.Abs(other.Z - blocker.Z) <= 160 && other.Brain is StandardMobBrain otherBrain &&
                other.GetDistanceTo(blocker) < Math.Max(320, blockerBrain.AggroRange + otherBrain.AggroRange + 80));
            ConColor con = ConLevels.GetConColor(bot.GetConLevel(blocker));
            bool bossLike = blocker is IGameEpicNpc ||
                            AutonomousPveTargetPolicy.IsUnreachableFlyer(blocker.Flags, blocker.Z, bot.Z) && !ranged;

            Vector3? detour = detouring ? null : FindRouteThreatDetour(nav, zone, corridor, blocker,
                blockerBrain.AggroRange, margin, destination, threats);
            int detours = _routeThreatDetours.GetValueOrDefault(blocker);
            bool mayRetarget = _camp != null &&
                               now - _lastRouteThreatRetargetTick >= AutonomousRouteThreatPolicy.RetargetCooldownMilliseconds;
            RouteThreatAction action = AutonomousRouteThreatPolicy.Decide(con, grouped, pack, bossLike,
                detour.HasValue, detours, mayRetarget);
            if (action == RouteThreatAction.Ignore)
                return false;

            Log.Info($"AUTONOMOUS_ROUTE_THREAT bot={bot.Name} id={bot.DatabaseID} class=\"{bot.ClassName}\" level={bot.Level} " +
                     $"group={(grouped ? _groupDirective.GroupId : "solo")} action={action} target=\"{blocker.Name}\" " +
                     $"targetLevel={blocker.Level} con={con} pack={pack} aggro={blockerBrain.AggroRange} along={first:0} " +
                     $"detours={detours} zone=\"{zone.Description}\" at={blocker.X},{blocker.Y},{blocker.Z}");

            switch (action)
            {
                case RouteThreatAction.Detour:
                    _routeThreatDetours[blocker] = detours + 1;
                    _routeRecoveryWaypoint = detour.Value;
                    _routeRecoveryArrivalRadius = 60;
                    _nextMoveOrderTick = 0;
                    bot.ForcePathReplot();
                    SetStatus(bot, $"Going around {blocker.Name}", GoalText(),
                        $"Taking a way around the {(pack > 1 ? $"pack of {pack}" : "monster")} on the route instead of walking into its aggro",
                        blocker.Name, _camp?.ZoneName ?? string.Empty);
                    return false;

                case RouteThreatAction.Retarget:
                    _lastRouteThreatRetargetTick = now;
                    AbandonCamp(bot, $"Route blocked by {blocker.Name} (level {blocker.Level}, {pack} together) with no way around");
                    return true;

                default: // Pull
                    if (grouped && !AutonomousBotGroupCoordinator.CanInitiateNewPull(bot))
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                        SetStatus(bot, "Holding before a route threat", GoalText(),
                            $"Waiting for the whole party before clearing {blocker.Name} from the route",
                            blocker.Name, _camp?.ZoneName ?? string.Empty);
                        return true;
                    }
                    if (!AutonomousNavigationSurface.TryFloor(nav, zone, blockerPosition, out Vector3 blockerFloor) ||
                        !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, new(bot.X, bot.Y, bot.Z), blockerFloor))
                    {
                        _unreachableRouteThreats.Add(blocker);
                        Log.Info($"AUTONOMOUS_ROUTE_THREAT_UNREACHABLE bot={bot.Name} id={bot.DatabaseID} target=\"{blocker.Name}\" " +
                                 $"at={blocker.X},{blocker.Y},{blocker.Z} zone=\"{zone.Description}\"");
                        return false;
                    }
                    _routeThreatPullRepeats = blocker == _lastRouteThreatPull && now - _lastRouteThreatPullTick < 45_000
                        ? _routeThreatPullRepeats + 1 : 0;
                    _lastRouteThreatPull = blocker;
                    _lastRouteThreatPullTick = now;
                    if (_routeThreatPullRepeats >= RouteThreatPullAttempts)
                    {
                        _unreachableRouteThreats.Add(blocker);
                        _routeThreatPullRepeats = 0;
                        Log.Info($"AUTONOMOUS_ROUTE_THREAT_PULL_FAILED bot={bot.Name} id={bot.DatabaseID} target=\"{blocker.Name}\" " +
                                 $"attempts={RouteThreatPullAttempts} at={blocker.X},{blocker.Y},{blocker.Z} zone=\"{zone.Description}\"");
                        return false;
                    }
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                    bot.TargetObject = blocker;
                    _lastEngagedCon = con;
                    _routeInterruptedByCombat = true;
                    AutonomousBotGroupCoordinator.MarkCombatObserved(bot.Group);
                    brain.AddToAggroList(blocker, Math.Max(25, blocker.EffectiveLevel * 10));
                    brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    if (SavageBotCombatPolicy.MustMeleePull(characterClass))
                        BeginSavageMeleePull(bot, blocker);
                    SetStatus(bot, $"Clearing the route: {blocker.Name}", GoalText(),
                        ranged
                            ? "Opening from range on a monster ahead on the route instead of walking into its aggro"
                            : "Taking on a monster ahead on the route before it can jump from behind",
                        blocker.Name, _camp?.ZoneName ?? string.Empty);
                    return true;
            }
        }

        /// <summary>
        /// A reachable sidestep beside the monster that stays out of every nearby monster's aggro
        /// range; the shorter way of the two sides wins.
        /// </summary>
        private static Vector3? FindRouteThreatDetour(IPathfindingMgr nav, Zone zone, ReadOnlySpan<Vector3> corridor,
            GameNPC blocker, int aggroRange, int margin, Vector3 destination, GameNPC[] threats)
        {
            Vector3 start = corridor[0];
            Vector3 position = new(blocker.X, blocker.Y, blocker.Z);
            if (!AutonomousRouteThreatPolicy.NearestOnRoute(corridor, position, AutonomousRouteThreatPolicy.LookAhead,
                    out Vector3 routePoint, out Vector2 direction) ||
                !AutonomousNavigationSurface.TryFloor(nav, zone, start, out Vector3 startFloor))
                return null;
            // Rejoin the route a sidestep's length past the monster (or at the destination if nearer).
            float sidestep = aggroRange + margin + AutonomousRouteThreatPolicy.DetourClearance;
            Vector3 past = new(routePoint.X + direction.X * sidestep * 1.5f, routePoint.Y + direction.Y * sidestep * 1.5f, routePoint.Z);
            Vector3 rejoin = Vector3.Distance(routePoint, destination) < Vector3.Distance(routePoint, past) ? destination : past;
            Vector3? best = null;
            float bestCost = float.MaxValue;
            foreach (int side in new[] { 1, -1 })
            foreach (float alongFraction in AutonomousRouteThreatPolicy.DetourAlongFractions)
            {
                Vector3 candidate = AutonomousRouteThreatPolicy.DetourPoint(position, direction, aggroRange, margin,
                    routePoint.Z, side, alongFraction);
                // Quick filter: both straight legs pass outside every nearby monster's aggro.
                if (threats.Any(npc => npc.Brain is StandardMobBrain mob &&
                        !AutonomousRouteThreatPolicy.LegsClear(start, candidate, rejoin, new(npc.X, npc.Y, npc.Z),
                            mob.AggroRange + margin / 2f)))
                    continue;
                Vector3? floor = nav.GetClosestPoint(zone, candidate, 96, 96, 320, nav.DefaultFilters);
                if (!floor.HasValue || zone != blocker.CurrentRegion?.GetZone((int)floor.Value.X, (int)floor.Value.Y))
                    continue;
                bool inAggro = threats.Any(npc => npc.Brain is StandardMobBrain mob &&
                    Math.Abs(npc.Z - floor.Value.Z) <= 200 &&
                    Vector2.Distance(new(npc.X, npc.Y), new(floor.Value.X, floor.Value.Y)) < mob.AggroRange + margin);
                if (inAggro || !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, startFloor, floor.Value) ||
                    !PathClearOfAggro(nav, zone, startFloor, floor.Value, threats, margin) ||
                    !PathClearOfAggro(nav, zone, floor.Value, rejoin, threats, margin))
                    continue;
                float cost = Vector3.Distance(start, floor.Value) + Vector3.Distance(floor.Value, destination);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = floor.Value;
                }
            }
            return best;
        }

        /// <summary>
        /// The real navmesh path between two points stays outside every monster's aggro range
        /// (half the safety margin may be used).
        /// </summary>
        private static bool PathClearOfAggro(IPathfindingMgr nav, Zone zone, Vector3 from, Vector3 to,
            GameNPC[] threats, int margin)
        {
            Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[256];
            PathfindingResult result = nav.GetPathStraight(zone, from, to, nav.DefaultFilters, nodes);
            if (result.NodeCount == 0)
                return false;
            int count = Math.Min(result.NodeCount, nodes.Length);
            Span<Vector3> path = stackalloc Vector3[count + 1];
            path[0] = from;
            float length = 0;
            for (int i = 0; i < count; i++)
            {
                path[i + 1] = nodes[i].Position;
                length += Vector3.Distance(path[i], path[i + 1]);
            }
            foreach (GameNPC npc in threats)
            {
                if (npc.Brain is not StandardMobBrain mob)
                    continue;
                if (AutonomousDungeonPolicy.IntersectsCorridor(path, new(npc.X, npc.Y, npc.Z),
                        mob.AggroRange + margin / 2f, length + 1, out _))
                    return false;
            }
            return true;
        }

        private void PruneRouteThreatDetours()
        {
            if (_unreachableRouteThreats.Count > 0)
            {
                _unreachableRouteThreats.RemoveWhere(npc => !npc.IsAlive || npc.ObjectState != GameObject.eObjectState.Active);
                if (_unreachableRouteThreats.Count > 32) _unreachableRouteThreats.Clear();
            }
            if (_routeThreatDetours.Count == 0)
                return;
            foreach (GameNPC npc in _routeThreatDetours.Keys.Where(npc => !npc.IsAlive ||
                         npc.ObjectState != GameObject.eObjectState.Active).ToArray())
                _routeThreatDetours.Remove(npc);
            if (_routeThreatDetours.Count > 32)
                _routeThreatDetours.Clear();
        }
    }
}

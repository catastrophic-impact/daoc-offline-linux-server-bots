using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using DOL.GS.Movement;
using DOL.GS.Keeps;
using DOL.Logging;
using static DOL.GS.GameNPC;

namespace DOL.GS
{
    public sealed class Pathfinder
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        public const int MIN_TARGET_DIFF_REPLOT_DISTANCE = 64;
        public const int NODE_REACHED_DISTANCE = 16;
        public const int NODE_REACHED_DISTANCE_STRICT = 2;
        public const int NODE_MAX_SKIP_DISTANCE = 80;
        public const int DOOR_SEARCH_DISTANCE = 128;

        public GameNPC Owner { get; }
        public bool ForceReplot { get; set; }
        public PathfindingStatus PathfindingStatus { get; private set; }

        private PathBuffer _activePath = new();
        private PathBuffer _calculationBuffer = new();

        private Vector3 _lastTarget = Vector3.Zero;
        // A closed door with no way around it is remembered (2026-10-17: followers and pets behind a sieged keep's
        // doors re-planned the detour on every follow check, ~1 CPU core).
        private Vector3 _doorDetourFailedTarget;
        private long _doorDetourFailedUntil;
        private long _lastNoPathTick;
        public const int DoorDetourRetryMilliseconds = 5_000;
        public const int RecentFailureMilliseconds = 2_000;
        private static long _nextDoorHoldReportTick;
        private static int _doorHoldsSinceReport;
        private PathVisualization _pathVisualization;

        public static EDtPolyFlags[] DefaultFilters => PathfindingProvider.Instance.DefaultFilters;
        public static EDtPolyFlags[] BlockingDoorAvoidanceFilters => PathfindingProvider.Instance.BlockingDoorAvoidanceFilters;

        public Pathfinder(GameNPC owner)
        {
            ForceReplot = true;
            Owner = owner;
        }

        public void Clear()
        {
            _activePath.Clear();
            _calculationBuffer.Clear();
            _lastTarget = Vector3.Zero;
            PathfindingStatus = PathfindingStatus.NotSet;
            ForceReplot = true;
        }

        /// <summary>Read-only lookahead for dungeon safety. Does not replot,
        /// advance nodes, alter movement, or allocate a second route.</summary>
        public int CopyUpcomingPath(Vector3 target, Span<Vector3> destination)
        {
            if (ForceReplot || !_lastTarget.IsInRange(target, MIN_TARGET_DIFF_REPLOT_DISTANCE))
                return 0;
            int count = Math.Min(destination.Length, _activePath.Nodes.Count);
            for (int i = 0; i < count; i++)
                destination[i] = _activePath.Nodes.Peek(i).Position;
            return count;
        }

        public bool ShouldPath(Zone zone, Vector3 target)
        {
            if (zone == null || !zone.IsPathfindingEnabled)
                return false;

            if ((Owner.Flags & (eFlags.FLYING | eFlags.SWIMMING)) != 0 || Owner is GameTaxi || Owner is GameTaxiBoat)
                return false;

            // Target is in a different zone (TODO: implement this maybe? not sure if really required).
            if (Owner.CurrentRegion.GetZone((int) target.X, (int) target.Y) != zone)
                return false;

            return true;
        }

        private PathfindingStatus CalculatePath(PathBuffer pathBuffer, Zone zone, Vector3 position, Vector3 target, EDtPolyFlags[] filters,
            bool useRoads = false)
        {
            const int MAX_PATH_NODES = 512;
            WrappedPathfindingNode[] rentedNodeBuffer = ArrayPool<WrappedPathfindingNode>.Shared.Rent(MAX_PATH_NODES);

            try
            {
                PathfindingResult pathfindingResult = PathfindingProvider.Instance.GetPathStraight(zone, position, target, filters, rentedNodeBuffer);
                if (useRoads && Owner is GameBot &&
                    TryRoadPath(PathfindingProvider.Instance, zone, position, target, filters, pathfindingResult, rentedNodeBuffer, MAX_PATH_NODES,
                        out PathfindingResult road))
                    pathfindingResult = road;

                pathBuffer.Clear();
                pathBuffer.Origin = position;

                if (Owner is GameBot { IsAutonomousWorldBot: true } &&
                    zone.ZoneRegion?.ID == AutonomousDarknessFallsPolicy.RegionId &&
                    !AutonomousDarknessFallsNavigation.MayQueueRuntimePath(pathfindingResult,
                        rentedNodeBuffer, position, target) &&
                    !AutonomousDarknessFallsNavigation.MayQueueOwnExitGroundPath(Owner.Realm,
                        pathfindingResult, rentedNodeBuffer, position, target))
                {
                    // DF navigation is only safe as a complete certified 3D
                    // corridor. Do not advance along a partial route or an
                    // uncertified Jump/fall while looking for a better path.
                    return PathfindingStatus.NoPathFound;
                }

                if (pathfindingResult.Status is PathfindingStatus.BufferTooSmall)
                {
                    if (log.IsWarnEnabled)
                        log.Warn($"Path buffer for {Owner} was too small. Needed {pathfindingResult.NodeCount}, had {MAX_PATH_NODES}.");
                }

                int nodeCount = Math.Min(pathfindingResult.NodeCount, MAX_PATH_NODES);

                if (nodeCount > 0)
                {
                    // Keep track of doors on the path.
                    // Skip the first node if it isn't a door.
                    for (int i = 0; i < nodeCount; i++)
                    {
                        WrappedPathfindingNode node = rentedNodeBuffer[i];
                        bool isDoor = (node.Flags & EDtPolyFlags.AnyDoor) != 0;

                        if (i == 0 && !isDoor)
                            continue;

                        pathBuffer.Nodes.Enqueue(node);

                        if (!isDoor)
                            continue;

                        Point3D point = new(node.Position.X, node.Position.Y, node.Position.Z);
                        pathBuffer.Doors[node] = new(1);
                        Owner.CurrentRegion.GetInRadius(point, eGameObjectType.DOOR, DOOR_SEARCH_DISTANCE, pathBuffer.Doors[node]);
                    }
                }

                return pathfindingResult.Status;
            }
            finally
            {
                ArrayPool<WrappedPathfindingNode>.Shared.Return(rentedNodeBuffer);
            }
        }

        /// <summary>
        /// A bot's partial path that stops far short of its goal (Detour's search reached a dead end on
        /// a long walk that first leads away from the goal) is replaced by the zone road route: the
        /// Detour paths of its proven legs, joined, as far as the node buffer holds (then partial, and
        /// the usual continuation replots from there). Short walks, DF and zones without roads are unchanged.
        /// </summary>
        public static bool TryRoadPath(IPathfindingMgr nav, Zone zone, Vector3 position, Vector3 target, EDtPolyFlags[] filters,
            PathfindingResult direct, WrappedPathfindingNode[] nodes, int capacity, out PathfindingResult result)
        {
            result = direct;
            if (direct.Status != PathfindingStatus.PartialPathFound || direct.NodeCount < 1 || direct.NodeCount > capacity ||
                zone.ZoneRegion?.ID == AutonomousDarknessFallsPolicy.RegionId ||
                Vector3.DistanceSquared(nodes[direct.NodeCount - 1].Position, target) <= 256 * 256)
                return false;
            if (!AutonomousZoneRoads.TryRoute(nav, zone, position, target, out Vector3[] via))
                return false;
            WrappedPathfindingNode[] leg = ArrayPool<WrappedPathfindingNode>.Shared.Rent(capacity);
            try
            {
                int count = 0;
                Vector3 from = position;
                for (int i = 0; i <= via.Length; i++)
                {
                    Vector3 to = i < via.Length ? via[i] : target;
                    PathfindingResult part = nav.GetPathStraight(zone, from, to, filters, leg);
                    if (part.NodeCount < 1 || part.NodeCount > capacity) break;
                    // Each leg starts where the previous one ended; keep that node once.
                    int skip = count == 0 ? 0 : 1;
                    if (count + part.NodeCount - skip > capacity) break;
                    for (int n = skip; n < part.NodeCount; n++) nodes[count++] = leg[n];
                    bool complete = part.Status == PathfindingStatus.PathFound ||
                        Vector3.DistanceSquared(leg[part.NodeCount - 1].Position, to) <= 48 * 48;
                    if (!complete) break;
                    if (i == via.Length)
                    {
                        result = new(PathfindingStatus.PathFound, count);
                        return true;
                    }
                    from = to;
                    // Enough for this order; the partial continuation replots from the last node.
                    if (count >= capacity / 2) break;
                }
                if (count <= 1) return false;
                result = new(PathfindingStatus.PartialPathFound, count);
                return true;
            }
            finally { ArrayPool<WrappedPathfindingNode>.Shared.Return(leg); }
        }

        public bool TryGetNextNode(Zone zone, Vector3 position, Vector3 target, out Vector3? nextNode,
            bool allowPartialContinuation = true)
        {
            // Check if we can reuse our path. We assume that we ourselves never "suddenly" warp to a completely different pos.
            if (ForceReplot || !_lastTarget.IsInRange(target, MIN_TARGET_DIFF_REPLOT_DISTANCE))
            {
                PathfindingStatus status = CalculatePath(_activePath, zone, position, target, DefaultFilters, true);
                UpdatePathState(status, target);
            }

            // Check if any doors on the path have become closed and can't be opened via interaction.
            // If so, replot with door avoidance filters. A detour that just failed is not searched again until
            // the target moves or a few seconds pass; the NPC holds as it did after the failure.
            if (TryFindClosedUnopenableDoor(_activePath.Doors, out GameDoorBase closedDoor))
            {
                long now = GameLoop.GameLoopTime;
                if (ShouldRetryDoorDetour(now, _doorDetourFailedUntil, _doorDetourFailedTarget, target) &&
                    TryApplyAlternativePath(zone, position, target))
                    _doorDetourFailedUntil = 0;
                else
                {
                    if (now >= _doorDetourFailedUntil || !_doorDetourFailedTarget.IsInRange(target, MIN_TARGET_DIFF_REPLOT_DISTANCE))
                    {
                        _doorDetourFailedTarget = target;
                        _doorDetourFailedUntil = now + DoorDetourRetryMilliseconds;
                        ReportDoorHold(closedDoor, position, target, now);
                    }
                    PathfindingStatus = PathfindingStatus.NoPathFound;
                    _lastNoPathTick = now;
                    nextNode = null;
                    return false;
                }
            }

            // Friendly keep doors are traversed by interaction, not opened to
            // enemies. Let the original corridor reach the door, then perform
            // exactly that native interaction before advancing beyond it.
            if (TryUsePathDoor())
            {
                position = new(Owner.X, Owner.Y, Owner.Z);
                UpdatePathState(CalculatePath(_activePath, zone, position, target, DefaultFilters, true), target);
            }

            // Dequeue the next node if we're close to it, and any subsequent node that might be close.
            // Prevent corner-cutting by raycasting to the next node before removing the current one.
            // Open any doors associated with the node as we reach it.
            ManagePathProgress(zone, position);

            if (_activePath.Nodes.Count == 0)
            {
                // A buffer-limited corridor is still the SAME movement order.
                // Extend it here, before the mover emits a stop. Do not wake the
                // entire brain (pet buffs, training, camp/group planning, etc.).
                // A stalled/disconnected endpoint gets no recursive retry.
                if (allowPartialContinuation && Owner is GameBot { IsAutonomousWorldBot: true } &&
                    BotTravelHandoff.CanContinuePartial(PathfindingStatus, _activePath.Origin, position, target))
                {
                    ForceReplot = true;
                    return TryGetNextNode(zone, position, target, out nextNode, false);
                }
                nextNode = null;
                return false;
            }

            nextNode = _activePath.Nodes.Peek(0).Position;
            return true;
        }

        // Battleground bots pass their own realm's keep doors too, as players do: the Midgard and Hibernia portal keeps
        // have a door on the way to the exit, and run 21 (2026-10-17) counted ~190 bots that could not leave a
        // battleground to train (Albion's exit has no door, so only Albion got out).
        // A realm's portal keep is where its bots arrive in the frontier: every bot of that realm leaves through its door.
        // Run 22 (2026-10-18): 136 Hibernian bots without an RvR objective could not leave Hibernia's portal keep in
        // Odin's Gate (120 route give-ups, door 115000902 held 35 times).
        public static bool CanUseFriendlyKeepDoor(GameNPC owner, GameKeepDoor door) =>
            owner is GameBot { IsAutonomousWorldBot: true, IsAlive: true } bot &&
            (AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR) ||
             BattlegroundBrackets.IsBattlegroundRegion(bot.CurrentRegionID) ||
             door?.Component?.Keep?.IsPortalKeep == true ||
             bot.TempProperties?.GetProperty<AutonomousFrontierTransport.Request>(AutonomousFrontierTransport.RequestKey)
                 ?.Passage?.Medallion == "home_necklace") &&
            door?.Component?.Keep != null && bot.Realm != eRealm.None && door.Realm == bot.Realm;

        private bool TryUsePathDoor()
        {
            if (Owner is not GameBot bot || bot.TempProperties.GetProperty<long>("RvrDoorPassUntil") > GameLoop.GameLoopTime)
                return false;
            for (int i = 0; i < _activePath.Nodes.Count; i++)
            {
                if (!_activePath.Doors.TryGetValue(_activePath.Nodes.Peek(i), out var doors)) continue;
                foreach (var candidate in doors)
                {
                    if (candidate is not GameKeepDoor door || !CanUseFriendlyKeepDoor(bot, door)) continue;
                    if (!bot.IsWithinRadius(door, Math.Min(200, WorldMgr.INTERACT_DISTANCE)) || Math.Abs(bot.Z-door.Z)>160)
                        return false;
                    if (!door.TryTraverse(bot)) return false;
                    bot.TempProperties.SetProperty("RvrDoorPassUntil", GameLoop.GameLoopTime + 2000);
                    return true;
                }
            }
            return false;
        }

        private bool TryFindClosedUnopenableDoor(Dictionary<WrappedPathfindingNode, List<GameDoorBase>> doorsOnPath, out GameDoorBase closed)
        {
            foreach (List<GameDoorBase> doorsAroundNode in doorsOnPath.Values)
            {
                foreach (GameDoorBase door in doorsAroundNode)
                {
                    if (door.State is eDoorState.Closed && !door.CanBeOpenedViaInteraction &&
                        !(door is GameKeepDoor keepDoor && CanUseFriendlyKeepDoor(Owner, keepDoor)))
                    {
                        closed = door;
                        return true;
                    }
                }
            }

            closed = null;
            return false;
        }

        /// <summary>A failed door detour is searched again once its wait is over or the target has moved.</summary>
        public static bool ShouldRetryDoorDetour(long now, long failedUntil, Vector3 failedTarget, Vector3 target) =>
            now >= failedUntil || !failedTarget.IsInRange(target, MIN_TARGET_DIFF_REPLOT_DISTANCE);

        /// <summary>No path was found to about this target in the last <see cref="RecentFailureMilliseconds"/>.</summary>
        public bool HasRecentFailure(Vector3 target) =>
            PathfindingStatus == PathfindingStatus.NoPathFound && GameLoop.GameLoopTime - _lastNoPathTick < RecentFailureMilliseconds &&
            _lastTarget.IsInRange(target, MIN_TARGET_DIFF_REPLOT_DISTANCE);

        private void ReportDoorHold(GameDoorBase door, Vector3 position, Vector3 target, long now)
        {
            System.Threading.Interlocked.Increment(ref _doorHoldsSinceReport);
            long next = System.Threading.Interlocked.Read(ref _nextDoorHoldReportTick);
            if (now < next || System.Threading.Interlocked.CompareExchange(ref _nextDoorHoldReportTick, now + 60_000, next) != next)
                return;
            int holds = System.Threading.Interlocked.Exchange(ref _doorHoldsSinceReport, 0);
            if (log.IsInfoEnabled)
                log.Info($"PATH_CLOSED_DOOR_HOLD holdsLastMinute={holds} npc={Owner?.Name} id={Owner?.ObjectID} region={Owner?.CurrentRegionID} " +
                         $"at={position.X:0},{position.Y:0},{position.Z:0} target={target.X:0},{target.Y:0},{target.Z:0} " +
                         $"door={door?.Name} doorId={door?.DoorId} doorAt={door?.X},{door?.Y},{door?.Z}");
        }

        private bool TryApplyAlternativePath(Zone zone, Vector3 position, Vector3 target)
        {
            PathfindingStatus altStatus = CalculatePath(_calculationBuffer, zone, position, target, BlockingDoorAvoidanceFilters);

            // Stop here if the alternative path isn't complete.
            // This prevents NPCs from trying to get closer to their target by walking to the other side of a keep.
            // Basically, if the primary path contains an impassable doors, take the alternative path unless it isn't complete.
            if (altStatus is PathfindingStatus.PathFound)
            {
                (_activePath, _calculationBuffer) = (_calculationBuffer, _activePath);
                UpdatePathState(altStatus, target);
                return true;
            }

            PathfindingStatus = PathfindingStatus.NoPathFound;
            _lastNoPathTick = GameLoop.GameLoopTime;
            return false;
        }

        private void UpdatePathState(PathfindingStatus status, Vector3 target)
        {
            PathfindingStatus = status;
            if (status == PathfindingStatus.NoPathFound)
                _lastNoPathTick = GameLoop.GameLoopTime;
            _lastTarget = target;
            ForceReplot = false;
            _pathVisualization?.Visualize(_activePath.Nodes, Owner.CurrentRegion);
        }

        private void ManagePathProgress(Zone zone, Vector3 position)
        {
            if (!_activePath.Nodes.TryPeek(0, out WrappedPathfindingNode current) || !Owner.IsWithinRadius(current.Position, NODE_REACHED_DISTANCE))
                return;

            // Raid dungeon climb links (Glacier, Darkness Falls, Caer Sidi, Galladoria) follow the original
            // surfaces in 3D.
            // A floor-only LOS shortcut must not skip their intermediate nodes.
            // All ordinary walking, other zones, horses and players retain their
            // existing movement smoothing and cadence.
            // Run 13 (2026-10-16): bots walking Caer Sidi's lift link (964 up over 45) still stalled half way up and
            // dropped off the mesh, even node by node, and the raid lost Bane of Hope at 3% while they hung there. A bot
            // at the foot of a near-vertical link rides it: it is placed on the link's far end, as a lift would carry it.
            if (Owner is GameBot && _activePath.Nodes.Count > 1 &&
                _activePath.Nodes.Peek(1) is var far && IsVerticalLink(current.Flags | far.Flags, current.Position, far.Position))
            {
                _activePath.Doors.Remove(_activePath.Nodes.Dequeue(), out _);
                _activePath.Doors.Remove(_activePath.Nodes.Dequeue(), out _);
                Owner.MoveInRegion(Owner.CurrentRegionID, (int)far.Position.X, (int)far.Position.Y, (int)far.Position.Z, Owner.Heading, true);
                return;
            }

            bool exactClimb = RequiresExactClimbNode(Owner is GameBot, zone.ID, current.Flags,
                _activePath.Nodes.Count > 1 ? _activePath.Nodes.Peek(1).Flags : 0);

            int nodesToRemove = 0;
            int count = _activePath.Nodes.Count;

            for (int i = 1; i < count; i++)
            {
                if (exactClimb) break;
                Vector3 candidatePosition = _activePath.Nodes.Peek(i).Position;

                if (!Owner.IsWithinRadius(candidatePosition, NODE_MAX_SKIP_DISTANCE))
                    break;

                if (!PathfindingProvider.Instance.HasLineOfSight(zone, position, candidatePosition, DefaultFilters))
                    break;

                nodesToRemove = i;
            }

            // If we don't have LoS to any subsequent node, only remove the current one if we're really close, or if we're moving away from it.
            if (nodesToRemove == 0)
            {
                if (Owner.IsWithinRadius(current.Position, NODE_REACHED_DISTANCE_STRICT))
                    nodesToRemove = 1;
                else if (!exactClimb && Owner.movementComponent.IsMoving)
                {
                    float dot = Vector3.Dot(current.Position - position, Vector3.Normalize(Owner.movementComponent.Velocity));

                    if (dot < 0f)
                        nodesToRemove = 1;
                }
            }

            for (int i = 0; i < nodesToRemove; i++)
            {
                WrappedPathfindingNode node = _activePath.Nodes.Dequeue();

                if (_activePath.Doors.Remove(node, out List<GameDoorBase> doors))
                {
                    // Owner 2026-10-07: bots kept working the border keep door levers, so the player could not use
                    // them. Bots walk through openable doors anyway; only other NPCs open them on the way.
                    if (Owner is GameBot) continue;
                    foreach (GameDoorBase door in doors)
                    {
                        if (door.CanBeOpenedViaInteraction && door.State is not eDoorState.Open)
                            door.Open();
                    }
                }
            }
        }

        // Run 12 (2026-10-15): Caer Sidi's lift link (39026,31690 up 964 to the floor above) left 30+ raiders and their
        // pets hanging between the two floors, cut off from the mesh, until they were regrouped; Galladoria too.
        /// <summary>An off-mesh (jump) link that rises or drops at least 300 units and at least twice as far as it runs.</summary>
        public static bool IsVerticalLink(EDtPolyFlags flags, Vector3 from, Vector3 to)
        {
            float rise = Math.Abs(to.Z - from.Z);
            float run = Vector2.Distance(new(from.X, from.Y), new(to.X, to.Y));
            return (flags & EDtPolyFlags.Jump) != 0 && rise >= 300 && rise >= 2 * run;
        }

        public static bool RequiresExactClimbNode(bool bot, ushort zone, EDtPolyFlags current, EDtPolyFlags next) =>
            bot && (zone is 160 or 249 or 60 or 191) && ((current | next) & EDtPolyFlags.Jump) != 0;

        public bool TryGetClosestReachableNode(Zone zone, Vector3 position, Vector3 target, out Vector3? node)
        {
            if (ForceReplot || !_lastTarget.IsInRange(target, MIN_TARGET_DIFF_REPLOT_DISTANCE))
            {
                CalculatePath(_activePath, zone, position, target, DefaultFilters);
                _lastTarget = target;
                ForceReplot = false;
            }

            if (_activePath.Nodes.Count <= 0)
            {
                node = null;
                return false;
            }

            node = _activePath.Nodes.Peek(_activePath.Nodes.Count - 1).Position;
            return true;
        }

        public void ToggleVisualization()
        {
            if (_pathVisualization != null)
            {
                _pathVisualization.CleanUp();
                _pathVisualization = null;
                return;
            }

            _pathVisualization = new();
        }

        public override string ToString()
        {
            return $"{nameof(Pathfinder)}[Target={_lastTarget}, " +
                $"Nodes={_activePath.Nodes.Count}, " +
                $"NextNode={(_activePath.Nodes.Count > 0 ? _activePath.Nodes.Peek(0).ToString() : null)}]";
        }

        private class PathBuffer
        {
            public Vector3 Origin;
            public RingQueue<WrappedPathfindingNode> Nodes { get; } = new();
            public Dictionary<WrappedPathfindingNode, List<GameDoorBase>> Doors { get; } = new();

            public void Clear()
            {
                Nodes.Clear();
                Doors.Clear();
            }
        }
    }
}

using System;
using System.Numerics;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.Movement;
using DOL.GS.ServerProperties;
using DOL.Logging;
using static DOL.GS.GameObject;

namespace DOL.GS
{
    public class NpcMovementComponent : MovementComponent
    {
        public static readonly Logger log = LoggerManager.Create(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const short DEFAULT_WALK_SPEED = 70;


        private MovementState _movementState;
        private Vector3 _velocity;
        private Vector3 _destination;
        private long _nextFollowTick;
        private int _followTickInterval;
        private short _moveOnPathSpeed;
        private long _stopAtPathPointUntil;
        private long _walkingToEstimatedArrivalTime;
        private readonly MovementRequest _movementRequest = new();
        private readonly Pathfinder _pathfinder;
        private ResetHeadingAction _resetHeadingAction;
        private Vector3 _destinationForClient;
        private long _positionForClientTick;
        private Vector3 _positionForClient;
        private bool _needsBroadcastUpdate = true;
        private short _currentMovementDesiredSpeed;
        private int? _groundTravelArrivalMilliseconds;
        private PathVisualization _pathVisualization;
        private long _lastPositionUpdateTick = -1;
        private long _nextAutonomousClientCorrectionTick;
        private bool _forceReliableBroadcast;
        private bool _autonomousPathFailurePending;
        private PathfindingStatus _autonomousPathFailureStatus;
        private Vector3 _autonomousPathFailureDestination;
        private bool _autonomousTravelArrival;
        private SeamContinuation _seamContinuation;
        private FallContinuation _fallContinuation;
        private enum FallStage { WalkOffLip, Descend }
        private sealed record FallContinuation(Vector3 Air, Vector3 Landing, Vector3 Final,
            ushort Region, Group Group, string Assignment, short Speed, FallStage Stage);
        private sealed record SeamContinuation(Vector3 At, Vector3 Next, Vector3? Final,
            ushort Region, Group Group, string Assignment, bool Walk);

        private bool TryContinueCertifiedFall()
        {
            FallContinuation fall = _fallContinuation;
            if (fall == null) return false;
            Vector3 expected = fall.Stage == FallStage.WalkOffLip ? fall.Air : fall.Landing;
            if (Owner is not GameBot { IsAutonomousWorldBot: true } bot ||
                bot.CurrentRegionID != fall.Region || bot.Group != fall.Group ||
                bot.PersistentRecord?.ObjectiveAssignmentId != fall.Assignment || !bot.IsAlive ||
                bot.IsOnStableMasterRoute || Vector3.DistanceSquared(_ownerPosition, expected) > 16 * 16)
            {
                _fallContinuation = null;
                return false;
            }

            if (fall.Stage == FallStage.WalkOffLip)
            {
                // Autonomous NPC movement has no gravity. Ordinary XYZ
                // interpolation from the lip straight to the lower floor
                // would cut through DF's vertical stone wall.
                _fallContinuation = fall with { Stage = FallStage.Descend };
                WalkToInternal(fall.Landing, fall.Speed);
            }
            else
            {
                _fallContinuation = null;
                PathToInternal(fall.Final, fall.Speed);
            }
            return IsFlagSet(MovementState.WalkTo);
        }

        public void PathAcrossValidatedSeam(Vector3 inside, Vector3 outside, Vector3? final, short speed)
        {
            PathTo(inside, speed);
            if (Owner is GameBot { IsAutonomousWorldBot: true } bot)
                _seamContinuation = new(inside, outside, final, bot.CurrentRegionID, bot.Group,
                    bot.PersistentRecord?.ObjectiveAssignmentId, true);
        }

        public void PathViaValidatedWaypoint(Vector3 waypoint, Vector3 final, short speed)
        {
            PathTo(waypoint, speed);
            if (Owner is GameBot { IsAutonomousWorldBot: true } bot)
                _seamContinuation = new(waypoint, final, null, bot.CurrentRegionID, bot.Group,
                    bot.PersistentRecord?.ObjectiveAssignmentId, false);
        }

        private bool TryContinueValidatedSeam()
        {
            SeamContinuation continuation = _seamContinuation;
            if (continuation == null) return false;
            _seamContinuation = null;
            if (Owner is not GameBot bot || bot.CurrentRegionID != continuation.Region ||
                bot.Group != continuation.Group || bot.PersistentRecord?.ObjectiveAssignmentId != continuation.Assignment ||
                !bot.IsAlive || bot.InCombat || bot.IsAttacking || bot.IsCasting || bot.IsCrowdControlled ||
                bot.castingComponent?.HasPendingSkillRequests == true ||
                bot.IsOnStableMasterRoute || bot.IsRecoveryResting || bot.Brain is not BotBrain brain || brain.HasAggro ||
                Vector3.DistanceSquared(_ownerPosition, continuation.At) > 16 * 16)
                return false;
            if (bot.Group != null)
                foreach (GameLiving member in bot.Group.GetMembersInTheGroup())
                    if (member.InCombat || member.IsAttacking || member is GameBot { IsRecoveryResting: true }) return false;
            if (continuation.Walk)
            {
                UnsetFlag(MovementState.Pathfinding);
                _movementRequest.Set(MovementRequestType.Walk, continuation.Next, _currentMovementDesiredSpeed);
                WalkToInternal(continuation.Next, _currentMovementDesiredSpeed);
                if (continuation.Final.HasValue)
                    _seamContinuation = continuation with { At = continuation.Next, Next = continuation.Final.Value, Final = null, Walk = false };
            }
            else
                PathToInternal(continuation.Next, _currentMovementDesiredSpeed);
            return IsFlagSet(MovementState.WalkTo);
        }

        public bool ConsumeAutonomousTravelArrival()
        {
            bool arrived = _autonomousTravelArrival;
            _autonomousTravelArrival = false;
            return arrived;
        }

        public new GameNPC Owner { get; }
        public int CopyUpcomingPath(Vector3 target, Span<Vector3> destination) =>
            _pathfinder.CopyUpcomingPath(target, destination);
        public ref Vector3 Velocity => ref _velocity;
        public ref Vector3 Destination => ref _destination;
        public GameLiving FollowTarget { get; private set; }
        public int MinFollowDistance { get; private set; }
        public int MaxFollowDistance { get; private set; }
        public string PathID { get; set; }
        public PathPoint CurrentPathPoint { get; set; }
        public bool IsReturningToSpawnPoint { get; private set; }
        public int RoamingRange { get; set; }
        public long MovementStartTick { get; set; }
        public long MovementElapsedTicks => IsMoving ? GameLoop.GameLoopTime - MovementStartTick : 0;
        public bool FixedSpeed { get; set; }
        public override short MaxSpeed => FixedSpeed ? MaxSpeedBase : Owner is GameBot bot
            ? CompanionFollowPolicy.SpeedLimit(bot, base.MaxSpeed) : base.MaxSpeed;
        public bool IsMovingOnPath => IsFlagSet(MovementState.OnPath);
        public bool IsNearSpawn => Owner.IsWithinRadius(Owner.SpawnPoint, 25);
        public bool IsDestinationValid { get; private set; }
        public bool IsAtDestination => !IsDestinationValid || (_destination - _ownerPosition).LengthSquared() < 1.0f;
        public bool CanRoam => Properties.ALLOW_ROAM && RoamingRange > 0 && !CanMoveOnPath;
        public bool CanMoveOnPath => !string.IsNullOrEmpty(PathID);
        public double HorizontalVelocityForClient { get; private set; }
        public bool HasActiveResetHeadingAction => _resetHeadingAction != null && _resetHeadingAction.IsAlive;
        public ref Vector3 DestinationForClient => ref _destinationForClient;
        public ref Vector3 PositionForClient => ref _positionForClientTick == GameLoop.GameLoopTime ? ref _positionForClient : ref _ownerPosition;

        public int X => (int) Math.Round(_ownerPosition.X);
        public int Y => (int) Math.Round(_ownerPosition.Y);
        public int Z => (int) Math.Round(_ownerPosition.Z);

        public NpcMovementComponent(GameNPC owner) : base(owner)
        {
            Owner = owner;
            _pathfinder = new(owner);
        }

        protected override void TickInternal()
        {
            // Update the component's position first for correct calculations.
            UpdatePosition();

            if (IsFlagSet(MovementState.TurnTo))
            {
                if (!Owner.IsAttacking)
                {
                    FinalizeTick();
                    return;
                }

                UnsetFlag(MovementState.TurnTo);
                _resetHeadingAction.Stop();
                _resetHeadingAction = null;
            }

            if (IsFlagSet(MovementState.Request))
            {
                UnsetFlag(MovementState.Request);
                ProcessMovementRequest();
            }

            if (IsFlagSet(MovementState.Follow))
            {
                if (GameServiceUtils.ShouldTick(_nextFollowTick))
                {
                    _followTickInterval = FollowTick();

                    if (_followTickInterval != 0)
                        _nextFollowTick = GameLoop.GameLoopTime + _followTickInterval;
                    else
                        UnsetFlag(MovementState.WalkTo);
                }
            }

            if (IsFlagSet(MovementState.WalkTo))
            {
                if (GameServiceUtils.ShouldTick(_walkingToEstimatedArrivalTime))
                {
                    UnsetFlag(MovementState.WalkTo);
                    OnArrival();
                }
            }

            if (IsFlagSet(MovementState.AtPathPoint))
            {
                if (GameServiceUtils.ShouldTick(_stopAtPathPointUntil))
                {
                    UnsetFlag(MovementState.AtPathPoint);
                    MoveToNextPathPoint();
                }
            }

            FinalizeTick();
        }

        private void FinalizeTick()
        {
            base.TickInternal();

            // A bot stable ride has one authoritative mover: its taxi. Keep
            // the server-side bot actor at the taxi position before any spatial
            // queries or client broadcasts observe this tick.
            if (Owner is GameBotTaxi botTaxi)
                botTaxi.SynchronizeRider();

            bool autonomousCorrection = Owner is GameBot { IsOnStableMasterRoute: false } && IsMoving &&
                                        GameServiceUtils.ShouldTick(_nextAutonomousClientCorrectionTick);
            if (_needsBroadcastUpdate || autonomousCorrection)
            {
                if (Owner is GameBot)
                    _nextAutonomousClientCorrectionTick = GameLoop.GameLoopTime + 1200 + Owner.ObjectID % 350;

                _needsBroadcastUpdate = false;
                OnPositionUpdate();
                // Movement starts/stops and combat-facing changes must not be
                // lossy for playerbots. Periodic in-motion corrections remain
                // UDP and cheaply re-anchor client extrapolation if one update
                // was missed.
                // A stable-route bot is visually attached to its GameTaxi.
                // Sending an independent NPC movement packet for the rider
                // detaches it client-side and produces an empty horse.
                if (Owner is not GameBot { IsOnStableMasterRoute: true })
                    ClientService.UpdateNpcForPlayers(Owner, !_forceReliableBroadcast);
                _forceReliableBroadcast = false;
            }

            if (_movementState is MovementState.None)
                RemoveFromServiceObjectStore();
        }

        public void WalkTo(Vector3 destination, short speed)
        {
            _seamContinuation = null;
            _fallContinuation = null;
            _movementRequest.Set(MovementRequestType.Walk, destination, speed);
            SetFlag(MovementState.Request);
            AddToServiceObjectStore();
        }

        public void PathTo(Vector3 destination, short speed)
        {
            // The controller may reissue the same travel order every tick.
            // Do not restart a certified fall between its lip and landing.
            if (_fallContinuation is { } fall &&
                Owner is GameBot { IsAutonomousWorldBot: true } &&
                Owner.CurrentRegionID == fall.Region &&
                Vector3.DistanceSquared(destination, fall.Final) <= 64 * 64)
                return;
            _seamContinuation = null;
            _fallContinuation = null;
            _movementRequest.Set(MovementRequestType.Path, destination, speed);
            SetFlag(MovementState.Request);
            AddToServiceObjectStore();
        }

        public void ForcePathReplot()
        {
            _pathfinder.ForceReplot = true;
        }

        public bool TryConsumeAutonomousPathFailure(out PathfindingStatus status, out Vector3 destination)
        {
            status = _autonomousPathFailureStatus;
            destination = _autonomousPathFailureDestination;
            if (!_autonomousPathFailurePending)
                return false;
            _autonomousPathFailurePending = false;
            return true;
        }

        public void StopMoving()
        {
            _seamContinuation = null;
            _fallContinuation = null;
            _movementState = MovementState.None;
            StopFollowing();
            StopMovingOnPath();
            CancelReturnToSpawnPoint();

            if (IsMoving)
                UpdateMovement(0);
        }

        public void Follow(GameLiving target, int minDistance, int maxDistance)
        {
            if (target == null || target.ObjectState is not eObjectState.Active || !Owner.castingComponent.IsAllowedToFollow(target))
                return;

            _seamContinuation = null;
            _fallContinuation = null;

            if (target != FollowTarget)
                _nextFollowTick = 0;

            FollowTarget = target;
            MinFollowDistance = minDistance;
            MaxFollowDistance = maxDistance;
            SetFlag(MovementState.Follow);
            AddToServiceObjectStore();
        }

        public void StopFollowing()
        {
            UnsetFlag(MovementState.Follow);
            FollowTarget = null;
        }

        public void MoveOnPath(short speed)
        {
            StopMoving();
            _moveOnPathSpeed = speed;

            // Move to the first path point if we don't have any.
            // Otherwise and if we're not currently moving on path, move to the previous one (current path point if none).
            if (CurrentPathPoint == null)
            {
                if (PathID == null)
                {
                    if (log.IsErrorEnabled)
                        log.Error($"Called {nameof(MoveOnPath)} but PathID is null (NPC: {Owner})");

                    return;
                }

                CurrentPathPoint = MovementMgr.LoadPath(PathID);

                if (CurrentPathPoint == null)
                {
                    if (log.IsErrorEnabled)
                        log.Error($"Called {nameof(MoveOnPath)} but LoadPath returned null (PathID: {PathID}) (NPC: {Owner})");

                    return;
                }

                SetFlag(MovementState.OnPath);
                PathTo(new(CurrentPathPoint.X, CurrentPathPoint.Y, CurrentPathPoint.Z), Math.Min(_moveOnPathSpeed, CurrentPathPoint.MaxSpeed));
                return;
            }
            else if (!IsFlagSet(MovementState.OnPath))
            {
                SetFlag(MovementState.OnPath);

                if (Owner.IsWithinRadius(CurrentPathPoint, 25))
                {
                    MoveToNextPathPoint();
                    return;
                }

                if (CurrentPathPoint.Type == EPathType.Path_Reverse && CurrentPathPoint.FiredFlag)
                {
                    if (CurrentPathPoint.Next != null)
                        CurrentPathPoint = CurrentPathPoint.Next;
                }
                else if (CurrentPathPoint.Prev != null)
                    CurrentPathPoint = CurrentPathPoint.Prev;

                PathTo(new(CurrentPathPoint.X, CurrentPathPoint.Y, CurrentPathPoint.Z), Owner.MaxSpeed);
            }
            else if (log.IsErrorEnabled)
                log.Error($"Called {nameof(MoveOnPath)} but both {nameof(CurrentPathPoint)} and {nameof(MovementState.OnPath)} are already set. (NPC: {Owner})");
        }

        public void StopMovingOnPath()
        {
            if (!IsFlagSet(MovementState.OnPath))
                return;

            UnsetFlag(MovementState.OnPath);

            if (Owner is (GameTaxi or GameTaxiBoat) and not GameBotTaxi)
                Owner.RemoveFromWorld();

            // We don't reset CurrentPathPoint here to allow the path to be resumed. This must be done manually if needed (on NPC death for example).
        }

        public void ReturnToSpawnPoint()
        {
            ReturnToSpawnPoint(DEFAULT_WALK_SPEED);
        }

        public void ReturnToSpawnPoint(short speed)
        {
            StopMoving();
            Owner.TargetObject = null;
            Owner.attackComponent.StopAttack();
            (Owner.Brain as StandardMobBrain)?.ClearAggroList();
            IsReturningToSpawnPoint = true;
            PathTo(new(Owner.SpawnPoint.X, Owner.SpawnPoint.Y, Owner.SpawnPoint.Z), speed);
        }

        public void CancelReturnToSpawnPoint()
        {
            IsReturningToSpawnPoint = false;
        }

        public void Roam(short speed)
        {
            // `CanRoam` returns false if `RoamingRange` is <= 0.
            if (!CanRoam)
                return;

            int maxRoamingRadius = Owner.RoamingRange;

            if (Owner.CurrentZone.IsPathfindingEnabled)
            {
                EDtPolyFlags[] filters = PathfindingProvider.Instance.DefaultFilters;
                Vector3? target = PathfindingProvider.Instance.GetRandomPoint(Owner.CurrentZone, new(Owner.SpawnPoint.X, Owner.SpawnPoint.Y, Owner.SpawnPoint.Z), maxRoamingRadius, filters);

                if (target.HasValue)
                    PathTo(target.Value, speed);

                return;
            }

            maxRoamingRadius = Util.Random(maxRoamingRadius);
            double angle = Util.RandomDouble() * Math.PI * 2;
            double targetX = Owner.SpawnPoint.X + maxRoamingRadius * Math.Cos(angle);
            double targetY = Owner.SpawnPoint.Y + maxRoamingRadius * Math.Sin(angle);
            WalkTo(new((float) targetX, (float) targetY, Owner.SpawnPoint.Z), speed);
        }

        public void RestartCurrentMovement()
        {
            if (IsDestinationValid && !IsAtDestination)
                WalkToInternal(new(_destination.X, _destination.Y, _destination.Z), _currentMovementDesiredSpeed);
        }

        public void TurnTo(GameObject target, int duration = 0)
        {
            if (target == null || target.CurrentRegion != Owner.CurrentRegion)
                return;

            TurnTo(target.X, target.Y, duration);
        }

        public void TurnTo(int x, int y, int duration = 0)
        {
            TurnTo(Owner.GetHeading(x, y), duration);
        }

        public void TurnTo(ushort heading, int duration = 0)
        {
            if (Owner.Heading == heading || Owner.IsCrowdControlled || IsTurningDisabled)
                return;

            if (duration > 0)
            {
                SetFlag(MovementState.TurnTo);

                if (_resetHeadingAction == null)
                {
                    _resetHeadingAction = CreateResetHeadingAction();
                    _resetHeadingAction.Start(duration);
                }
                else
                {
                    // Attempt to extend the duration of our existing `ResetHeadingAction`.
                    _resetHeadingAction.Start(duration);

                    if (!_resetHeadingAction.IsAlive)
                    {
                        _resetHeadingAction = CreateResetHeadingAction();
                        _resetHeadingAction.Start(duration);
                    }
                }
            }

            _needsBroadcastUpdate = true;
            if (Owner is GameBot && Owner.IsAttacking)
                _forceReliableBroadcast = true;
            Owner.Heading = heading;
            AddToServiceObjectStore();
        }

        public override void DisableTurning(bool add)
        {
            // Trigger an update to make sure the NPC properly starts or stops auto facing client side.
            // May technically be only necessary if the count is going from 0 to 1 or 1 to 0, but we're skipping that because it would need to be thread safe.

            _needsBroadcastUpdate = true;
            base.DisableTurning(add);
        }

        public void TogglePathVisualization()
        {
            // Toggle visualization for both `Pathfinder` (pathfinding) and `PathPoint` (patrols, horse routes).

            _pathfinder.ToggleVisualization();

            if (_pathVisualization != null)
            {
                _pathVisualization.CleanUp();
                _pathVisualization = null;
                return;
            }

            _pathVisualization = new();

            if (CurrentPathPoint != null)
                _pathVisualization.Visualize(MovementMgr.FindFirstPathPoint(CurrentPathPoint), Owner.CurrentRegion);
        }

        public void ForceUpdatePosition()
        {
            _seamContinuation = null;
            _fallContinuation = null;
            // Must be called every time the NPC is teleported or moved by other means than this component.
            _ownerPosition = new(Owner.RealX, Owner.RealY, Owner.RealZ);
            _positionForClient = _ownerPosition;
            _lastPositionUpdateTick = GameLoop.GameLoopTime;
        }

        protected override void UpdatePosition()
        {
            if (_lastPositionUpdateTick == GameLoop.GameLoopTime)
                return;

            if (!IsMoving)
            {
                _lastPositionUpdateTick = GameLoop.GameLoopTime;
                return;
            }

            long timeDelta = GameLoop.GameLoopTime - _lastPositionUpdateTick;
            Vector3 movementDelta = _velocity * (timeDelta * 0.001f);
            Vector3 potentialPosition = _ownerPosition + movementDelta;

            if (!IsDestinationValid)
            {
                if (!AllowPosition(potentialPosition)) return;
                _ownerPosition = potentialPosition;
                _lastPositionUpdateTick = GameLoop.GameLoopTime;
                return;
            }

            Vector3 absToDestination = Vector3.Abs(_destination - _ownerPosition);
            Vector3 absMovementDelta = Vector3.Abs(movementDelta);

            // Create a "mask" vector (1.0f or 0.0f) for each axis.
            // 1.0f means we use the potential position.
            // 0.0f means we have overshot and should clamp to destination.
            Vector3 usePotential = new(
                absToDestination.X >= absMovementDelta.X ? 1.0f : 0.0f,
                absToDestination.Y >= absMovementDelta.Y ? 1.0f : 0.0f,
                absToDestination.Z >= absMovementDelta.Z ? 1.0f : 0.0f
            );

            potentialPosition = potentialPosition * usePotential + _destination * (Vector3.One - usePotential);
            if (!AllowPosition(potentialPosition)) return;
            _ownerPosition = potentialPosition;
            _lastPositionUpdateTick = GameLoop.GameLoopTime;
        }

        private void ProcessMovementRequest()
        {
            if (_movementRequest.Type is MovementRequestType.Walk)
                WalkToInternal(_movementRequest.Destination, _movementRequest.Speed);
            else
                PathToInternal(_movementRequest.Destination, _movementRequest.Speed);
        }

        private void UpdateVelocity(float distanceToTarget)
        {
            MovementStartTick = GameLoop.GameLoopTime;
            _groundTravelArrivalMilliseconds = null;

            if (!IsMoving || distanceToTarget <= 0)
            {
                _velocity = Vector3.Zero;
                HorizontalVelocityForClient = 0.0;
                return;
            }

            if (!IsDestinationValid)
            {
                double heading = Owner.Heading * Point2D.HEADING_TO_RADIAN;
                _velocity = new((float) -Math.Sin(heading), (float) Math.Cos(heading), 0.0f);
            }
            else
            {
                Vector3 direction = _destination - _ownerPosition;
                if (AutonomousGroundTravelMotion.Applies(Owner, FixedSpeed, FollowTarget) &&
                    AutonomousGroundTravelMotion.TryCalculate(direction, CurrentSpeed, out var travel))
                {
                    _velocity = travel.Velocity;
                    HorizontalVelocityForClient = travel.HorizontalSpeed;
                    _groundTravelArrivalMilliseconds = travel.ArrivalMilliseconds;
                    return;
                }
                float scale = CurrentSpeed / distanceToTarget;
                _velocity = direction * scale;
            }

            HorizontalVelocityForClient =  new Vector2(_velocity.X, _velocity.Y).Length();
            return;
        }

        private void WalkToInternal(Vector3 destination, short speed)
        {
            if (!AllowPosition(destination)) return;
            if (IsTurningDisabled)
                return;

            _currentMovementDesiredSpeed = speed;

            if (speed > MaxSpeed)
                speed = MaxSpeed;

            if (speed <= 0)
            {
                if (CurrentSpeed > 0)
                    UpdateMovement(0);

                return;
            }

            float distanceToTarget = (_ownerPosition - destination).Length();

            if (distanceToTarget > 25)
                TurnTo((int) destination.X, (int) destination.Y);
            else if (!IsFlagSet(MovementState.Pathfinding))
                TurnTo(FollowTarget);

            int ticksToArrive = (int) (distanceToTarget * 1000 / speed);

            if (ticksToArrive <= 0)
            {
                _ownerPosition = destination;

                if (CurrentSpeed > 0)
                    UpdateMovement(0);

                return;
            }

            // Assume either the destination or speed has changed.
            UpdateMovement(destination, distanceToTarget, speed);
            SetFlag(MovementState.WalkTo);
            // Without this, a slope-corrected bot reaches the next node early
            // then visibly waits for the old, longer 3D arrival timer.
            _walkingToEstimatedArrivalTime = GameLoop.GameLoopTime + (_groundTravelArrivalMilliseconds ?? ticksToArrive);
        }

        private void PathToInternal(Vector3 destination, short speed, bool allowFloorRepair = true)
        {
            if (!AutonomousRealmBoundary.Allows(Owner, destination))
            {
                UpdateMovement(0);
                return;
            }
            Zone zone = Owner.CurrentZone;
            if (Owner is GameBot borderBot && AutonomousRvrTravel.OpenNearbyBorderDoors(borderBot, destination))
                _pathfinder.ForceReplot = true;

            if (!_pathfinder.ShouldPath(zone, destination))
            {
                if (IsPersistentAutonomous(this))
                    PauseMovement(this, destination);
                else
                    FallbackToWalk(this, destination, speed);
                return;
            }

            if (_pathfinder.TryGetNextNode(zone, _ownerPosition, destination, out Vector3? _nextNode))
            {
                ClearAutonomousPathFailure();
                if (Owner is GameBot { IsAutonomousWorldBot: true } bot &&
                    zone.ID == AutonomousDarknessFallsPolicy.RegionId &&
                    _pathfinder.PathfindingStatus == PathfindingStatus.PathFound &&
                    AutonomousDarknessFallsNavigation.TryGetCertifiedFall(_ownerPosition,
                        _nextNode.Value, out Vector3 air, out Vector3 landing))
                {
                    _fallContinuation = new(air, landing, destination, bot.CurrentRegionID,
                        bot.Group, bot.PersistentRecord?.ObjectiveAssignmentId, speed, FallStage.WalkOffLip);
                    UnsetFlag(MovementState.Pathfinding);
                    WalkToInternal(air, speed);
                    return;
                }
                // Continue to the next node, even on partial paths.
                _movementRequest.Set(MovementRequestType.Path, destination, speed);
                SetFlag(MovementState.Pathfinding);
                // The Detour node chain is authoritative. Injecting a second
                // steering destination here made otherwise valid routes cut
                // into walls and repeatedly restart. Threat avoidance belongs
                // to high-level route selection, not the continuous mover.
                WalkToInternal(_nextNode.Value, speed);
                return;
            }

            switch (_pathfinder.PathfindingStatus)
            {
                case PathfindingStatus.NoPathFound when allowFloorRepair &&
                    Owner is GameBot { IsAutonomousWorldBot: true, IsOnStableMasterRoute: false, InCombat: false }:
                {
                    // Only a failed path triggers these two bounded lookups.
                    // Old mob/bind Z and interrupted slope movement can leave a
                    // bot 80-90 units below the polygon, outside Detour's pick box.
                    // Correct altitude without MoveTo/remove/add packet flicker;
                    // never move laterally through an obstruction or bypass a path.
                    var nav = PathfindingProvider.Instance;
                    Vector3 start = _ownerPosition, end = destination;
                    AutonomousNavigationSurface.TryFloor(nav, zone, start, out start);
                    AutonomousNavigationSurface.TryFloor(nav, zone, end, out end);
                    if (Vector3.DistanceSquared(start, _ownerPosition) > 4 ||
                        Vector3.DistanceSquared(end, destination) > 4)
                    {
                        _ownerPosition = start;
                        UpdateMovement(0);
                        _pathfinder.ForceReplot = true;
                        PathToInternal(end, speed, false);
                    }
                    else PauseMovement(this, destination);
                    break;
                }
                case PathfindingStatus.PathFound:
                {
                    EDtPolyFlags[] filters = PathfindingProvider.Instance.BlockingDoorAvoidanceFilters;

                    // Finalize the path if we have direct LoS to the destination.
                    // This ensures that the NPC stays on the mesh, assuming it's on it to begin with.
                    // Use the most restrictive filters for now, since we don't know which ones were used.
                    if (PathfindingProvider.Instance.HasLineOfSight(zone, _ownerPosition, destination, filters))
                    {
                        ClearAutonomousPathFailure();
                        FallbackToWalk(this, destination, speed);
                    }
                    else
                        PauseMovement(this, destination);

                    break;
                }
                case PathfindingStatus.PartialPathFound:
                case PathfindingStatus.BufferTooSmall:
                case PathfindingStatus.NoPathFound: // Happens when either the current position or the destination isn't on a mesh.
                {
                    if (IsPersistentAutonomous(this) || CompanionFollowPolicy.HasFormationOrder(Owner as GameBot))
                    {
                        PauseMovement(this, destination);
                        break;
                    }

                    // Non-pet NPCs are teleported to the closest reachable node from a reverse-path.
                    // The teleport can cover a large distance in some cases, for example when both the NPC and the player are on a mesh island.
                    // This helps against exploits and misplaced NPCs.

                    // Pets following their owner are teleported at their feet if both are out of combat.
                    // This allows them to keep up if they jump down a ledge or bridge.
                    // This can theoretically be exploited by players in combat, but it requires both the pet and the owner to leave combat.

                    if (Owner.Brain is not ControlledMobBrain petBrain)
                    {
                        if (JumpToClosestReachableNode(this, destination))
                            break;
                    }
                    else if (!Owner.InCombat && !petBrain.Owner.InCombat && FollowTarget != null && petBrain.Owner == FollowTarget)
                    {
                        if (TeleportPetToFloorBeneathOwner(this, petBrain))
                            break;
                    }

                    PauseMovement(this, destination);
                    break;
                }
                case PathfindingStatus.NotSet:
                case PathfindingStatus.NavmeshUnavailable:
                {
                    if (IsPersistentAutonomous(this))
                        PauseMovement(this, destination);
                    else
                        FallbackToWalk(this, destination, speed);
                    break;
                }
                default:
                {
                    PauseMovement(this, destination);
                    break;
                }
            }

            static void FallbackToWalk(NpcMovementComponent component, Vector3 destination, short speed)
            {
                component.UnsetFlag(MovementState.Pathfinding);
                component.WalkToInternal(destination, speed);
            }

            static bool IsPersistentAutonomous(NpcMovementComponent component) =>
                component.Owner is GameBot { IsAutonomousWorldBot: true };

            static bool JumpToClosestReachableNode(NpcMovementComponent component, Vector3 destination)
            {
                if (!component._pathfinder.TryGetClosestReachableNode(component.Owner.CurrentZone, destination, component._ownerPosition, out Vector3? node) || !node.HasValue)
                    return false;

                component._ownerPosition = node.Value;
                component.UpdateMovement(0);
                component._pathfinder.ForceReplot = true;
                return true;
            }

            static bool TeleportPetToFloorBeneathOwner(NpcMovementComponent component, ControlledMobBrain petBrain)
            {
                const int MAX_TELEPORT_TRIGGER_RANGE = 1024;
                const int MAX_FLOOR_SEARCH_DEPTH = 1024;
                const int MIN_TELEPORT_DISTANCE = 128;

                GamePlayer playerOwner = petBrain.GetPlayerOwner();

                if (DragonCombatGeometry.IsRecoveringFromThrow(playerOwner))
                    return false;

                if (!component.Owner.IsWithinRadius(playerOwner, MAX_TELEPORT_TRIGGER_RANGE))
                    return false;

                Vector3 playerOwnerPos = new(playerOwner.X, playerOwner.Y, playerOwner.Z);
                EDtPolyFlags[] filters = PathfindingProvider.Instance.DefaultFilters;
                Vector3? floor = PathfindingProvider.Instance.GetFloorBeneath(playerOwner.CurrentZone, playerOwnerPos, MAX_FLOOR_SEARCH_DEPTH, filters);

                if (!floor.HasValue || component.Owner.IsWithinRadius(floor.Value, MIN_TELEPORT_DISTANCE))
                    return false;

                component._ownerPosition = floor.Value;
                component.UpdateMovement(0);
                component._pathfinder.ForceReplot = true;
                return true;
            }

            static void PauseMovement(NpcMovementComponent component, Vector3 destination)
            {
                bool autonomous = IsPersistentAutonomous(component);
                if (autonomous)
                {
                    component._autonomousPathFailurePending = true;
                    component._autonomousPathFailureStatus = component._pathfinder.PathfindingStatus;
                    component._autonomousPathFailureDestination = destination;
                }
                else
                {
                    component.TurnTo((int) destination.X, (int) destination.Y);
                }
                component.UnsetFlag(MovementState.Pathfinding);

                if (component.IsMoving)
                    component.UpdateMovement(0);
            }
        }

        private void ClearAutonomousPathFailure()
        {
            _autonomousPathFailurePending = false;
            _autonomousPathFailureStatus = PathfindingStatus.NotSet;
            _autonomousPathFailureDestination = default;
        }

        private int FollowTick()
        {
            // A bot's mobile song is a cast, but must not freeze its follow
            // route. Ordinary NPC casts and ranged attacks keep their stop rule.
            if (Owner.IsCasting && !BotSongTwistPolicy.HasMobileSongCast(Owner) ||
                (Owner.IsAttacking && Owner.ActiveWeaponSlot is eActiveWeaponSlot.Distance))
            {
                StopMoving();
                return Properties.GAMENPC_FOLLOWCHECK_TIME;
            }

            if (!FollowTarget.IsAlive || FollowTarget.ObjectState is not eObjectState.Active || Owner.CurrentRegionID != FollowTarget.CurrentRegionID)
            {
                StopMoving();
                return 0;
            }

            Vector3 targetPos = new(FollowTarget.X, FollowTarget.Y, FollowTarget.Z);

            // Stop at the dragon's body, not its origin under the model. Ordinary
            // following and all other NPC targets retain their existing distances.
            int dragonReach = DragonCombatGeometry.TargetReach(FollowTarget);
            if (dragonReach > 0 && Owner.IsAttacking && Owner.TargetObject == FollowTarget)
                MinFollowDistance = Math.Max(MinFollowDistance, dragonReach);

            if (Owner.Brain is StandardMobBrain brain && FollowTarget.Realm == Owner.Realm)
            {
                int tx = (int) targetPos.X;
                int ty = (int) targetPos.Y;
                int tz = (int) targetPos.Z;

                // Update to formation-adjusted position.
                if (brain.CheckFormation(ref tx, ref ty, ref tz))
                {
                    targetPos = new(tx, ty, tz);
                    MinFollowDistance = 0;
                }
            }

            Vector3 relative = targetPos - _ownerPosition;
            float distanceSquared = relative.LengthSquared();

            if (distanceSquared > MaxFollowDistance * MaxFollowDistance)
            {
                StopFollowing();
                return 0;
            }

            // The way position is updated ensures that we never move past the destination, so we need to take potential small inaccuracies into account.
            if (distanceSquared <= (MinFollowDistance + 1) * (MinFollowDistance + 1))
            {
                TurnTo(FollowTarget);

                if (IsMoving)
                    UpdateMovement(0);

                UnsetFlag(MovementState.Pathfinding); // Ensures OnArrival doesn't try to reach remaining nodes.
                return Properties.GAMENPC_FOLLOWCHECK_TIME;
            }

            float distance = MathF.Sqrt(distanceSquared);
            float scale = MinFollowDistance / distance;

            Vector3 destination = targetPos - relative * scale;
            destination.Z = targetPos.Z; // May move the NPC closer than intended, but improves movement overall.

            short speed;

            // No smoothing if the NPC is attacking and is out of melee range.
            if (Owner.IsAttacking && distance > Owner.MeleeAttackRange)
                speed = MaxSpeed;
            else
                speed = (short) Math.Min(MaxSpeed, (distance - MinFollowDistance) * 2.5);

            // Snap the destination to the mesh with a generous search distance. Use the follow target's position as a fallback.
            if (!TrySnapToMesh(ref destination))
            {
                destination = targetPos;
                TrySnapToMesh(ref destination);
            }

            PathToInternal(destination, Math.Max((short) 20, speed));
            return Properties.GAMENPC_FOLLOWCHECK_TIME;
        }

        public bool TrySnapToMesh(ref Vector3 destination)
        {
            const float MAX_SNAP_DISTANCE = 128f;
            Zone zone = Owner.CurrentRegion.GetZone((int) destination.X, (int) destination.Y);
            return PathfindingProvider.Instance.TrySnapToMesh(zone, ref destination, MAX_SNAP_DISTANCE);
        }

        private void OnArrival()
        {
            if (!AllowPosition(_destination)) return;
            if (TryContinueCertifiedFall()) return;
            if (_seamContinuation != null && Vector3.DistanceSquared(_ownerPosition, _seamContinuation.At) <= 16 * 16 &&
                TryContinueValidatedSeam()) return;
            if (IsFlagSet(MovementState.Pathfinding))
            {
                ProcessMovementRequest();

                if (IsFlagSet(MovementState.WalkTo))
                    return;
            }

            if (IsFlagSet(MovementState.Follow))
                return;

            if (IsReturningToSpawnPoint)
            {
                _ownerPosition = _destination;
                UpdateMovement(0);
                CancelReturnToSpawnPoint();
                TurnTo(Owner.SpawnHeading);
                return;
            }

            if (IsFlagSet(MovementState.OnPath))
            {
                if (CurrentPathPoint != null)
                {
                    if (CurrentPathPoint.WaitTime == 0)
                    {
                        MoveToNextPathPoint();
                        return;
                    }

                    SetFlag(MovementState.AtPathPoint);
                    _stopAtPathPointUntil = GameLoop.GameLoopTime + CurrentPathPoint.WaitTime * 100;
                }
                else
                    StopMovingOnPath();
            }

            if (TryContinueValidatedSeam()) return;

            if (IsMoving)
            {
                _ownerPosition = _destination;
                UpdateMovement(0);
            }
            if (!_autonomousPathFailurePending || _autonomousPathFailureStatus == PathfindingStatus.PartialPathFound)
            {
                _autonomousTravelArrival = Owner is GameBot { IsAutonomousWorldBot: true };
            }
        }

        private void MoveToNextPathPoint()
        {
            PathPoint oldPathPoint = CurrentPathPoint;
            PathPoint nextPathPoint = CurrentPathPoint.Next;

            if ((CurrentPathPoint.Type is EPathType.Path_Reverse) && CurrentPathPoint.FiredFlag)
                nextPathPoint = CurrentPathPoint.Prev;

            if (nextPathPoint == null)
            {
                switch (CurrentPathPoint.Type)
                {
                    case EPathType.Loop:
                    {
                        CurrentPathPoint = MovementMgr.FindFirstPathPoint(CurrentPathPoint);
                        break;
                    }
                    case EPathType.Once:
                    {
                        CurrentPathPoint = null;
                        PathID = null; // Unset the path ID, otherwise the brain will re-enter patrolling state and restart it.
                        break;
                    }
                    case EPathType.Path_Reverse:
                    {
                        CurrentPathPoint = oldPathPoint.FiredFlag ? CurrentPathPoint.Next : CurrentPathPoint.Prev;
                        break;
                    }
                }
            }
            else
                CurrentPathPoint = CurrentPathPoint.Type is EPathType.Path_Reverse && CurrentPathPoint.FiredFlag ? CurrentPathPoint.Prev : CurrentPathPoint.Next;

            oldPathPoint.FiredFlag = !oldPathPoint.FiredFlag;

            if (CurrentPathPoint != null)
                PathTo(new(CurrentPathPoint.X, CurrentPathPoint.Y, CurrentPathPoint.Z), Math.Min(_moveOnPathSpeed, CurrentPathPoint.MaxSpeed));
            else
                StopMovingOnPath();
        }

        private void PrepareValuesForClient(bool wasMoving, double distanceToTarget)
        {
            // Use slightly modified object position and target position to smooth movement out client-side.
            // The real target position makes NPCs stop before it. The real object position makes NPCs teleport a bit ahead when initiating movement.
            // The reasons why it happens and the expected values by the client are unknown.
            _positionForClientTick = GameLoop.GameLoopTime;

            if (!IsDestinationValid)
            {
                _positionForClient = _ownerPosition;
                _destinationForClient = Destination;
                return;
            }

            float magic;
            float ratio;

            if (wasMoving)
                _positionForClient = _ownerPosition;
            else
            {
                magic = (float) (CurrentSpeed * 0.15);
                ratio = (float) ((distanceToTarget + magic) / distanceToTarget);
                _positionForClient = Vector3.Lerp(_destination, _ownerPosition, ratio);
            }

            magic = (float) Math.Max(15, CurrentSpeed * 0.1);
            ratio = (float) ((distanceToTarget + magic) / distanceToTarget);
            _destinationForClient = Vector3.Lerp(_ownerPosition, _destination, ratio);
        }

        private void UpdateMovement(short speed)
        {
            if (speed == 0)
                SnapAutonomousBotToGround();

            // Save current position.
            Owner.X = (int) Math.Round(_ownerPosition.X);
            Owner.Y = (int) Math.Round(_ownerPosition.Y);
            Owner.Z = (int) Math.Round(_ownerPosition.Z);

            _needsBroadcastUpdate = true;
            IsDestinationValid = false;
            bool wasMoving = IsMoving;
            CurrentSpeed = speed;
            UpdateVelocity(0);
            PrepareValuesForClient(wasMoving, 0);
        }

        private void UpdateMovement(Vector3 destination, float distanceToTarget, short speed)
        {
            if (!AllowPosition(destination)) return;
            // Save current position.
            Owner.X = (int) Math.Round(_ownerPosition.X);
            Owner.Y = (int) Math.Round(_ownerPosition.Y);
            Owner.Z = (int) Math.Round(_ownerPosition.Z);

            IsDestinationValid = distanceToTarget >= 0;
            _destination = destination;
            _needsBroadcastUpdate = true;

            bool wasMoving = IsMoving;
            CurrentSpeed = speed;
            UpdateVelocity(distanceToTarget);
            PrepareValuesForClient(wasMoving, distanceToTarget);

        }

        private bool AllowPosition(Vector3 position)
        {
            if (AutonomousRealmBoundary.Allows(Owner, position)) return true;
            _lastPositionUpdateTick = GameLoop.GameLoopTime;
            _velocity = Vector3.Zero;
            CurrentSpeed = 0;
            _pathfinder.ForceReplot = true;
            _needsBroadcastUpdate = true;
            return false;
        }

        private void SnapAutonomousBotToGround()
        {
            if (Owner is not GameBot || Owner is GameBot { IsOnStableMasterRoute: true } || Owner.CurrentZone == null)
                return;

            // A broad nearest-poly snap at every stop could land on an isolated
            // prop or another side of a wall. Only correct slight underground Z
            // with effectively unchanged XY. Never pull an actor down a floor.
            if (!AutonomousNavigationSurface.TryFloor(PathfindingProvider.Instance,
                    Owner.CurrentZone, _ownerPosition, out Vector3 snapped))
                return;

            // Preserve legitimate fine movement while correcting visible feet-
            // through-ground offsets at authoritative stop/combat positions.
            if (Vector3.DistanceSquared(_ownerPosition, snapped) <= 1f)
                return;

            _ownerPosition = snapped;
            _pathfinder.ForceReplot = true;
        }

        private ResetHeadingAction CreateResetHeadingAction()
        {
            return new(Owner, this, () =>
            {
                UnsetFlag(MovementState.TurnTo);
                _resetHeadingAction = null;
            });
        }

        private bool IsFlagSet(MovementState flag)
        {
            return (_movementState & flag) == flag;
        }

        private void SetFlag(MovementState flag)
        {
            _movementState |= flag;
        }

        private void UnsetFlag(MovementState flag)
        {
            _movementState &= ~flag;
        }

        private delegate void MovementRequestAction(Vector3 destination, short speed);

        private enum MovementRequestType
        {
            Walk,
            Path
        }

        private class MovementRequest
        {
            public MovementRequestType Type { get; private set; }
            public Vector3 Destination { get; private set; }
            public short Speed { get; private set; }

            public void Set(MovementRequestType type, Vector3 destination, short speed)
            {
                Type = type;
                Destination = destination;
                Speed = speed;
            }
        }

        private class ResetHeadingAction : ECSGameTimerWrapperBase
        {
            private NpcMovementComponent _movementComponent;
            private ushort _oldHeading;
            private long _oldMovementStartTick;
            private Action _onCompletion;

            public ResetHeadingAction(GameObject actionSource, NpcMovementComponent movementComponent, Action onCompletion) : base(actionSource)
            {
                _movementComponent = movementComponent;
                _oldHeading = actionSource.Heading;
                _oldMovementStartTick = movementComponent.MovementStartTick;
                _onCompletion = onCompletion;
            }

            protected override int OnTick(ECSGameTimer timer)
            {
                GameNPC owner = _movementComponent.Owner;

                if (_oldMovementStartTick == _movementComponent.MovementStartTick &&
                    !_movementComponent.IsMoving &&
                    owner.IsAlive &&
                    owner.ObjectState is eObjectState.Active &&
                    !owner.attackComponent.AttackState)
                {
                    _movementComponent.TurnTo(_oldHeading);
                }

                _onCompletion();
                return 0;
            }
        }

        [Flags]
        private enum MovementState
        {
            None = 0,
            Request = 1 << 1,     // Was requested to move.
            WalkTo = 1 << 2,      // Is moving and has a destination.
            Follow = 1 << 3,      // Is following an object.
            OnPath = 1 << 4,      // Is following a path / is patrolling.
            AtPathPoint = 1 << 5, // Is waiting at a path point.
            Pathfinding = 1 << 6, // Is moving using Pathfinder.
            TurnTo = 1 << 7       // Is facing a direction for a certain duration.
        }
    }
}

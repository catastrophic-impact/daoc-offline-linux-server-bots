using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.Keeps;
using DOL.GS.ServerRules;
using DOL.GS.Spells;
using DOL.Logging;

namespace DOL.GS
{
    /// <summary>
    /// Executes persistent playerbot goals entirely in the live world. Planning
    /// is deliberately infrequent; ordinary NPC movement, combat and spell code
    /// remain authoritative between planning decisions.
    /// </summary>
    public sealed partial class AutonomousWorldBotController
    {
        private const int CampCellSize = 4200;
        // Source-empty CapnBry zones are rebuilt from live NPCs.  Their live
        // clusters are intentionally smaller than the ordinary 4,200-unit
        // catalog cell so two nearby spawns cannot be averaged into a wall,
        // river, or empty ground between them.
        private const int SourceEmptyCampCellSize = 1500;
        private const int CampArrivalRadius = 1250;
        private const int ImmediateTargetSearchRadius = 2600;
        // Search the local camp rather than one database spawn point. Dungeon
        // candidates still pass the floor/wall line-of-sight gate below, so a
        // larger outdoor scan never targets through a dungeon wall or floor.
        private const int TargetSearchRadius = 5000;
        private const int DungeonBlockerRadius = 1450;
        private const int ZonePointArrivalRadius = 190;
        private const int EmptyCampReplanMilliseconds = 75_000;
        private const int DarknessFallsSoloRespawnHoldMilliseconds = 240_000;
        private const int EmptyGroupCampRoamMilliseconds = 180_000;
        private const int RouteStallReplotMilliseconds = 12_000;
        private const int RouteRecoveryAttemptsBeforeNewGoal = AutonomousRouteRecoveryPolicy.MaximumLocalAttempts;
        private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object ZonePointLock = new();
        private static DbZonePoint[] _zonePoints;
        // Keep one bot from repeatedly selecting a transition endpoint that
        // just failed from its current surface. A failed local approach must
        // never quarantine the same capital gate for every other bot.
        private static readonly ConcurrentDictionary<string, long> FailedZonePointUntil = new();
        private const long FailedZonePointQuarantineMilliseconds = 10 * 60_000;

        private CampDestination _camp;
        private CampDestination _restingCamp;
        private long _campStartedTick;
        private long _emptyCampSinceTick;
        private string _reportedEmptySharedCampId = string.Empty;
        private long _nextPlanTick;
        private long _nextMeetupPlanTick;
        private long _nextTargetSearchTick;
        private long _nextFailedSoloPullRoutePruneTick;
        private readonly Dictionary<ushort, SavageBotCombatPolicy.FailedSoloPullRoute> _failedSoloPullRoutes = new();
        private long _nextFailedSavageMeleePullPruneTick;
        private readonly Dictionary<ushort, long> _failedSavageMeleePullTargets = new();
        private GameNPC _savageMeleePullTarget;
        private string _savageMeleePullCampId = string.Empty;
        private string _savageMeleePullAssignmentId = string.Empty;
        private Group _savageMeleePullGroup;
        private Vector3 _savageMeleePullLastPosition;
        private int _savageMeleePullLastDistance;
        private int _savageMeleePullTargetHealth;
        private long _savageMeleePullLastAttackTick;
        private long _savageMeleePullLastAttackedTick;
        private long _savageMeleePullStartedTick;
        private long _savageMeleePullProgressTick;
        private bool _savageMeleePullRetried;
        // 3,020 of the first live run's archer pull timeouts stood 299-300 units
        // from the target: inside the native NPC bow "melee" distance
        // (melee range + half a second of movement), outside the bot's own.
        private const int ArcherStallMeleeDistance = 450;
        private const int ArcherMeleeFallbackMilliseconds = 20_000;
        private object _pullWatchCastHandler;
        private long _pullWatchCastSinceTick;
        private const string PendingSavageMeleePullHandoffKey = "AutonomousSavageMeleePullHandoff";
        private const string FailedSavageMeleePullHandoffKey = "AutonomousFailedSavageMeleePullHandoff";
        private readonly record struct PendingSavageMeleePullHandoff(GameNPC Target, Group Group,
            string AssignmentId, long AttackTick, long AttackedTick, int TargetHealth);
        private readonly record struct FailedSavageMeleePullHandoff(ushort TargetId, ushort RegionId,
            Group Group, string AssignmentId, long RetryAfterTick);
        private long _nextMoveOrderTick;
        private long _nextDarknessFallsEvacuationProbeTick;
        private Vector3 _lastDarknessFallsEvacuationPosition;
        private long _lastDarknessFallsEvacuationProgressTick;
        private long _nextStatusSaveTick;
        private long _nextStableCheckTick;
        private AutonomousStableRoutePlanner.Choice _pendingStableChoice;
        private bool _pendingStablePortalRide;
        private bool _connectedPortalRideSearchFoundNoChoice;
        private string _meetupHorseFailedGroupId = string.Empty;
        private AutonomousCapitalTransit.Plan _capitalTransit;
        private string _capitalTransitAssignment;
        private string _capitalTransitGroup;
        private string _capitalTransitPhase;
        private Vector3 _pendingStableWaypoint;
        private readonly Dictionary<GameStableMaster, long> _failedBoardingMasters = new();
        private readonly Dictionary<GameTrainer, long> _failedTrainerAnchors = new();
        private readonly AutonomousStableSourceQuarantine _stableSourceQuarantine = new();
        private Vector3 _pathSegmentOrigin;
        private AutonomousZoneBoundaryRouting.Step? _resolvedBoundaryStep;
        private Zone _resolvedBoundaryZone;
        private Vector3? _patrolDestination;
        // RvR uses the same collision-safe/stable/zone route machinery as PvE,
        // but never treats the destination as a monster camp.
        private CampDestination _rvrDestination;
        private bool _rvrSharedEvent;
        private long _nextSiegeCombatAttendanceTick;
        public const int SiegeCombatAttendanceRadius = 9_500;

        /// <summary>
        /// A siege participant fighting at the keep (inside the rally-post ring) is physically there.
        /// Presence was only reported at a bot's own rally post outside combat: run 12's Albion army
        /// fought Midgard's defenders 2,000-6,000 units from Arvakr Faste and counted 0 present.
        /// </summary>
        private void ReportSiegeCombatAttendance(GameBot bot, long nowTick)
        {
            if (!_rvrSharedEvent || _rvrDestination == null || nowTick < _nextSiegeCombatAttendanceTick ||
                !_rvrDestination.Id.StartsWith("rvr-keep-", StringComparison.Ordinal) ||
                bot.CurrentRegionID != _rvrDestination.RegionId ||
                Distance(bot.X, bot.Y, _rvrDestination.X, _rvrDestination.Y) > SiegeCombatAttendanceRadius)
                return;
            _nextSiegeCombatAttendanceTick = nowTick + 2_000 + bot.ObjectID % 500;
            string forceId = _groupDirective?.IsDynamic == true ? _groupDirective.GroupId : $"rvr-{bot.DatabaseID}";
            AutonomousRvrEventLayer.ReportAttendance(_rvrDestination.Id, forceId, bot.Realm, bot.DatabaseID, true, nowTick,
                bot, new(bot.X, bot.Y, bot.Z));
        }
        private Vector3? _rvrApproachDestination;
        private bool _soloRvrBorderStaged;
        private Vector3? _soloRvrStagingPoint;
        private AutonomousRvrStaging.BorderKeep? _soloRvrBorderKeep;
        private int _observedDeathCount = -1;
        private int _deathDifficultySteps;
        private int _recentSoloDeathsWithoutExperience;
        private long _lastSoloDeathTick;
        private long _experienceAtLastSoloDeath;
        private int _levelAtLastSoloDeath;
        private long _soloDeathRecoveryUntilTick;
        private bool _recoverAfterSoloDeath;
        private readonly Dictionary<string, long> _recentFailedSoloCamps = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _recentFailedSoloTargets = new(StringComparer.OrdinalIgnoreCase);
        private Vector3 _lastRoutePosition;
        private long _lastRouteProgressTick;
        private int _routeStallReplans;
        private Vector3? _issuedRouteDestination;
        private Vector3? _routeRecoveryWaypoint;
        private float _routeRecoveryBaselineDistance = -1f;
        private ushort _lastTerminalRouteFailureRegion;
        private Vector3 _lastTerminalRouteFailurePosition;
        private long _lastTerminalRouteFailureTick;
        private int _terminalRouteFailuresInPocket;
        private bool _routeInterruptedByCombat;
        private bool _recoverBeforeNextCampTarget;
        private ConColor? _lastEngagedCon;
        private string _lastFailedCampId = string.Empty;
        private string _lastFailedTargetName = string.Empty;
        private AutonomousBotGroupCoordinator.Directive _groupDirective;
        private Group _observedGroup;
        private long _nextGroupPulseTick;
        private string _activeDynamicGroupId = string.Empty;
        private bool _regroupRouteCleared;
        private GameTrainer _trainingTrainer;
        private long _nextTrainingDecisionTick;
        private string _observedObjectiveAssignmentId = string.Empty;
        private GameNPC _serviceNpc;
        private eWorldServiceKind? _serviceKind;
        private long _nextServiceSearchTick;
        private long _nextRvrReleaseCheckTick;
        private Vector3? _verifiedApproachTarget;
        private Vector3? _verifiedApproachPoint;
        private Zone _verifiedApproachZone;
        private int _verifiedApproachRadius;
        // Negative cache: the same unresolvable approach from the same spot is
        // not re-searched (dozens of navmesh queries) on every brain turn.
        private Vector3? _failedApproachTarget;
        private Vector3 _failedApproachFrom;
        private Zone _failedApproachZone;
        private int _failedApproachRadius;
        private long _failedApproachUntilTick;

        private sealed record CampDestination(
            string Id,
            string MonsterName,
            string ZoneName,
            ushort RegionId,
            int X,
            int Y,
            int Z,
            int LiveMobCount,
            bool IsDungeon,
            bool IsFrontier,
            ConColor? FallbackMaximumCon = null,
            int TargetLevel = 0);

        private sealed record CampCatalogCell(
            string Id,
            string MonsterName,
            string ZoneName,
            ushort RegionId,
            int X,
            int Y,
            int Z,
            int[] Levels,
            int LiveMobCount,
            Zone Zone,
            bool IsDungeon,
            bool IsFrontier,
            bool NeedsProjection = true,
            eRealm DarknessFallsWing = eRealm.None,
            float DarknessFallsAlbionDistance = float.PositiveInfinity,
            float DarknessFallsMidgardDistance = float.PositiveInfinity,
            float DarknessFallsHiberniaDistance = float.PositiveInfinity);

        private static CampCatalogCell[] _campCatalog = [];

        public bool Tick(BotBrain brain)
        {
            GameBot bot = brain?.BotBody;
            if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup)
                return false;

            try
            {
                long nowTick = GameLoop.GameLoopTime;
                // A solo siege force is released at most a couple of seconds
                // after its event ends; no need to take the event lock each turn.
                if (bot.Group == null && nowTick >= _nextRvrReleaseCheckTick)
                {
                    _nextRvrReleaseCheckTick = nowTick + 2_000;
                    if (AutonomousRvrEventLayer.TryConsumeRelease($"rvr-{bot.DatabaseID}", nowTick, out string eventReason))
                        AutonomousObjectiveAssignments.BeginSoloAfterGroupTask(bot, eventReason);
                }
                if (_observedGroup != bot.Group || nowTick >= _nextGroupPulseTick)
                {
                    _observedGroup = bot.Group;
                    _groupDirective = AutonomousBotGroupCoordinator.Pulse(bot);
                    _nextGroupPulseTick = nowTick + 1_500 + bot.ObjectID % 500;
                }
                string assignmentId = bot.PersistentRecord?.ObjectiveAssignmentId ?? string.Empty;
                if (!string.Equals(_observedObjectiveAssignmentId, assignmentId, StringComparison.Ordinal))
                {
                    AutonomousGoalDiagnostics.End(bot, GoalAttemptEnd.Reassigned, "Objective assignment changed");
                    ReleaseOwnedSiegeRams(bot);
                    // A solo siege force leaves its event with its RvR task. It used to keep the slot:
                    // run 10 counted 44 Hibernian "attackers" of Caer Erasleigh that were grinding PvE.
                    if (!AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR))
                        AutonomousRvrEventLayer.RemoveForce($"rvr-{bot.DatabaseID}");
                    _observedObjectiveAssignmentId = assignmentId;
                    _keepPlanning = null; _keepTravelPoints = null; _keepTravelKey = null;
                    _keepTravelLastPosition = null;
                    ResetTownIdle();
                    _camp = null;
                    _rvrDestination = null;
                    _rvrApproachDestination = null;
                    bot.TempProperties.RemoveProperty(AutonomousFrontierTransport.RequestKey);
                    bot.TempProperties.RemoveProperty("RvrSupplying");
                    _frontierPorter = null;
                    _nextPorterSearch = 0;
                    _soloRvrBorderStaged = false;
                    _soloRvrStagingPoint = null;
                    _soloRvrBorderKeep = null;
                    _campStartedTick = 0;
                    _emptyCampSinceTick = 0;
                    _patrolDestination = null;
                    _pendingStableChoice = null;
                    _nextPlanTick = 0;
                    if (!bot.IsOnStableMasterRoute)
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                    }
                }
                if (_groupDirective?.IsDynamic == true &&
                    !string.Equals(_activeDynamicGroupId, _groupDirective.GroupId, StringComparison.Ordinal))
                {
                    // Group assignment supersedes the bot's private task at
                    // once.  No stale solo destination survives matchmaking.
                    _activeDynamicGroupId = _groupDirective.GroupId;
                    AutonomousGoalDiagnostics.End(bot, GoalAttemptEnd.GroupChanged, "Joined a new autonomous group");
                    // The party is the force now; a solo siege reservation must not outlive it.
                    AutonomousRvrEventLayer.RemoveForce($"rvr-{bot.DatabaseID}");
                    _camp = null;
                    _campStartedTick = 0;
                    _emptyCampSinceTick = 0;
                    _patrolDestination = null;
                    _pendingStableChoice = null;
                    _nextPlanTick = 0;
                    // A new group assignment changes the destination after the
                    // current authoritative ticket leg. It must not make a rider
                    // jump off mid-route.
                    if (!bot.IsOnStableMasterRoute)
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                    }
                    if (bot.PersistentRecord != null)
                        bot.PersistentRecord.CurrentCampId = string.Empty;
                }
                else if (_groupDirective?.IsDynamic != true && _activeDynamicGroupId.Length > 0)
                {
                    // A disbanded bot chooses new independent work instead of
                    // silently continuing the former party's shared camp.
                    _activeDynamicGroupId = string.Empty;
                    AutonomousGoalDiagnostics.End(bot, GoalAttemptEnd.GroupChanged, "Autonomous group disbanded");
                    _camp = null;
                    _campStartedTick = 0;
                    _emptyCampSinceTick = 0;
                    _patrolDestination = null;
                    _pendingStableChoice = null;
                    _nextPlanTick = 0;
                }
                ObserveDeaths(bot);

                // Every autonomous return uses the same navmesh controller,
                // even before a casualty has propagated into group metadata.
                if (!bot.IsPlayerLedGroup)
                    bot.HandOverReleaseReturnToGroup();
                if (_groupDirective?.Phase != "Regrouping")
                    _regroupRouteCleared = false;

                if (!bot.IsAlive || bot.IsReturningAfterRelease)
                {
                    ClearSavageMeleePull();
                    bot.TempProperties.RemoveProperty(PendingSavageMeleePullHandoffKey);
                    return false;
                }

                AcceptSavageMeleePullHandoff(brain, bot);
                ClearStaleCast(bot);
                // A selected Savage pull can have aggro without a landed blow.
                // Observe it before the normal combat return so a failed melee
                // approach cannot preserve the same untouched target forever.
                if (ObserveSavageMeleePull(brain, bot))
                    return true;

                if (_rvrSharedEvent && _rvrDestination != null)
                    AutonomousRvrEventLayer.ReportTravel(_rvrDestination.Id,
                        _groupDirective?.GroupId ?? $"rvr-{bot.DatabaseID}", bot.DatabaseID,
                        double.PositiveInfinity, false, nowTick, bot);

                // A ticket ride is an uninterrupted world route. Resolve it
                // before ordinary combat/rest logic so the rider cannot peel
                // off the horse because an NPC happened to notice it.
                if (HandleStableTravel(bot))
                    return true;

                // Shared dungeons are RvR areas even when the bot's durable
                // assignment is PvE. Only opponents actually present inside
                // the local awareness radius are considered; no human player
                // or enemy bot is selected through global knowledge.
                if (TryEngageSharedDungeonOpponent(brain, bot))
                    return false;

                // Native siege damage sets the operator's combat timer. That
                // alone must not hand control back to the ordinary melee FSM
                // after the first shot. Real incoming aggro still wins.
                if (!brain.HasAggro && !bot.IsAttacking && !bot.IsCasting &&
                    AutonomousObjectiveAssignments.Is(bot,eAutonomousObjectiveKind.RvR) && BotSiegeRuntime.HoldingPosition(bot))
                    return ExecuteRvr(brain,bot);

                bool postCombatRecovery = BotRestRecovery.NeedsOrContinuesRest(bot) && !BotRestRecovery.BlocksRest(bot);
                if (brain.HasAggro || (bot.InCombat && !postCombatRecovery) || bot.IsAttacking)
                {
                    // Defense owns movement; do not resume an old dragon-ring
                    // waypoint from a different position after the fight.
                    _dragonRallyPlanning = null;
                    _dragonRallyRoute = null;
                    _dragonRallyRetry = 0;
                    if (_camp != null && _groupDirective?.IsDynamic != true)
                        _recoverBeforeNextCampTarget = true;

                    // Combat temporarily owns movement. Preserve the actual
                    // travel destination, but discard any short wall-detour so
                    // the canonical route is replotted once combat ends.
                    if (!_routeInterruptedByCombat && _issuedRouteDestination.HasValue)
                    {
                        _routeInterruptedByCombat = true;
                        _routeRecoveryWaypoint = null;
                        bot.StopMoving();
                        bot.ForcePathReplot();
                    }
                    if (bot.TargetObject is GameLiving target)
                        SetStatus(bot, $"Fighting {target.Name}", GoalText(), $"Engaged level {target.EffectiveLevel} {target.Name}", target.Name);
                    ReportSiegeCombatAttendance(bot, nowTick);
                    return false;
                }

                if (RetireStaleDarknessFallsGoalAndEvacuate(bot))
                    return true;

                // Roadside combat pauses the entire formation. Once it ends,
                // every member holds while the coordinator waits for the whole
                // party to reach full resources. Attack events still run above
                // this gate and wake recovery immediately.
                // Defense above remains authoritative. A member safely outside
                // must finish a committed portal crossing, not rest outside
                // while its tank (or a corpse needing resurrection) is inside.
                if (AutonomousBotGroupCoordinator.IsCompletingDungeonEntry(bot, _groupDirective) &&
                    (!bot.IsCasting || BotSongTwistPolicy.HasMobileSongCast(bot)))
                    return TravelAcrossRegions(bot);

                if (_groupDirective?.IsDynamic == true &&
                    AutonomousBotGroupCoordinator.ShouldPauseForGroupPullRecovery(_groupDirective.Phase,
                        _groupDirective.GroupCombatActive, _groupDirective.RecoveringBetweenPulls) &&
                    (AutonomousRealmRaid.GetView(bot.Group) == null ||
                     _groupDirective.Leader?.CurrentRegionID == bot.CurrentRegionID && bot.GetDistanceTo(_groupDirective.Leader) <= 1800))
                {
                    // PvE pauses around one defensive pull. RvR combat is not
                    // a puller/assist encounter: each available member must be
                    // allowed through to its own nearby enemy acquisition.
                    bool distributeRvrCombat = _groupDirective.ObjectiveKind == eAutonomousObjectiveKind.RvR &&
                                               _groupDirective.GroupCombatActive;
                    if (!distributeRvrCombat && !AutonomousRvrEventLayer.IsBattleForce(_groupDirective.GroupId, nowTick))
                        return HandleGroupCombatAndRecovery(brain, bot, _groupDirective);
                }

                if (_recoverAfterSoloDeath)
                {
                    if (_groupDirective?.IsDynamic == true ||
                        !AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.SoloPve))
                        _recoverAfterSoloDeath = false;
                    else if (HoldSoloDeathRecovery(bot))
                        return true;
                }

                // Keep a camp's recovery session across AI turns. Clearing it on
                // every tick restarts the pose and loses recovery-to-full. Only
                // an obsolete camp's sit state is stale; routing below also wakes
                // the bot when movement actually takes over.
                bool recoveringAtCurrentCamp = _camp != null && ReferenceEquals(_restingCamp, _camp);
                if (bot.IsRecoveryResting && !recoveringAtCurrentCamp && !_dungeonRouteResting &&
                    _groupDirective?.Phase != "Regrouping" && !_townIdleUntilUtc.HasValue)
                    bot.WakeRecoveryRest();

                if (bot.IsCasting && !BotSongTwistPolicy.HasMobileSongCast(bot))
                    return true;

                if (HandleRealmRaidStableTravel(bot))
                    return true;

                if (TryReturnFromForeignFrontier(bot))
                    return true;

                if (HandleCapitalTransit(bot))
                    return true;

                if (AutonomousRealmRaid.GetTravelView(bot) is { Muster: false } expedition &&
                    TravelRealmExpedition(bot, expedition)) return true;

                // Custody outlives an ordinary timed task. Finish the physical
                // return before matchmaking can send a carrier to PvE/services.
                if (GameRelic.IsPlayerCarryingRelic(bot))
                    return ExecuteRvr(brain, bot);

                if (_groupDirective?.IsDynamic == true && _groupDirective.Camp == null &&
                    _groupDirective.Phase == "Choosing group target" && _camp != null)
                {
                    AutonomousGoalDiagnostics.End(bot, GoalAttemptEnd.GroupChanged, "Group size changed; choosing a suitable target");
                    _camp = null;
                    _nextPlanTick = 0;
                }
                // Siege membership is independent of town/full-party attendance.
                // Ordinary PvE and uncommitted roaming formations are unchanged.
                if (AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR) && bot.Level == 50)
                {
                    string eventForce = _groupDirective?.IsDynamic == true ? _groupDirective.GroupId : $"rvr-{bot.DatabaseID}";
                    if (_rvrDestination == null && nowTick >= _nextRvrPlanReview)
                    {
                        _nextRvrPlanReview = nowTick + 45_000;
                        _rvrDestination = ChooseRvrDestination(bot);
                    }
                    if (AutonomousRvrEventLayer.IsBattleForce(eventForce, nowTick))
                        return ExecuteRvr(brain, bot);
                }
                if (_groupDirective?.IsDynamic == true && _groupDirective.Phase == "Regrouping" &&
                    !AutonomousRvrEventLayer.IsBattleForce(_groupDirective.GroupId, nowTick))
                    return HandleRecoveryRegroup(brain, bot);
                if (_groupDirective?.IsDynamic == true &&
                    AutonomousBotGroupCoordinator.IsAssemblyPhase(_groupDirective.Phase) &&
                    !(_groupDirective.ObjectiveKind == eAutonomousObjectiveKind.RvR &&
                      AutonomousRvrEventLayer.IsBattleForce(_groupDirective.GroupId, nowTick)))
                {
                    // A wipe/no-show resize invalidates the old shared
                    // camp even when the group ID itself has not changed.
                    if (_camp != null)
                    {
                        AutonomousGoalDiagnostics.End(bot, GoalAttemptEnd.GroupChanged, "Group assembling for a fresh shared goal");
                        _camp = null;
                        _nextPlanTick = 0;
                        if (bot.PersistentRecord != null)
                            bot.PersistentRecord.CurrentCampId = string.Empty;
                    }
                    // Meetup overhaul: the leader of a forming PvE party picks the camp now, so the
                    // party meets at the safe town nearest it and every member travels at once.
                    if (_groupDirective.Leader == bot && _groupDirective.Phase == "Leader staging" &&
                        _groupDirective.ObjectiveKind == eAutonomousObjectiveKind.GroupPve && _hasCampCatalog &&
                        nowTick >= _nextMeetupPlanTick)
                    {
                        _nextMeetupPlanTick = nowTick + 30_000;
                        SelectCamp(bot);
                        AutonomousBotGroupCoordinator.SharedCamp planned = _camp != null ? ToSharedCamp(_camp) : null;
                        if (_camp != null)
                        {
                            AutonomousGoalDiagnostics.End(bot, GoalAttemptEnd.GroupChanged, "Camp planned for the party meetup");
                            _camp = null;
                            _nextPlanTick = 0;
                        }
                        if (AutonomousBotGroupCoordinator.PlanMeetupNearCamp(bot, planned))
                            _groupDirective = AutonomousBotGroupCoordinator.Pulse(bot);
                    }
                    return HandleGroupRendezvous(bot, _groupDirective);
                }

                eAutonomousObjectiveKind objectiveKind = AutonomousObjectiveAssignments.KindFor(bot);
                if (objectiveKind == eAutonomousObjectiveKind.GroupPve && _groupDirective?.IsDynamic != true)
                {
                    if (bot.CurrentZone?.IsDungeon == true)
                        return LeaveDungeonForGroupMatchmaking(bot);
                    // While the matchmaker assembles a full roster the bot keeps
                    // grinding an outdoor camp instead of standing idle for up to
                    // twenty minutes. It stays in the matchmaking pool (recruited
                    // between fights) and the group directive replaces this solo
                    // goal the moment a party forms.
                }

                if (objectiveKind == eAutonomousObjectiveKind.RvR)
                    return ExecuteRvr(brain, bot);

                if (_groupDirective?.IsDynamic != true && AutonomousObjectiveAssignments.IsBetweenPveTasks(bot))
                {
                    if (!AutonomousObjectiveAssignments.BetweenTaskServiceExpired(bot))
                    {
                        if (AutonomousObjectiveAssignments.WantsBetweenTaskTraining(bot) && HandlePendingTraining(bot))
                        {
                            AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                            return true;
                        }
                        if (AutonomousObjectiveAssignments.WantsBetweenTaskInventory(bot) && TryRouteToNeededService(bot))
                            return true;
                        if (AutonomousObjectiveAssignments.WantsBetweenTaskDowntime(bot) && HandleTownIdle(brain, bot))
                            return true;
                    }
                    AutonomousObjectiveAssignments.CompleteBetweenTaskServices(bot);
                    _serviceNpc = null;
                    _serviceKind = null;
                    _trainingTrainer = null;
                    _nextPlanTick = 0;
                    return true;
                }

                if (objectiveKind == eAutonomousObjectiveKind.Battleground && _groupDirective?.IsDynamic != true)
                    return ExecuteBattleground(brain, bot);

                if (_groupDirective?.IsDynamic == true && _groupDirective.Camp != null &&
                    (!string.Equals(_camp?.Id, _groupDirective.Camp.Id, StringComparison.Ordinal) ||
                     _camp?.TargetLevel != _groupDirective.Camp.TargetLevel ||
                     _camp?.X != _groupDirective.Camp.X || _camp?.Y != _groupDirective.Camp.Y ||
                     _camp?.Z != _groupDirective.Camp.Z))
                {
                    _camp = FromSharedCamp(_groupDirective.Camp);
                    _reportedEmptySharedCampId = string.Empty;
                    BeginCampDiagnostics(bot);
                }

                if (_camp == null && GameServiceUtils.ShouldTick(_nextPlanTick))
                {
                    if (_groupDirective?.IsDynamic != true || _groupDirective.Leader == bot)
                    {
                        SelectCamp(bot);
                        if (_camp != null && _groupDirective?.IsDynamic == true)
                        {
                            AutonomousBotGroupCoordinator.PublishCamp(bot, ToSharedCamp(_camp));
                            _groupDirective = AutonomousBotGroupCoordinator.Pulse(bot);
                        }
                        else if (_camp == null && _hasCampCatalog && _groupDirective?.IsDynamic == true)
                        {
                            AutonomousBotGroupCoordinator.ReportNoAvailableCamp(bot);
                            _groupDirective = AutonomousBotGroupCoordinator.Pulse(bot);
                        }
                    }
                }

                if (_camp == null)
                {
                    if (!_hasCampCatalog)
                    {
                        SetStatus(bot, "Preparing live XP camps", "Find a reachable level-appropriate XP camp",
                            "Waiting for the first complete catalog; this is not a route or group failure");
                        return true;
                    }
                    if (_groupDirective?.IsDynamic == true)
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                        SetStatus(bot, "Holding group formation", _groupDirective.SharedGoal,
                            "The party is assembled while its leader chooses one shared target");
                        return true;
                    }
                    SetStatus(bot, "Searching for a valid XP camp", "Find a reachable level-appropriate XP camp",
                        "No reachable non-grey camp is currently available; retrying shortly");
                    return true;
                }

                if (bot.CurrentRegionID != _camp.RegionId)
                {
                    if (_groupDirective?.IsDynamic == true && _groupDirective.Leader != bot)
                        return FollowDynamicGroupLeader(bot, _groupDirective);
                    if (_groupDirective?.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
                        !AutonomousBotGroupCoordinator.IsCohesive(_groupDirective))
                    {
                        DbZonePoint next = FindNextCrossing(bot, _camp.RegionId, _camp.X, _camp.Y);
                        bool enteringStagingArea = next != null && _camp.IsDungeon && next.TargetRegion == _camp.RegionId &&
                            Distance(bot.X, bot.Y, next.SourceX, next.SourceY) <= AutonomousDungeonPolicy.DungeonEntranceStagingRadius;
                        bool crossingStarted = next != null && bot.Group.GetMembersInTheGroup()
                            .Any(member => member.IsAlive && member.CurrentRegionID == next.TargetRegion);
                        bool farFromCrossing = next == null || Distance(bot.X, bot.Y, next.SourceX, next.SourceY) > AutonomousGroupPace.TrailRadius;
                        if (!enteringStagingArea && !crossingStarted && farFromCrossing &&
                            KeepGroupMovingAtPace(bot))
                            return TravelAcrossRegions(bot);
                        if (!enteringStagingArea && !crossingStarted)
                        {
                            bot.StopMovingOnPath();
                            bot.StopMoving();
                            SetStatus(bot, "Waiting for group members", GoalText(),
                                "Holding the route until the party catches up before the next region crossing");
                            return true;
                        }
                    }
                    return TravelAcrossRegions(bot);
                }

                if (_groupDirective?.IsDynamic == true &&
                    AutonomousBotGroupCoordinator.ShouldHoldDungeonArrival(bot, _groupDirective, out string dungeonHold))
                    return HandleDungeonArrivalHold(brain, bot, dungeonHold);

                var raidOrder = AutonomousRealmRaid.GetView(bot.Group);
                if (raidOrder?.Hold == true)
                {
                    if (_groupDirective?.Leader != bot) return FollowDynamicGroupLeader(bot, _groupDirective);
                    Vector3 post = new(raidOrder.Camp.X, raidOrder.Camp.Y, raidOrder.Camp.Z);
                    if (!AutonomousBotGroupCoordinator.IsCohesive(_groupDirective))
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                        SetStatus(bot, "Waiting for expedition party", raidOrder.Camp.MonsterName,
                            "Keeping all eight members together on the route from the service hub");
                        return true;
                    }
                    if (TryBeginFasterStableRoute(bot, post, raidOrder.Camp.ZoneName)) return true;
                    int arrival = raidOrder.Crossing ? 32 : 175;
                    if (Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), post) > arrival * arrival &&
                        FollowDarknessFallsRaidChain(bot, post, raidOrder.Camp.MonsterName)) return true;
                    if (Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), post) > arrival * arrival)
                        IssuePath(bot, post, preciseArrival: raidOrder.Crossing);
                    else { bot.StopMovingOnPath(); bot.StopMoving(); }
                    SetStatus(bot, raidOrder.State, raidOrder.Camp.MonsterName,
                        "Holding the party's connected raid staging point; ordinary defense and upkeep remain active");
                    return true;
                }
                bool certifiedDarknessFallsCamp = _camp.RegionId == AutonomousDarknessFallsPolicy.RegionId &&
                    bot.CurrentRegionID == AutonomousDarknessFallsPolicy.RegionId && raidOrder == null;
                if (certifiedDarknessFallsCamp)
                {
                    // All group members, not only the leader, use the same
                    // exact-spawn 3D chain. Following the leader with one long
                    // generic path can silently end at a DF cliff or wall.
                    if (_groupDirective?.IsDynamic == true && _groupDirective.Leader == bot &&
                        !AutonomousBotGroupCoordinator.IsCohesive(_groupDirective))
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                        SetStatus(bot, "Waiting for group members", GoalText(),
                            "Holding the certified route until every living bot is back in formation",
                            _camp.MonsterName, _camp.ZoneName);
                        return true;
                    }
                    if (FollowDarknessFallsCampRoute(bot)) return true;
                }
                if (_camp == null) return true; // the route step above may abandon the camp
                int campDistance = Distance(bot.X, bot.Y, _camp.X, _camp.Y);
                // Intercept before walking onto the spawn's camp coordinate.
                // The old arrival-first order placed melee tanks inside packs
                // before the ranged pull policy ever had a chance to run.
                if (campDistance <= 2000 && _groupDirective?.Puller == bot &&
                    AutonomousBotGroupCoordinator.UsesDefensivePullGroup(bot.Group) &&
                    AutonomousBotGroupCoordinator.CanInitiateNewPull(bot) &&
                    GameLoop.GameLoopTime >= _nextTargetSearchTick)
                {
                    _nextTargetSearchTick = GameLoop.GameLoopTime + 1500;
                    GameNPC nearbyGoal = FindCampTarget(bot);
                    if (nearbyGoal != null && AutonomousDefensivePull.TryBeginFlyingHandoff(bot, nearbyGoal))
                    {
                        SetStatus(bot, $"Ranged pulling {nearbyGoal.Name}", GoalText(),
                            "The designated puller handed an unreachable flying target to a ranged party member",
                            nearbyGoal.Name, _camp.ZoneName);
                        return true;
                    }
                    if (nearbyGoal != null &&
                        !SavageBotCombatPolicy.MustMeleePull((eCharacterClass)bot.CharacterClass.ID) &&
                        AutonomousDefensivePull.TryBegin(bot, nearbyGoal))
                    {
                        SetStatus(bot, $"Ranged pulling {nearbyGoal.Name}", GoalText(),
                            "Holding outside the spawn pack; drawing the assigned target back to the party",
                            nearbyGoal.Name, _camp.ZoneName);
                        return true;
                    }
                }
                bool physicallyAtDungeonCampPoint = certifiedDarknessFallsCamp ||
                    campDistance <= 120 && Math.Abs(bot.Z - _camp.Z) <= 100;
                bool dungeonApproach = bot.CurrentZone?.IsDungeon == true && !physicallyAtDungeonCampPoint &&
                    (campDistance > 220 || Math.Abs(bot.Z - _camp.Z) > 100 ||
                     !PathfindingProvider.Instance.HasLineOfSight(bot.CurrentZone, new(bot.X, bot.Y, bot.Z),
                         new(_camp.X, _camp.Y, _camp.Z), PathfindingProvider.Instance.DefaultFilters));
                // DF arrival is the certified attackable floor position. The
                // authored monster coordinate may be farther away or above
                // that floor; never replace the ordered chain with a generic
                // direct PathTo merely because raw spawn distance is large.
                if (!certifiedDarknessFallsCamp && (campDistance > CampArrivalRadius || dungeonApproach))
                {
                    _emptyCampSinceTick = 0;
                    _patrolDestination = null;
                    if (_groupDirective?.IsDynamic == true && _groupDirective.Leader != bot)
                        return FollowDynamicGroupLeader(bot, _groupDirective);
                    if (_groupDirective?.IsDynamic == true && !AutonomousBotGroupCoordinator.IsCohesive(_groupDirective) &&
                        !KeepGroupMovingAtPace(bot))
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                        SetStatus(bot, "Waiting for group members", GoalText(),
                            "Holding the route until every living bot is back in formation", _camp?.MonsterName ?? string.Empty,
                            _camp?.ZoneName ?? string.Empty);
                        return true;
                    }
                    // Each step below can abandon the camp (IssuePath rejects a route with no safe seam and
                    // clears _camp, then returns false); stop this turn instead of reading the cleared camp.
                    // Run 15: 73 NullReferenceExceptions in Tick, all right after such a route failure.
                    if (_camp == null) return true;
                    if ((_groupDirective?.IsDynamic != true || _groupDirective.Leader == bot) &&
                        TryBeginFasterStableRoute(bot, new Vector3(_camp.X, _camp.Y, _camp.Z), _camp.ZoneName))
                        return true;
                    if (_camp == null) return true;
                    // Look ahead for aggressive monsters on the way (pull, go around, or give up).
                    if (GuardOpenWorldTravel(bot, new Vector3(_camp.X, _camp.Y, _camp.Z)))
                        return true;
                    if (_camp == null) return true;
                    if (!IssuePath(bot, new Vector3(_camp.X, _camp.Y, _camp.Z)) || _camp == null)
                        return true;
                    SetStatus(bot, $"Traveling to {_camp.MonsterName}", GoalText(),
                        $"Walking through {_camp.ZoneName}; {campDistance:N0} units remain", _camp.MonsterName, _camp.ZoneName);
                    return true;
                }

                if (_groupDirective?.IsDynamic == true)
                    AutonomousBotGroupCoordinator.MarkGrinding(bot);
                return WorkCamp(brain, bot);
            }
            catch (Exception exception)
            {
                AutonomousGoalDiagnostics.End(bot, GoalAttemptEnd.Error, exception.GetType().Name);
                Log.Error($"Autonomous goal execution failed for {bot?.Name}", exception);
                _camp = null;
                _nextPlanTick = GameLoop.GameLoopTime + 15_000;
                if (bot != null)
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                    SetStatus(bot, "Replanning after a route error", "Find a reachable level-appropriate XP camp",
                        "The previous live route failed safely; trying another camp");
                }
                return true;
            }
        }

        /// <summary>
        /// Where a bot walks to use a zone connection: the platform of a Shrouded Isles portal
        /// (it climbs onto it and crosses there), otherwise the zone point's source spot.
        /// </summary>
        private static Vector3 CrossingApproachPoint(DbZonePoint crossing) =>
            ShroudedIslesPortals.TryGetPad(crossing.Id, out Vector3 pad)
                ? pad
                : new(crossing.SourceX, crossing.SourceY, crossing.SourceZ);

        private static int CrossingApproachRadius(DbZonePoint crossing) =>
            ShroudedIslesPortals.TryGetPad(crossing.Id, out _) ? ShroudedIslesPortals.PadRadius : ZonePointArrivalRadius;

        private bool TravelAcrossRegions(GameBot bot)
        {
            CampDestination camp = _camp;
            if (camp == null)
                return true;

            // Battlegrounds have no zone connections: players arrive by teleporter and leave by the exit portal.
            if (BattlegroundBrackets.IsBattlegroundRegion(bot.CurrentRegionID) && bot.CurrentRegionID != camp.RegionId)
                return LeaveBattleground(bot);
            if (BattlegroundBrackets.IsBattlegroundRegion(camp.RegionId))
            {
                BattlegroundBrackets.Bracket bracket = BattlegroundBrackets.ForRegion(camp.RegionId);
                if (bracket == null || !BattlegroundBrackets.Allows(bracket, bot.Level))
                {
                    AbandonCamp(bot, $"{camp.ZoneName} is outside this bot's battleground level bracket");
                    return true;
                }
                return TravelToBattleground(bot, bracket, new(camp.X, camp.Y, camp.Z));
            }

            DbZonePoint crossing = FindNextCrossing(bot, camp.RegionId, camp.X, camp.Y);
            if (crossing == null)
            {
                // A capital service or trainer can stand on a walkable interior
                // component that is disconnected from both city gates in the
                // installed client mesh. If those real gates were quarantined
                // after failed physical approaches, retain the current goal and
                // recover through the same authoritative paired exit instead of
                // cycling every camp until the watchdog returns to the same city.
                if (TryRecoverBlockedCapitalEgress(bot, camp))
                    return true;
                if (TryEscapeTerminalRoutePocket(bot, new(bot.X, bot.Y, bot.Z)))
                    return true;
                AbandonCamp(bot, $"No legal region route to {camp.ZoneName}");
                return true;
            }

            if (FollowDarknessFallsHomeExit(bot, crossing)) return true;
            if (TryTownTeleportAcross(bot, crossing, camp.RegionId, camp.X, camp.Y, camp.Z)) return true;

            if (TryRepairAuditedCrossingSource(bot, crossing))
                return true;

            if (AutonomousBotGroupCoordinator.TryGetDungeonExteriorStaging(bot, _groupDirective, crossing,
                    out Vector3 exteriorSlot))
            {
                int slotDistance = Distance(bot.X, bot.Y, (int)exteriorSlot.X, (int)exteriorSlot.Y);
                if (slotDistance > AutonomousRendezvousAttendance.ArrivalRadius)
                {
                    if (!IssuePath(bot, exteriorSlot, preciseArrival: true))
                        return true;
                    SetStatus(bot, "Forming outside dungeon", GoalText(),
                        $"Taking a separate entrance formation slot; {slotDistance:N0} units remain",
                        camp.MonsterName, camp.ZoneName);
                }
                else
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                    SetStatus(bot, "Staged outside dungeon", GoalText(),
                        "Holding a clear formation slot until all eight members are ready to enter",
                        camp.MonsterName, camp.ZoneName);
                }
                return true;
            }

            int distance = Distance(bot.X, bot.Y, crossing.SourceX, crossing.SourceY);
            if (AtRegionCrossing(bot, crossing, distance))
            {
                bot.StopMovingOnPath();
                bot.StopMoving();
                if (!AutonomousBotGroupCoordinator.CanCrossDungeonEntrance(bot, _groupDirective, crossing,
                        out string dungeonWait))
                {
                    SetStatus(bot, "Staging outside dungeon", GoalText(), dungeonWait,
                        camp.MonsterName, camp.ZoneName);
                    return true;
                }
                if (!IsRegionPointAccessible(bot.Realm, crossing.TargetRegion, crossing.TargetX, crossing.TargetY) ||
                    !IsRegionEdgeAccessible(bot.Realm, crossing.SourceRegion, crossing.TargetRegion))
                {
                    AbandonCamp(bot, "The planned zone connection is not accessible to this realm");
                    return true;
                }

                // This is not a synthetic shortcut: crossing is an authoritative
                // DbZonePoint edge, the same paired source/target data consumed
                // by PlayerRegionChangeRequestHandler. Persistent bots have no
                // client packet/loading screen, so after physically reaching the
                // source endpoint they use that exact server-side paired exit.
                if (!AutonomousZonePointArrival.TryResolve(crossing, out Vector3 arrival) ||
                    !bot.MoveTo(crossing.TargetRegion, (int)arrival.X, (int)arrival.Y, (int)arrival.Z, crossing.TargetHeading))
                {
                    AbandonCamp(bot, "The server rejected the planned zone-border transfer");
                    return true;
                }
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Movement);
                _nextMoveOrderTick = GameLoop.GameLoopTime + 1200;
                SetStatus(bot, $"Crossing toward {camp.ZoneName}", GoalText(),
                    $"Used a real zone connection from region {crossing.SourceRegion} to {crossing.TargetRegion}", camp.MonsterName, camp.ZoneName);
                return true;
            }

            DbZonePoint firstRejectedCrossing = crossing;
            Vector3 rawWaypoint = CrossingApproachPoint(crossing);
            Vector3 waypoint = default;
            bool hasConnectedApproach = false;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (TryResolveConnectedApproach(bot, rawWaypoint, CrossingApproachRadius(crossing), out waypoint))
                {
                    hasConnectedApproach = true;
                    break;
                }

                QuarantineZonePoint(bot, crossing);
                DbZonePoint alternate = FindNextCrossing(bot, camp.RegionId, camp.X, camp.Y);
                if (alternate == null || alternate.Id == crossing.Id)
                    break;
                crossing = alternate;
                rawWaypoint = CrossingApproachPoint(crossing);
            }
            if (!hasConnectedApproach)
            {
                if (TryRecoverBlockedCapitalEgress(bot, camp, firstRejectedCrossing))
                    return true;
                if (TryEscapeTerminalRoutePocket(bot, new(bot.X, bot.Y, bot.Z)))
                    return true;
                AbandonCamp(bot, $"No tested zone connection has a connected approach from the current surface");
                return true;
            }
            distance = Distance(bot.X, bot.Y, crossing.SourceX, crossing.SourceY);
            // The installed Lough Derg/Valley itinerary toward this Connacht
            // portal reverses at the same seam. Prefer a real ticket only when
            // its first walk and final portal activation approach both connect.
            bool connectedPortalRide = bot.Realm == eRealm.Hibernia &&
                crossing.Id == AutonomousDarknessFallsPolicy.HomeEntranceZonePointId(eRealm.Hibernia) &&
                bot.CurrentRegionID == AutonomousDarknessFallsPolicy.HomeRegion(eRealm.Hibernia) &&
                bot.CurrentZone != bot.CurrentRegion.GetZone(crossing.SourceX, crossing.SourceY);
            if (TryBeginFasterStableRoute(bot, waypoint, "the next zone connection",
                    connectedPortalRide ? rawWaypoint : null))
                return true;
            if (_connectedPortalRideSearchFoundNoChoice &&
                AutonomousDarknessFallsPolicy.IsAuditedHiberniaExteriorLoop(
                    bot.Realm, crossing, bot.CurrentRegionID, bot.CurrentZone?.ID ?? 0,
                    new Vector3(bot.X, bot.Y, bot.Z), bot.Group?.MemberCount ?? 1))
            {
                AbandonCamp(bot, "No connected real ticket past the audited Hibernia DF exterior seam");
                return true;
            }
            if (GuardOpenWorldTravel(bot, waypoint))
                return true;
            if (!IssuePath(bot, waypoint, preciseArrival: true))
                return true;
            SetStatus(bot, $"Traveling to {camp.MonsterName}", GoalText(),
                $"Walking to the next zone connection; {distance:N0} units remain", camp.MonsterName, camp.ZoneName);
            return true;
        }

        private bool TryRouteToNeededService(GameBot bot)
        {
            eWorldServiceKind? needed = AutonomousBotEconomy.GetNeededService(bot);
            if (needed is not eWorldServiceKind.RealmExchange and not eWorldServiceKind.Vendor)
            {
                _serviceNpc = null;
                _serviceKind = null;
                return false;
            }

            if (_serviceNpc?.ObjectState is not GameObject.eObjectState.Active || _serviceKind != needed)
            {
                // A failed search is repeated at most every ten seconds for the
                // same need; the bot keeps reporting that it is waiting.
                if (_serviceKind != needed || GameServiceUtils.ShouldTick(_nextServiceSearchTick))
                {
                    _serviceNpc = FindReachableService(bot, needed.Value);
                    _serviceKind = needed;
                    _nextServiceSearchTick = _serviceNpc == null ? GameLoop.GameLoopTime + 10_000 : 0;
                }
                else
                {
                    _serviceNpc = null;
                }
            }
            if (_serviceNpc == null)
            {
                SetStatus(bot, "Waiting for a real world service", "Clear backpack or visit the Realm Exchange",
                    needed == eWorldServiceKind.RealmExchange
                        ? "No reachable Realm Exchange broker in this realm capital is currently loaded"
                        : "No reachable ordinary merchant is currently loaded");
                return true;
            }

            CampDestination destination = new($"service-{needed}-{_serviceNpc.ObjectID}", _serviceNpc.Name,
                _serviceNpc.CurrentZone?.Description ?? _serviceNpc.CurrentRegion?.Description ?? "service", _serviceNpc.CurrentRegionID,
                _serviceNpc.X, _serviceNpc.Y, _serviceNpc.Z, 1, false, false);
            if (bot.CurrentRegionID != destination.RegionId)
            {
                CampDestination previous = _camp;
                _camp = destination;
                bool moving = TravelAcrossRegions(bot);
                _camp = previous;
                return moving;
            }

            int distance = Distance(bot.X, bot.Y, destination.X, destination.Y);
            int interactionRadius = Math.Max(32, GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE);
            if (!bot.IsWithinRadius(_serviceNpc, interactionRadius))
            {
                Vector3 rawPoint = new(destination.X, destination.Y, destination.Z);
                Vector3 point = rawPoint;
                bool hasCachedApproach = !_routeInterruptedByCombat &&
                    _verifiedApproachPoint.HasValue && _verifiedApproachTarget.HasValue &&
                    _verifiedApproachZone == _serviceNpc.CurrentZone &&
                    _verifiedApproachRadius == interactionRadius &&
                    Vector3.DistanceSquared(_verifiedApproachTarget.Value, rawPoint) <= 16 * 16;
                if (hasCachedApproach)
                {
                    point = _verifiedApproachPoint.Value;
                }
                else if (AutonomousRouteHotspotRepair.TryResolveJordheimServiceApproach(
                        PathfindingProvider.Instance, _serviceNpc.CurrentZone, bot.CurrentRegionID,
                        new(bot.X, bot.Y, bot.Z), rawPoint, interactionRadius, out point))
                {
                    _verifiedApproachTarget = rawPoint;
                    _verifiedApproachPoint = point;
                    _verifiedApproachZone = _serviceNpc.CurrentZone;
                    _verifiedApproachRadius = interactionRadius;
                }
                else
                {
                    // Use the same connected interaction-point resolver already
                    // used by trainers and real zone gates. Falling back to the
                    // authored point preserves legacy behavior without a mesh.
                    // The Realm Exchange prefers a point with a clear view of the
                    // broker so bots never trade through a pillar or hallway wall.
                    TryResolveConnectedApproach(bot, rawPoint, Math.Max(16, interactionRadius - 16), out point,
                        needed == eWorldServiceKind.RealmExchange
                            ? AutonomousZonePointApproach.ClearSightOf(PathfindingProvider.Instance, _serviceNpc.CurrentZone, rawPoint)
                            : null);
                }
                if (TryBeginFasterStableRoute(bot, point, _serviceNpc.Name))
                    return true;
                IssuePath(bot, point, preciseArrival: true);
                SetStatus(bot, $"Traveling to {_serviceNpc.Name}", needed == eWorldServiceKind.RealmExchange ? "Use the Realm Exchange" : "Sell ordinary backpack loot",
                    "Routing to a real world service before resuming progression", _serviceNpc.Name, destination.ZoneName);
                return true;
            }

            if (_serviceNpc is GameMerchant supplyMerchant &&
                FrontierSupplyMerchantPolicy.Label(supplyMerchant.TradeItems?.ItemsListID) != null &&
                !ApproachSupplyMerchant(bot, supplyMerchant))
            {
                SetStatus(bot, $"Approaching {_serviceNpc.Name}", "Use a nearby merchant interaction spot",
                    "Leaving room around the supply merchant", _serviceNpc.Name, destination.ZoneName);
                return true;
            }
            bot.StopMovingOnPath();
            bot.StopMoving();
            SetStatus(bot, $"Ready to use {_serviceNpc.Name}", needed == eWorldServiceKind.RealmExchange ? "Use the Realm Exchange" : "Sell ordinary backpack loot",
                "Reached the real service; BotBrain will transact only within range", _serviceNpc.Name, destination.ZoneName);
            bot.StopFollowing();
            // Hand the exact reached broker to the transaction layer after
            // stopping the route. Do not rely on a separate nearby-NPC lookup
            // on an earlier AI turn to rediscover the service destination.
            if (bot.Brain is BotBrain serviceBrain)
            {
                // An old attack stance is not combat. The transaction guard
                // correctly refuses attacking actors, but routing never cleared
                // that stance after arrival. Never interrupt real aggro/casts.
                if (!bot.InCombat && !serviceBrain.HasAggro && !bot.IsCasting)
                    bot.StopAttack();
                serviceBrain.TryUseArrivedAutonomousService(_serviceNpc);
            }
            return true;
        }

        private static GameNPC FindReachableService(GameBot bot, eWorldServiceKind kind)
        {
            HashSet<ushort> reachable = ReachableRegions(bot.Realm, bot.CurrentRegionID);
            bool exchange = kind == eWorldServiceKind.RealmExchange;
            GameNPC best = null;
            double bestMinutes = double.MaxValue;
            // Same candidates and ordering as before (first candidate wins a tie,
            // like OrderBy + FirstOrDefault), but merchants come from each
            // region's live merchant index instead of scanning every monster,
            // pet and bot object. Brokers only ever qualify in capital cities.
            foreach (Region region in WorldMgr.GetAllRegions())
            {
                if (region == null || region.IsHousing ||
                    !AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(region.Expansion) ||
                    !reachable.Contains(region.ID))
                    continue;

                if (exchange)
                {
                    if (!region.IsCapitalCity)
                        continue;
                    foreach (GameNPC npc in region.Objects.OfType<GameNPC>())
                    {
                        if (npc is RealmExchangeBroker broker && IsServiceCandidate(bot, broker) &&
                            broker.CurrentRegion?.IsCapitalCity == true && (broker.Realm == eRealm.None || broker.Realm == bot.Realm))
                            ConsiderService(broker);
                    }
                }
                else
                {
                    foreach (GameMerchant merchant in region.Merchants)
                    {
                        if (IsServiceCandidate(bot, merchant) && (merchant.Realm == eRealm.None || merchant.Realm == bot.Realm))
                            ConsiderService(merchant);
                    }
                }
            }
            return best;

            void ConsiderService(GameNPC npc)
            {
                double minutes = EstimateTravelMinutes(bot, npc.CurrentRegionID, npc.X, npc.Y);
                if (minutes < bestMinutes)
                {
                    bestMinutes = minutes;
                    best = npc;
                }
            }
        }

        private static bool IsServiceCandidate(GameBot bot, GameNPC npc) =>
            npc.ObjectState is GameObject.eObjectState.Active && npc.CurrentZone != null && IsZoneAccessible(bot.Realm, npc.CurrentZone);

        private bool HandlePendingTraining(GameBot bot)
        {
            if (!AutonomousObjectiveAssignments.WantsBetweenTaskTraining(bot) || !bot.HasSpendableAutonomousTrainingPoints)
            {
                _trainingTrainer = null;
                return false;
            }

            if (_trainingTrainer?.ObjectState is not GameObject.eObjectState.Active)
                _trainingTrainer = null;

            if (_trainingTrainer == null)
            {
                if (!GameServiceUtils.ShouldTick(_nextTrainingDecisionTick))
                    return false;

                _nextTrainingDecisionTick = GameLoop.GameLoopTime + 60_000 + bot.ObjectID % 20_000;
                // The once-per-task boundary roll already chose this trainer visit.
                _trainingTrainer = FindReachableClassTrainer(bot);
                if (_trainingTrainer == null)
                    return false;

                // Training is a committed live-world goal, not a field action.
                AutonomousGoalDiagnostics.End(bot, GoalAttemptEnd.ServiceDetour, "Visiting a trainer");
                _camp = null;
                _campStartedTick = 0;
                _patrolDestination = null;
                _pendingStableChoice = null;
                if (bot.PersistentRecord != null)
                    bot.PersistentRecord.CurrentCampId = string.Empty;
            }

            GameTrainer trainer = _trainingTrainer;
            if (bot.CurrentRegionID != trainer.CurrentRegionID)
                return TravelToTrainerRegion(bot, trainer);

            int distance = Distance(bot.X, bot.Y, trainer.X, trainer.Y);
            if (distance > 300)
            {
                Vector3 rawDestination = new(trainer.X, trainer.Y, trainer.Z);
                if (!TryResolveConnectedApproach(bot, rawDestination, 280, out Vector3 destination))
                {
                    RejectTrainerAnchor(bot, trainer, "The trainer has no connected interaction approach from this surface");
                    return true;
                }
                if (TryBeginFasterStableRoute(bot, destination, trainer.Name))
                    return true;
                IssuePath(bot, destination, preciseArrival: true);
                SetStatus(bot, $"Traveling to {trainer.Name}", "Train newly earned specialization points",
                    $"Walking to the real class trainer; {distance:N0} units remain", trainer.Name,
                    trainer.CurrentZone?.Description ?? trainer.CurrentRegion?.Description ?? string.Empty);
                return true;
            }

            bot.StopMovingOnPath();
            bot.StopMoving();
            if (bot.TrainPendingSpecializations(trainer))
            {
                SetStatus(bot, $"Training with {trainer.Name}", "Train newly earned specialization points",
                    $"Spent earned points through level {bot.Level} in the locked {bot.BotSpec?.SpecType} plan",
                    trainer.Name, trainer.CurrentZone?.Description ?? string.Empty, true);
                _trainingTrainer = null;
                _nextPlanTick = GameLoop.GameLoopTime + 2_500;
                return true;
            }

            _trainingTrainer = null;
            _nextTrainingDecisionTick = GameLoop.GameLoopTime + 60_000;
            return false;
        }

        private bool TravelToTrainerRegion(GameBot bot, GameTrainer trainer)
        {
            DbZonePoint crossing = FindNextCrossing(bot, trainer.CurrentRegionID, trainer.X, trainer.Y);
            if (crossing == null)
            {
                _trainingTrainer = null;
                _nextTrainingDecisionTick = GameLoop.GameLoopTime + 120_000;
                SetStatus(bot, "Postponing class training", "Resume ordinary progression",
                    "No legal route to the selected trainer was available; training will be retried later");
                return false;
            }

            if (FollowDarknessFallsHomeExit(bot, crossing)) return true;
            if (TryTownTeleportAcross(bot, crossing, trainer.CurrentRegionID, trainer.X, trainer.Y, trainer.Z)) return true;

            if (TryRepairAuditedCrossingSource(bot, crossing))
                return true;

            int distance = Distance(bot.X, bot.Y, crossing.SourceX, crossing.SourceY);
            if (AtRegionCrossing(bot, crossing, distance))
            {
                bot.StopMovingOnPath();
                bot.StopMoving();
                if (!IsRegionPointAccessible(bot.Realm, crossing.TargetRegion, crossing.TargetX, crossing.TargetY) ||
                    !AutonomousZonePointArrival.TryResolve(crossing, out Vector3 arrival) ||
                    !bot.MoveTo(crossing.TargetRegion, (int)arrival.X, (int)arrival.Y, (int)arrival.Z, crossing.TargetHeading))
                {
                    _trainingTrainer = null;
                    _nextTrainingDecisionTick = GameLoop.GameLoopTime + 120_000;
                    return false;
                }
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Movement);
                return true;
            }

            Vector3 rawWaypoint = CrossingApproachPoint(crossing);
            if (!TryResolveConnectedApproach(bot, rawWaypoint, CrossingApproachRadius(crossing), out Vector3 waypoint))
            {
                RejectTrainerAnchor(bot, trainer,
                    $"Zone connection {crossing.Id} has no connected approach from the current surface");
                return true;
            }
            if (TryBeginFasterStableRoute(bot, waypoint, "the next trainer route connection"))
                return true;
            IssuePath(bot, waypoint, preciseArrival: true);
            SetStatus(bot, $"Traveling to {trainer.Name}", "Train newly earned specialization points",
                $"Walking to the next zone connection; {distance:N0} units remain", trainer.Name,
                trainer.CurrentZone?.Description ?? string.Empty);
            return true;
        }

        private GameTrainer FindReachableClassTrainer(GameBot bot)
        {
            foreach (GameTrainer expired in _failedTrainerAnchors
                         .Where(pair => pair.Value <= GameLoop.GameLoopTime).Select(pair => pair.Key).ToArray())
                _failedTrainerAnchors.Remove(expired);
            HashSet<ushort> reachable = ReachableRegions(bot.Realm, bot.CurrentRegionID);
            eCharacterClass characterClass = (eCharacterClass)bot.CharacterClass.ID;
            GameTrainer[] candidates = WorldMgr.GetAllRegions()
                .Where(region => region != null &&
                                 AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(region.Expansion) &&
                                 reachable.Contains(region.ID))
                .SelectMany(region => region.Objects.OfType<GameTrainer>())
                .Where(trainer => trainer.ObjectState is GameObject.eObjectState.Active &&
                                  (trainer.Realm is eRealm.None || trainer.Realm == bot.Realm))
                .Where(trainer => !_failedTrainerAnchors.ContainsKey(trainer))
                .Where(trainer => trainer.TrainedClass == characterClass || trainer.TrainedClass == eCharacterClass.Unknown)
                .Where(trainer => trainer.CurrentZone == null || IsZoneAccessible(bot.Realm, trainer.CurrentZone))
                .OrderBy(trainer => trainer.TrainedClass == characterClass ? 0 : 1)
                .ThenBy(trainer => EstimateTravelMinutes(bot, trainer.CurrentRegionID, trainer.X, trainer.Y))
                .ToArray();
            foreach (GameTrainer trainer in candidates)
            {
                if (trainer.CurrentRegionID != bot.CurrentRegionID ||
                    TryResolveConnectedApproach(bot, new(trainer.X, trainer.Y, trainer.Z), 280, out _))
                    return trainer;
                _failedTrainerAnchors[trainer] = GameLoop.GameLoopTime + 30 * 60_000;
            }
            return null;
        }

        private bool LeaveDungeonForGroupMatchmaking(GameBot bot)
        {
            AutonomousStuckWatchdog.CapitalLocation capital = AutonomousStuckWatchdog.SafeCapitalFor(bot.Realm);
            if (capital.RegionId == 0)
                return true;
            CampDestination previous = _camp;
            _camp = new("group-matchmaking-egress", "group rendezvous", capital.Name,
                capital.RegionId, capital.X, capital.Y, capital.Z, 1, false, false);
            bool handled = TravelAcrossRegions(bot);
            _camp = previous;
            if (bot.CurrentZone?.IsDungeon == true)
                SetStatus(bot, "Leaving dungeon for group meetup", "Join a same-realm group-PvE party",
                    "Using the real dungeon exit before waiting for matchmaking; no watchdog relocation is needed");
            return handled;
        }

        private bool HandleGroupRendezvous(GameBot bot, AutonomousBotGroupCoordinator.Directive directive)
        {
            GameBot leader = directive.Leader;
            bool leaderStaging = directive.Phase == "Leader staging";
            if ((leaderStaging || directive.Phase == "Regrouping" && !directive.LeaderReadyForAssembly) && bot != leader)
            {
                bot.WakeRecoveryRest();
                bot.StopMovingOnPath();
                bot.StopMoving();
                SetStatus(bot, leaderStaging ? "Waiting for group leader" : "Waiting for regroup leader",
                    directive.SharedGoal,
                    $"Leader {leader?.Name ?? "unknown"} is establishing the formation point in {directive.RendezvousName}");
                return true;
            }
            if (AutonomousBotGroupCoordinator.IsRendezvousRouteHeld(bot, directive.GroupId))
            {
                bot.WakeRecoveryRest();
                bot.StopMovingOnPath();
                bot.StopMoving();
                bool raidMuster = AutonomousRealmRaid.GetView(bot.Group)?.Muster == true;
                SetStatus(bot, raidMuster ? "Expedition hub route failed" : "Waiting for group no-show timeout", directive.SharedGoal,
                    raidMuster ? "The failed hub approach was logged; the expedition's staging deadline remains authoritative" :
                    "The assigned route was proven unreachable; holding safely until the existing 15-minute meetup deadline");
                return true;
            }
            if (bot.CurrentRegionID != directive.RendezvousRegion)
            {
                bot.WakeRecoveryRest();
                return TravelToGroupRegion(bot, directive, directive.RendezvousRegion,
                    (int)directive.Rendezvous.X, (int)directive.Rendezvous.Y,
                    leaderStaging ? directive.RendezvousName : "group rendezvous");
            }

            Vector3 formation = AutonomousBotGroupCoordinator.RendezvousFormationPoint(bot, directive.GroupId,
                directive.Rendezvous,
                bot.CurrentRegion?.IsDungeon == true || bot.CurrentZone?.IsDungeon == true);
            Vector3 position = new(bot.X, bot.Y, bot.Z);
            int distance = (int)Math.Ceiling(Vector3.Distance(position, formation));
            bool tight = bot.CurrentRegion?.IsDungeon == true || bot.CurrentZone?.IsDungeon == true;
            // Reserve movement completion margin inside the attendance circle.
            int arrivalRadius = AutonomousRendezvousAttendance.ApproachRadius(tight);
            if (!AutonomousRendezvousAttendance.IsAtSlot(position, formation, tight))
            {
                bot.WakeRecoveryRest();
                if (TryBeginFasterStableRoute(bot, directive.Rendezvous, "the group rendezvous"))
                    return true;

                // Group formation used to send the raw slot straight to the
                // mover.  The formation was validated when the party formed,
                // but a member can change zones, be released, or move between
                // pulses before its first route order.  In that case Detour
                // correctly rejects the stale start/slot pair, while the
                // controller kept reporting a live travel status and retried
                // forever.  Resolve the *current* actor-to-slot corridor here
                // and reselect the rendezvous if the mesh has genuinely lost
                // connectivity; no teleport is used as a route fix.
                bot.StopFollowing();
                // Resolve well inside the slot's attendance boundary. The
                // mover's own completion tolerance must fit inside it too.
                if (!TryResolveConnectedApproach(bot, formation, arrivalRadius, out Vector3 approach))
                {
                    AutonomousBotGroupCoordinator.ReportUnreachableRendezvous(
                        bot, directive.GroupId,
                        "The current member-to-formation navmesh corridor is disconnected");
                    SetStatus(bot, "Replanning group rendezvous", directive.SharedGoal,
                        "The selected formation slot is not connected from this member's current surface");
                    return true;
                }

                if (!IssuePath(bot, approach, preciseArrival: true))
                {
                    AutonomousBotGroupCoordinator.ReportUnreachableRendezvous(
                        bot, directive.GroupId,
                        "The current member-to-formation path order was rejected by the navmesh");
                    SetStatus(bot, "Replanning group rendezvous", directive.SharedGoal,
                        "The selected formation route was rejected; choosing a connected rendezvous");
                    return true;
                }
                SetStatus(bot, leaderStaging ? $"Leader traveling to {directive.RendezvousName}" : "Meeting up with group",
                    directive.SharedGoal,
                    $"Traveling to the safe formation point; {distance:N0} units remain", string.Empty,
                    directive.RendezvousName);
                return true;
            }

            bot.StopMovingOnPath();
            bot.StopMoving();
            SetStatus(bot, leaderStaging ? $"Leader ready in {directive.RendezvousName}" : "Formed up at rendezvous",
                directive.SharedGoal, leaderStaging
                    ? "The leader arrived; inviting the remaining members to form around this position"
                    : $"Waiting in formation for {directive.BotMemberCount:N0} bot members");
            return true;
        }

        private bool HandleGroupCombatAndRecovery(BotBrain brain, GameBot bot,
            AutonomousBotGroupCoordinator.Directive directive)
        {
            if (directive.GroupCombatActive)
            {
                // A member fighting beyond assist range used to leave everyone else
                // standing here until its fight ended; walk over and help instead.
                GameBot fightingMember = !brain.HasAggro && !bot.IsAttacking
                    ? AutonomousGroupCombat.MemberToHelp(bot, GameLoop.GameLoopTime)
                    : null;
                if (fightingMember == null)
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                }
                bot.WakeRecoveryRest();
                if (brain.TryAssistAutonomousPveCombat())
                    return false;
                if (brain.CheckHeals()) return true;
                if (fightingMember != null)
                {
                    IssuePath(bot, new(fightingMember.X, fightingMember.Y, fightingMember.Z), retargetWhileMoving: true);
                    SetStatus(bot, $"Moving to help {fightingMember.Name}", directive.SharedGoal,
                        $"{fightingMember.Name} is fighting beyond assist range; the party goes to help");
                    return true;
                }
                SetStatus(bot, "Defending traveling group", directive.SharedGoal,
                    "The formation route is paused until the threat attacking the party is cleared");
                return true;
            }

            bot.StopMovingOnPath();
            bot.StopMoving();
            if (brain.CheckHeals()) return true;
            bool full = AutonomousRestPolicy.IsFullyRecovered(bot.HealthPercent, bot.ManaPercent,
                bot.EndurancePercent, bot.MaxMana > 0);
            if (full)
                bot.WakeRecoveryRest();
            else if (!BotRestRecovery.BlocksRest(bot))
                bot.BeginRecoveryRest();
            SetStatus(bot, "Recovering before travel", directive.SharedGoal,
                $"Route paused until every member is full: HP {bot.HealthPercent}% • power {bot.ManaPercent}% • endurance {bot.EndurancePercent}%");
            return true;
        }

        private bool HandleRecoveryRegroup(BotBrain brain, GameBot bot)
        {
            if (!_regroupRouteCleared)
            {
                _regroupRouteCleared = true;
                AutonomousGoalDiagnostics.End(bot, GoalAttemptEnd.GroupChanged, "Party casualty; regroup before new pulls");
                ReleaseOwnedSiegeRams(bot);
                AutonomousPetSupport.ReleaseFieldTurrets(bot);
                _camp = null;
                _rvrDestination = null;
                _rvrApproachDestination = null;
                _patrolDestination = null;
                _pendingStableChoice = null;
                _issuedRouteDestination = null;
                _routeRecoveryWaypoint = null;
                _nextPlanTick = 0;
                bot.StopMovingOnPath();
                bot.StopMoving();
                bot.TargetObject = null;
                if (bot.PersistentRecord != null) bot.PersistentRecord.CurrentCampId = string.Empty;
            }
            // Normal resurrection/healing and reactive combat remain enabled.
            // No teleport, resource refill, or forced stable dismount occurs.
            if (brain.CheckHeals()) return true;
            HandleGroupRendezvous(bot, _groupDirective);
            if (bot.CurrentRegionID == _groupDirective.RendezvousRegion &&
                AutonomousRendezvousAttendance.IsAtSlot(new(bot.X, bot.Y, bot.Z),
                    AutonomousBotGroupCoordinator.RendezvousFormationPoint(bot, _groupDirective.GroupId,
                        _groupDirective.Rendezvous,
                        bot.CurrentRegion?.IsDungeon == true || bot.CurrentZone?.IsDungeon == true),
                    bot.CurrentRegion?.IsDungeon == true || bot.CurrentZone?.IsDungeon == true))
            {
                bool resourcesReady = AutonomousGroupRecoveryState.ResourcesReady(bot.HealthPercent, bot.ManaPercent,
                    bot.EndurancePercent, bot.MaxMana > 0);
                if (resourcesReady)
                    bot.WakeRecoveryRest();
                else if (!BotRestRecovery.BlocksRest(bot))
                    bot.BeginRecoveryRest();
                SetStatus(bot, "Formed up at rendezvous", _groupDirective.SharedGoal,
                    "Regrouping after casualties; waiting for every member to return and recover; task timer continues");
            }
            return true;
        }

        private bool HoldBeforeNewGroupPull(BotBrain brain, GameBot bot)
        {
            if (AutonomousBotGroupCoordinator.CanInitiateNewPull(bot)) return false;
            _groupDirective = AutonomousBotGroupCoordinator.Pulse(bot);
            if (_groupDirective?.IsDynamic != true) return true;
            if (_groupDirective.Phase == "Regrouping")
                return HandleRecoveryRegroup(brain, bot);
            if (AutonomousBotGroupCoordinator.IsAssemblyPhase(_groupDirective.Phase))
                return HandleGroupRendezvous(bot, _groupDirective);
            if (_groupDirective.Leader != bot)
                return FollowDynamicGroupLeader(bot, _groupDirective);
            bot.StopMovingOnPath();
            bot.StopMoving();
            SetStatus(bot, "Holding group formation", _groupDirective.SharedGoal,
                "Waiting for the complete party before starting another fight; task timer continues");
            return true;
        }

        private bool TravelToGroupRegion(
            GameBot bot,
            AutonomousBotGroupCoordinator.Directive directive,
            ushort targetRegion,
            int targetX,
            int targetY,
            string destinationLabel)
        {
            if (AutonomousRealmRaid.GetView(bot.Group) != null)
            {
                // Expedition muster must use the same tested alternate-exit,
                // floor repair and paired-capital-egress path as normal travel.
                // The old abbreviated meetup path permanently held a bot on
                // the first rejected exit, never trying the other city gate.
                if (GameLoop.GameLoopTime < _expeditionRouteRetry) return true;
                CampDestination previous = _camp;
                _camp = new("realm-expedition-travel", destinationLabel, destinationLabel,
                    targetRegion, targetX, targetY, 0, 50, false, false);
                try { return TravelAcrossRegions(bot); }
                finally { _camp = previous; }
            }
            DbZonePoint crossing = FindNextCrossing(bot, targetRegion, targetX, targetY);
            if (crossing == null)
            {
                SetStatus(bot, "Replanning group rendezvous", directive.SharedGoal,
                    $"No legal region connection currently reaches the party's {destinationLabel}");
                return true;
            }

            if (FollowDarknessFallsHomeExit(bot, crossing)) return true;
            if (TryTownTeleportAcross(bot, crossing, targetRegion, targetX, targetY, bot.Z)) return true;

            if (TryRepairAuditedCrossingSource(bot, crossing))
                return true;

            int distance = Distance(bot.X, bot.Y, crossing.SourceX, crossing.SourceY);
            if (AtRegionCrossing(bot, crossing, distance))
            {
                bot.StopMovingOnPath();
                bot.StopMoving();
                if (!AutonomousBotGroupCoordinator.CanCrossDungeonEntrance(bot, directive, crossing,
                        out string dungeonWait))
                {
                    SetStatus(bot, "Staging outside dungeon", directive.SharedGoal, dungeonWait,
                        directive.Camp?.MonsterName ?? string.Empty, directive.Camp?.ZoneName ?? string.Empty);
                    return true;
                }
                if (!IsRegionPointAccessible(bot.Realm, crossing.TargetRegion, crossing.TargetX, crossing.TargetY) ||
                    !IsRegionEdgeAccessible(bot.Realm, crossing.SourceRegion, crossing.TargetRegion) ||
                    !AutonomousZonePointArrival.TryResolve(crossing, out Vector3 arrival) ||
                    !bot.MoveTo(crossing.TargetRegion, (int)arrival.X, (int)arrival.Y, (int)arrival.Z, crossing.TargetHeading))
                {
                    SetStatus(bot, "Replanning group rendezvous", directive.SharedGoal,
                        "The authoritative zone connection was unavailable; the route will be recalculated");
                    return true;
                }
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Movement);
                _nextMoveOrderTick = GameLoop.GameLoopTime + 1200;
                return true;
            }

            Vector3 rawWaypoint = CrossingApproachPoint(crossing);
            if (!TryResolveConnectedApproach(bot, rawWaypoint, CrossingApproachRadius(crossing), out Vector3 waypoint))
            {
                AutonomousBotGroupCoordinator.ReportUnreachableRendezvous(bot, directive.GroupId,
                    $"Zone connection {crossing.Id} has no connected approach from the current surface");
                ResetRouteOrderState();
                return true;
            }
            if (TryBeginFasterStableRoute(bot, waypoint, $"the {destinationLabel} connection"))
                return true;
            IssuePath(bot, waypoint, preciseArrival: true);
            SetStatus(bot, "Meeting up with group", directive.SharedGoal,
                $"Traveling independently to the next legal {destinationLabel} connection; {distance:N0} units remain");
            return true;
        }

        private bool FollowDynamicGroupLeader(GameBot bot, AutonomousBotGroupCoordinator.Directive directive)
        {
            GameBot leader = directive.Leader;
            if (leader == null || !leader.IsAlive)
                return true;

            if (directive.ObjectiveKind == eAutonomousObjectiveKind.GroupPve && directive.Camp != null &&
                bot.CurrentRegionID != leader.CurrentRegionID)
            {
                DbZonePoint leaderCrossing = FindNextCrossing(leader, directive.Camp.RegionId,
                    directive.Camp.X, directive.Camp.Y);
                if (leaderCrossing?.TargetRegion == bot.CurrentRegionID)
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                    SetStatus(bot, "Waiting for group leader after crossing", directive.SharedGoal,
                        "Holding the arrival side of the real portal until the leader joins the party");
                    return true;
                }
            }

            if (directive.ObjectiveKind == eAutonomousObjectiveKind.RvR &&
                leader.TempProperties.GetProperty<AutonomousFrontierTransport.Request>(AutonomousFrontierTransport.RequestKey) is { } passage &&
                leader.CurrentRegion == bot.CurrentRegion && passage.Passage.Region != bot.CurrentRegionID &&
                TryFrontierTransport(bot,new("frontier-passage",passage.Passage.Location.Name,"frontier",
                    passage.Passage.Region,passage.Passage.Location.X,passage.Passage.Location.Y,passage.Passage.Location.Z,50,false,true)))
                return true;

            if (directive.Camp != null && bot.CurrentRegionID != directive.Camp.RegionId)
            {
                DbZonePoint crossing = FindNextCrossing(bot, directive.Camp.RegionId,
                    directive.Camp.X, directive.Camp.Y);
                if (bot.CurrentRegionID != leader.CurrentRegionID || crossing == null ||
                    Distance(bot.X, bot.Y, crossing.SourceX, crossing.SourceY) <=
                        AutonomousDungeonPolicy.DungeonEntranceStagingRadius)
                    return TravelAcrossRegions(bot);
                // Away from the actual crossing, follow the leader's corridor
                // below rather than independently racing toward the dungeon.
            }
            // Warbands have no PvE camp. Follow the leader through a legal
            // region edge instead of issuing its coordinates in the wrong region.
            if (directive.Camp == null && bot.CurrentRegionID != leader.CurrentRegionID)
                return TravelToGroupRegion(bot, directive, leader.CurrentRegionID,
                    leader.X, leader.Y, "warband leader");

            // Every follower probes its actual dungeon corridor. The designated
            // tank (or leader when no tank exists) owns a voluntary blocker
            // pull; all others hold formation until contact wakes group assist.
            if (bot.CurrentZone?.IsDungeon == true &&
                GuardDungeonTravel(bot, new Vector3(leader.X, leader.Y, leader.Z)))
                return true;

            // Formed PvE parties keep walking together. Individual horse rides
            // are still available during the initial town rendezvous.
            Vector3 strategicDestination = directive.Camp == null
                ? new Vector3(leader.X, leader.Y, leader.Z)
                : new Vector3(directive.Camp.X, directive.Camp.Y, directive.Camp.Z);
            if (directive.ObjectiveKind != eAutonomousObjectiveKind.GroupPve &&
                TryBeginFasterStableRoute(bot, strategicDestination,
                    directive.Camp?.ZoneName ?? "the group leader"))
                return true;

            bool tight = leader.CurrentRegion?.IsDungeon == true || leader.CurrentZone?.IsDungeon == true;
            // Aim around where the leader is about to be, not where it was
            // (smooth group travel from the stefanrows/OfflineDAoC fork).
            Vector3 formation = AutonomousBotGroupCoordinator.FormationPoint(bot,
                leader.IsMoving && !tight ? AutonomousGroupMotion.PredictLeader(leader) : new(leader.X, leader.Y, leader.Z), tight);
            // Close the gap on the move so the party travels as one unit instead of the
            // leader stopping for formation (the follower runs up to 25% faster while behind).
            if (bot.CurrentRegionID == leader.CurrentRegionID)
                AutonomousGroupPace.CatchUp(bot, Vector3.Distance(new(bot.X, bot.Y, bot.Z), formation), GameLoop.GameLoopTime);
            if (bot.CurrentRegionID != leader.CurrentRegionID)
            {
                // Never teleport a persistent group member to catch its leader.
                // If this member is already in the shared camp region, it holds
                // or advances there while the others finish their own routes.
                Vector3 independentFormation = directive.Camp == null
                    ? strategicDestination
                    : AutonomousBotGroupCoordinator.FormationPoint(bot, strategicDestination, tight);
                if (Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), independentFormation) > 60 * 60)
                    IssuePath(bot, independentFormation);
                else
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                }
            }
            else if (leader.IsMoving && !tight &&
                     Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), formation) < 2_500 * 2_500)
            {
                // Walk with the leader: steer smoothly, match its pace, never stop mid-march.
                AutonomousGroupMotion.FollowMovingLeader(bot, leader, formation);
            }
            else if (Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), formation) > 95 * 95)
            {
                IssuePath(bot, formation, retargetWhileMoving: true);
            }
            else
            {
                bot.StopMovingOnPath();
                bot.StopMoving();
            }
            SetStatus(bot, "Traveling in group formation", directive.SharedGoal,
                $"Keeping formation with {leader.Name}; the party clears dungeon threats before advancing",
                directive.Camp?.MonsterName ?? string.Empty, directive.Camp?.ZoneName ?? string.Empty);
            return true;
        }


        private static CampDestination FromSharedCamp(AutonomousBotGroupCoordinator.SharedCamp camp) =>
            new(camp.Id, camp.MonsterName, camp.ZoneName, camp.RegionId, camp.X, camp.Y, camp.Z, 1, camp.IsDungeon, camp.IsFrontier, TargetLevel: camp.TargetLevel);

        private static AutonomousBotGroupCoordinator.SharedCamp ToSharedCamp(CampDestination camp) =>
            new(camp.Id, camp.MonsterName, camp.ZoneName, camp.RegionId, camp.X, camp.Y, camp.Z, camp.IsDungeon, camp.IsFrontier, camp.TargetLevel);

        private bool ExecuteRvr(BotBrain brain, GameBot bot)
        {
            bool dynamicWarband = _groupDirective?.IsDynamic == true &&
                                  _groupDirective.ObjectiveKind == eAutonomousObjectiveKind.RvR;
            string forceId = dynamicWarband ? _groupDirective.GroupId : $"rvr-{bot.DatabaseID}";
            bot.TempProperties.SetProperty("RvrEventForce", forceId);
            bool battleActive = AutonomousRvrEventLayer.IsBattleForce(forceId, GameLoop.GameLoopTime);
            var committedPlan = AutonomousRvrEventLayer.KeepPlan(forceId, bot.Realm, GameLoop.GameLoopTime);
            if (committedPlan != null)
            {
                if (bot.TempProperties.GetProperty<AutonomousFrontierTransport.Request>(AutonomousFrontierTransport.RequestKey) is { } oldPassage &&
                    (oldPassage.Passage.Region!=committedPlan.RegionId || bot.CurrentRegionID==committedPlan.RegionId))
                    bot.TempProperties.RemoveProperty(AutonomousFrontierTransport.RequestKey);
                if (_rvrDestination?.Id != committedPlan.TargetId)
                    _rvrApproachDestination = null;
                _rvrDestination = new(committedPlan.TargetId, committedPlan.Name, committedPlan.Name,
                    committedPlan.RegionId, committedPlan.X, committedPlan.Y, committedPlan.Z, 1, false, true);
                _rvrSharedEvent = true;
                _rvrIntent = committedPlan.Intent;
                bot.TempProperties.SetProperty("RvrWarbandIntent", (int)_rvrIntent);
            }
            var rally = AutonomousRvrEventLayer.GetRallyOrder(forceId, bot.Realm, GameLoop.GameLoopTime);
            // A marching army's siege operators set up rams, catapults and trebuchets as soon as they reach
            // the keep instead of waiting at rally posts for the battle to be declared. Siege jobs ran only
            // after the battle started, and no bot had ever deployed a siege weapon (logs since 2026-10-02).
            if (rally != null && !rally.HomeMuster && bot.Realm == rally.Attacker && TryRunSiegeJob(bot)) return true;
            if (rally != null) return HandleSiegeRally(bot, forceId, rally);
            if (committedPlan == null && dynamicWarband && _groupDirective.Leader != bot && _groupDirective.Leader != null)
                _rvrIntent = (AutonomousRvrEventLayer.Intent)_groupDirective.Leader.TempProperties
                    .GetProperty<int>("RvrWarbandIntent", (int)AutonomousRvrEventLayer.Intent.Roam);
            if (!battleActive && !(dynamicWarband && _groupDirective.GroupCombatActive) && HoldBeforeNewGroupPull(brain, bot))
                return true;
            if (!battleActive && !dynamicWarband && !GameRelic.IsPlayerCarryingRelic(bot) && !StageSoloRvrAtBorderKeep(bot))
                return true;

            // Native keep/relic events remove resolved objectives immediately.
            // Release this controller's stale destination on its next AI pulse,
            // rather than letting it march toward a captured or timed-out site.
            if (_rvrSharedEvent && _rvrDestination != null &&
                !AutonomousRvrEventLayer.IsTargetActive(_rvrDestination.Id, GameLoop.GameLoopTime))
            {
                _rvrDestination = null;
                _rvrApproachDestination = null;
                _rvrSharedEvent = false;
                _nextRvrPlanReview = 0;
            }

            // RvR has no PvE puller. Every cohesive member can acquire a local
            // target, with pressure spread over the closest visible enemies.
            if (TryRunSiegeJob(bot)) return true;
            GameLiving enemy = FindRvrTarget(bot);
            if (enemy != null)
            {
                bot.StopMovingOnPath();
                bot.StopMoving();
                bot.TargetObject = enemy;
                AutonomousBotGroupCoordinator.MarkCombatObserved(bot.Group);
                brain.AddToAggroList(enemy, Math.Max(100, enemy.EffectiveLevel * 12));
                brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                eAutonomousRvrPhase phase = AutonomousRvrObjectiveState.ForTarget(enemy);
                SetRvrStatus(bot, phase.ToString(), "Live RvR target in awareness range", enemy.Name);
                return false;
            }

            // The leader owns navigation and the single siege operator role.
            // Followers fight independently above, otherwise they keep the
            // warband together around that leader.
            if (!battleActive && dynamicWarband && _groupDirective.Leader != bot && !GameRelic.IsPlayerCarryingRelic(bot))
            {
                int defendingId = _groupDirective.Leader?.TempProperties.GetProperty<int>("RvrDefendingKeep", -1) ?? -1;
                AbstractGameKeep wallKeep = defendingId >= 0
                    ? GameServer.KeepManager.GetKeepsOfRegion(bot.CurrentRegionID).FirstOrDefault(keep =>
                        keep.KeepID == defendingId && keep.Realm == bot.Realm &&
                        bot.GetDistanceTo(new Point3D(keep.X, keep.Y, keep.Z)) <= 2500) : null;
                if (wallKeep != null)
                {
                    return HoldDefensiveKeepPost(bot, wallKeep);
                }
                return FollowDynamicGroupLeader(bot, _groupDirective);
            }

            GameRelic carriedRelic = bot.TempProperties.GetProperty<GameRelic>(GameRelic.PLAYER_CARRY_RELIC_WEAK);
            if (carriedRelic?.CurrentCarrier == bot)
            {
                GameRelicPad home = RelicMgr.GetPadsSnapshot().FirstOrDefault(pad =>
                    pad.Realm == bot.Realm && pad.PadType == carriedRelic.RelicType && pad.MountedRelics.Count < 3);
                if (home == null)
                {
                    SetRvrStatus(bot, "Relic return blocked", "Protect the carried relic", "No matching home shrine is available");
                    return true;
                }
                if (bot.IsWithinRadius(home, 250) && carriedRelic.RelicPadTakesOver(home, false))
                {
                    _rvrDestination = null;
                    SetRvrStatus(bot, "Relic captured", "Relic delivered to the home shrine", carriedRelic.Name);
                    return true;
                }
                SetRvrStatus(bot, "Returning relic", "Escort the real relic to its home shrine", home.Name);
                return TravelRvrObjective(bot, new("relic-home", home.Name, home.CurrentZone?.Description ?? "home shrine",
                    home.CurrentRegionID, home.X, home.Y, home.Z, 50, false, true));
            }

            if (_rvrDestination?.Id.StartsWith("rvr-relic-carrier-", StringComparison.Ordinal) == true)
            {
                GameRelic escorted = RelicMgr.GetRelics().FirstOrDefault(relic =>
                    RelicObjectiveId(relic) == _rvrDestination.Id);
                GameLiving carrier = escorted?.CurrentCarrier;
                if (carrier?.IsAlive == true)
                {
                    _rvrDestination = _rvrDestination with { RegionId = carrier.CurrentRegionID, X = carrier.X, Y = carrier.Y, Z = carrier.Z };
                    SetRvrStatus(bot, carrier.Realm == bot.Realm ? "Escorting relic" : "Intercepting relic", escorted.Name, carrier.Name);
                    if (carrier.Realm == bot.Realm && bot.IsWithinRadius(carrier, 450))
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                        return true;
                    }
                    return TravelRvrObjective(bot, _rvrDestination);
                }
                if (escorted is { IsMounted: false, ObjectState: GameObject.eObjectState.Active })
                {
                    _rvrDestination = _rvrDestination with { RegionId = escorted.CurrentRegionID, X = escorted.X, Y = escorted.Y, Z = escorted.Z };
                    if (bot.IsWithinRadius(escorted, WorldMgr.INTERACT_DISTANCE))
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                        escorted.TryPickup(bot);
                    }
                    else TravelRvrObjective(bot, _rvrDestination);
                    SetRvrStatus(bot, "Recovering dropped relic", escorted.Name, "Recovering the real dropped item for the realm");
                    return true;
                }
                _rvrDestination = null;
            }

            if (_rvrDestination == null || _rvrDestination.RegionId == 0 ||
                (!_rvrSharedEvent && GameLoop.GameLoopTime >= _nextRvrPlanReview && !BotSiegeRuntime.Assigned(bot)) ||
                (_rvrIntent == AutonomousRvrEventLayer.Intent.Roam && bot.CurrentRegionID == _rvrDestination.RegionId &&
                 Distance(bot.X, bot.Y, _rvrDestination.X, _rvrDestination.Y) <= CampArrivalRadius))
            {
                _rvrDestination = ChooseRvrDestination(bot);
                _nextRvrPlanReview = GameLoop.GameLoopTime + 45_000 + bot.ObjectID % 15_000;
                _campStartedTick = GameLoop.GameLoopTime;
                _patrolDestination = null;
                _rvrApproachDestination = null;
                if (_rvrDestination == null)
                {
                    SetRvrStatus(bot, "Awaiting a frontier objective", "Roam active frontier keeps, relic routes, and enemy forces",
                        "No reachable live enemy force or frontier keep is currently available");
                    return true;
                }
            }

            rally = AutonomousRvrEventLayer.GetRallyOrder(forceId, bot.Realm, GameLoop.GameLoopTime);
            if (rally != null) return HandleSiegeRally(bot, forceId, rally);

            // The ram owns its normal decay after release; the controller never
            // deletes it or mutates the door when the native objective changes.
            ReleaseOwnedSiegeRams(bot);

            GameRelic relic = FindInteractableRelic(bot);
            if (relic != null)
            {
                if (!bot.IsWithinRadius(relic, WorldMgr.INTERACT_DISTANCE))
                    IssuePath(bot, new(relic.X, relic.Y, relic.Z));
                else
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                    bool pickedUp = relic.TryPickup(bot);
                    SetRvrStatus(bot, pickedUp ? "Returning relic" : "Rallying at relic",
                        relic.Name, pickedUp ? "Real relic secured; escorts follow the carrier home" :
                        "Waiting for the required raiders and a free inventory slot");
                }
                return true;
            }

            if (bot.CurrentRegionID != _rvrDestination.RegionId)
            {
                return TravelRvrObjective(bot,_rvrDestination);
            }

            Vector3 destination = new(_rvrDestination.X, _rvrDestination.Y, _rvrDestination.Z);
            Zone keepZone = bot.CurrentRegion?.GetZone(_rvrDestination.X, _rvrDestination.Y);
            AbstractGameKeep defendedKeep = _rvrIntent == AutonomousRvrEventLayer.Intent.DefendEvent
                ? GameServer.KeepManager.GetKeepsOfRegion(bot.CurrentRegionID).FirstOrDefault(keep =>
                    $"rvr-keep-{keep.KeepID}" == _rvrDestination.Id && keep.Realm == bot.Realm) : null;
            if (defendedKeep != null)
            {
                if (Vector2.DistanceSquared(new(bot.X,bot.Y),new(defendedKeep.X,defendedKeep.Y))>3500*3500)
                    return TravelToDefensivePost(bot,defendedKeep,new(defendedKeep.X,defendedKeep.Y,defendedKeep.Z));
                return HoldDefensiveKeepPost(bot, defendedKeep);
            }
            if (IsKeepOrKeepPatrolDestination(_rvrDestination))
            {
                if (FollowKeepTravel(bot, _rvrDestination)) return true;
                destination = _rvrApproachDestination.Value;
            }
            if (Vector2.Distance(new(bot.X, bot.Y), new(destination.X, destination.Y)) > 650)
            {
                // The first outbound leg leaves the safe border keep on foot,
                // keeping the assembled force together through the frontier
                // threshold. Normal faster travel remains available once the
                // force is actually roaming inside the frontier.
                if (IsInFrontier(bot) && TryBeginFasterStableRoute(bot, destination, _rvrDestination.ZoneName))
                    return true;
                IssueVariedRvrPath(bot, destination);
                SetRvrStatus(bot, $"Roaming toward {_rvrDestination.MonsterName}",
                    "Roam active frontier keeps, relic routes, and enemy forces", "Using a collision-safe frontier route", _rvrDestination.MonsterName);
                return true;
            }

            if (_rvrSharedEvent && _rvrDestination.Id.StartsWith("rvr-keep-",StringComparison.Ordinal))
            {
                // A siege is not a random patrol around the gate: a random
                // nearby polygon can be INSIDE the closed enemy keep. Retain
                // the proved approach until real door geometry changes.
                if(Vector3.DistanceSquared(new(bot.X,bot.Y,bot.Z),destination)>120*120)
                    IssuePath(bot,destination);
                else { bot.StopMovingOnPath();bot.StopMoving(); }
                SetRvrStatus(bot,"At keep assault approach",_rvrDestination.MonsterName,
                    "Engaging reachable enemies; advancing through gates only after a real breach");
                return true;
            }
            PatrolRvr(bot);
            if (GameLoop.GameLoopTime - _campStartedTick > 90_000)
            {
                _rvrDestination = null;
                _rvrApproachDestination = null;
                SetRvrStatus(bot, "Refreshing frontier patrol", "Roam active frontier keeps, relic routes, and enemy forces",
                    "No opposing force arrived at this patrol point; choosing another live objective");
                return true;
            }
            SetRvrStatus(bot, $"Searching near {_rvrDestination.MonsterName}",
                "Roam active frontier keeps, relic routes, and enemy forces", "Scanning for opposing-realm RvR forces", _rvrDestination.MonsterName);
            return true;
        }

        private GameLiving FindRvrTarget(GameBot bot)
        {
            // Player/bot/pet threats are acquired by the bounded early frontier
            // scan, before staging/recovery can consume a turn. Do not repeat
            // that scan here; this late path handles siege objectives only.

            // Doors/lords use the normal GameLiving combat pipeline. There is no
            // direct ownership mutation: if normal server rules reject an NPC
            // attacker (for example, no permitted siege damage), the warband
            // safely continues its live frontier patrol.
            if (_rvrIntent is not (AutonomousRvrEventLayer.Intent.AssaultKeep or AutonomousRvrEventLayer.Intent.AssaultRelicKeep))
                return null; // Roamers still retaliate through normal aggro; they do not initiate an unregistered siege.
            // Any closed door of the target keep keeps its lord off-limits, however far this bot stands from it (the
            // nearby-door check let a bot whose search circle reached the lord but not the inner door attack him).
            bool closedDoor = GameServer.KeepManager.GetKeepsOfRegion(bot.CurrentRegionID)
                .Where(keep => $"rvr-keep-{keep.KeepID}" == _rvrDestination?.Id)
                .SelectMany(keep => keep.Doors.Values)
                .Any(door => door.IsAlive && door.IsAttackableDoor && door.State == eDoorState.Closed);
            var keepNavigation=AutonomousKeepApproachNavigation.ForRealm(PathfindingProvider.Instance,bot.CurrentRegion,bot.Realm);
            // Staged assault: outer/inner guards first, then the real lord only
            // after a real gate opens.  The lord's normal death pipeline is the
            // only mechanism that can capture/reset a keep.
            GameKeepGuard[] guards = bot.GetNPCsInRadius(TargetSearchRadius)
                .OfType<GameKeepGuard>()
                .Where(guard => guard.IsAlive && guard.Realm != bot.Realm &&
                    (guard.Realm != eRealm.None || BattlegroundBrackets.IsBattlegroundRegion(guard.CurrentRegionID)))
                .Where(guard => guard.Component?.Keep != null && $"rvr-keep-{guard.Component.Keep.KeepID}" == _rvrDestination?.Id)
                .Where(guard => !guard.IsPortalKeepGuard && AutonomousRvrKeepPolicy.IsSiegeObjective(guard.Component?.Keep))
                .Where(guard => !closedDoor || guard is not GuardLord)
                .Where(guard => GameServer.ServerRules.IsAllowedToAttack(bot, guard, true))
                .Where(guard => PathfindingProvider.Instance.HasLineOfSight(bot.CurrentZone, new(bot.X, bot.Y, bot.Z),
                    new(guard.X, guard.Y, guard.Z), PathfindingProvider.Instance.BlockingDoorAvoidanceFilters))
                .Where(guard => AutonomousRvrDefense.IsRangedDefender(bot) ||
                    AutonomousZoneItinerary.HasCompleteCorridor(keepNavigation, bot.CurrentZone,
                        new(bot.X, bot.Y, bot.Z), new(guard.X, guard.Y, guard.Z)))
                .OrderBy(guard => guard is GuardLord ? 1 : 0)
                .ThenBy(guard => bot.GetDistanceTo(guard))
                .ToArray();
            // Both attacking realms seek the native capture, not an endless
            // guard patrol. Nearby PvP is handled first by the threat scan.
            if (!closedDoor && guards.OfType<GuardLord>().FirstOrDefault() is { } exposedLord) return exposedLord;
            return SelectDistributedRvrTarget(bot, guards.Cast<GameLiving>().ToArray());
        }

        private bool HandleDungeonArrivalHold(BotBrain brain, GameBot bot, string reason)
        {
            if (AutonomousBotGroupCoordinator.TryGetDungeonInteriorStaging(bot, _groupDirective,
                    out Vector3 staging) && bot.CurrentZone != null &&
                Vector2.DistanceSquared(new(bot.X, bot.Y), new(staging.X, staging.Y)) > 150 * 150)
            {
                // MoveAlongGround only clips a straight segment at the first
                // wall. That clipped point is not arrival at an entrance around
                // a corner. Let the normal corridor mover finish the real path.
                IssuePath(bot, staging);
                SetStatus(bot, "Forming inside dungeon entrance", _groupDirective.SharedGoal, reason,
                    _camp?.MonsterName ?? string.Empty, _camp?.ZoneName ?? string.Empty);
                return true;
            }

            bot.StopMovingOnPath();
            bot.StopMoving();
            if (brain.CheckHeals())
                return true;
            bool full = AutonomousRestPolicy.IsFullyRecovered(bot.HealthPercent, bot.ManaPercent,
                bot.EndurancePercent, bot.MaxMana > 0);
            if (full)
                bot.WakeRecoveryRest();
            else if (!BotRestRecovery.BlocksRest(bot))
                bot.BeginRecoveryRest();
            SetStatus(bot, "Forming inside dungeon entrance", _groupDirective.SharedGoal, reason,
                _camp?.MonsterName ?? string.Empty, _camp?.ZoneName ?? string.Empty);
            return true;
        }

        private static GameLiving SelectDistributedRvrTarget(GameBot bot, GameLiving[] candidates)
        {
            if (candidates == null || candidates.Length == 0)
                return null;
            int warbandSize = bot.Group?.GetMembersInTheGroup().OfType<GameBot>().Count() ?? 1;
            long actorKey = bot.DatabaseID > 0 ? bot.DatabaseID : bot.ObjectID;
            // Some attackers focus the objective carrier; the others fight its
            // screen. A human is not preferred merely because it is human.
            if (unchecked((ulong)actorKey) % 3 == 0)
            {
                GameLiving carrier = candidates.FirstOrDefault(GameRelic.IsPlayerCarryingRelic);
                if (carrier != null) return carrier;
            }
            return candidates[AutonomousRvrStaging.TargetIndex(actorKey, candidates.Length, warbandSize)];
        }

        private bool TryEngageSharedDungeonOpponent(BotBrain brain, GameBot bot)
        {
            // Shared frontier dungeons use the early bounded threat check;
            // retain this separate path only for the other shared dungeon.
            if (brain == null || bot == null || !AutonomousDungeonPolicy.IsSharedCombatDungeon(bot.CurrentRegionID) ||
                AutonomousDungeonPolicy.IsSharedFrontierDungeon(bot.CurrentRegionID) ||
                brain.HasAggro || bot.InCombat || bot.IsAttacking || IsSafeArea(bot) ||
                !AutonomousBotGroupCoordinator.CanInitiateNewPull(bot))
                return false;

            if (GameLoop.GameLoopTime < _nextSharedDungeonEnemyScan) return false;
            _nextSharedDungeonEnemyScan = GameLoop.GameLoopTime + 1500 + bot.ObjectID % 500;

            IEnumerable<GameLiving> humans = bot.GetPlayersInRadius(TargetSearchRadius)
                .Cast<GameLiving>();
            IEnumerable<GameLiving> worldBots = bot.GetNPCsInRadius(TargetSearchRadius).OfType<GameBot>()
                .Where(candidate => candidate != bot && candidate.IsAutonomousWorldBot && !candidate.IsTemporaryGroupHelper &&
                                    candidate.CurrentRegionID == bot.CurrentRegionID && bot.GetDistanceTo(candidate) <= TargetSearchRadius)
                .Cast<GameLiving>();

            GameLiving opponent = humans.Concat(worldBots)
                .Where(candidate => candidate != null && !IsSafeArea(candidate))
                .Where(candidate => !candidate.IsStealthed && AutonomousDungeonPolicy.CanEngageLocalOpponent(
                    bot.Realm, candidate.Realm, bot.CurrentRegionID, candidate.CurrentRegionID,
                    candidate.IsAlive, GameServer.ServerRules.IsAllowedToAttack(bot, candidate, true)))
                .OrderBy(candidate => bot.GetDistanceTo(candidate))
                .Where(candidate => PathfindingProvider.Instance.HasLineOfSight(bot.CurrentZone,
                    new(bot.X, bot.Y, bot.Z), new(candidate.X, candidate.Y, candidate.Z), PathfindingProvider.Instance.DefaultFilters))
                .FirstOrDefault();
            if (opponent == null)
                return false;

            bot.StopMovingOnPath();
            bot.StopMoving();
            bot.TargetObject = opponent;
            AutonomousBotGroupCoordinator.MarkCombatObserved(bot.Group);
            brain.AddToAggroList(opponent, Math.Max(100, opponent.EffectiveLevel * 12));
            brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
            return true;
        }

        private static GameKeepDoor FindClosedEnemyDoor(GameBot bot, string objectiveId = null) =>
            GameServer.KeepManager.GetKeepsOfRegion(bot.CurrentRegionID)
                .Where(AutonomousRvrKeepPolicy.IsSiegeObjective)
                .Where(keep => keep.Realm != eRealm.None && keep.Realm != bot.Realm)
                .Where(keep => objectiveId == null || objectiveId == $"rvr-keep-{keep.KeepID}")
                .SelectMany(keep => keep.Doors.Values)
                .Where(door => door.IsAlive && door.IsAttackableDoor && door.State == eDoorState.Closed)
                .Where(door => bot.GetDistanceTo(door) <= TargetSearchRadius)
                .OrderBy(door => bot.GetDistanceTo(door))
                .FirstOrDefault();


        private static GameMerchant FindReachableSiegeMerchant(GameBot bot)
        {
            HashSet<ushort> reachable = ReachableRegions(bot.Realm, bot.CurrentRegionID);
            return WorldMgr.GetAllRegions()
                .Where(region => region != null && reachable.Contains(region.ID))
                .SelectMany(region => region.Merchants)
                .Where(merchant => merchant.ObjectState is GameObject.eObjectState.Active && merchant.CurrentZone != null)
                .Where(merchant => (merchant.Realm == eRealm.None || merchant.Realm == bot.Realm) &&
                                   IsZoneAccessible(bot.Realm, merchant.CurrentZone) &&
                                   merchant.TradeItems != null &&
                                   AutonomousSiegePolicy.IsSiegeMerchant(bot.Realm, merchant.TradeItems.ItemsListID))
                .Where(merchant => merchant.TradeItems.GetAllItems().Values.OfType<DbItemTemplate>()
                    .Any(template => template.Price > 0 && AutonomousSiegePolicy.IsRamKit(bot.Realm, template.Id_nb)))
                .OrderBy(merchant => EstimateTravelMinutes(bot, merchant.CurrentRegionID, merchant.X, merchant.Y))
                .FirstOrDefault();
        }


        private static void ReleaseOwnedSiegeRams(GameBot bot)
        {
            if (bot == null)
                return;
            AutonomousSiegeJobs.Release(bot);
            foreach (GameSiegeWeapon ram in AutonomousSiegeOwnership.All(bot))
                ram.ReleaseControl();
        }


        private GameRelic FindInteractableRelic(GameBot bot) =>
            RelicMgr.GetRelics()
                .Where(relic => relic.ObjectState is GameObject.eObjectState.Active && relic.CurrentRegionID == bot.CurrentRegionID)
                .Where(relic => Distance(bot.X, bot.Y, relic.X, relic.Y) <= 600)
                .Where(relic => !relic.IsMounted || relic.Realm != bot.Realm && RelicMgr.CanPickupRelicFromShrine(bot, relic))
                // A nearby shrine is not reachable through an intact gate.
                // Once breached this is checked again on the next AI turn.
                .Where(relic => !relic.IsMounted || FindClosedEnemyDoor(bot, _rvrDestination?.Id) == null)
                .FirstOrDefault();

        private bool TravelRvrObjective(GameBot bot, CampDestination destination)
        {
            if (TryFrontierTransport(bot, destination)) return true;
            if (bot.CurrentRegionID != destination.RegionId)
            {
                CampDestination previous = _camp;
                _camp = destination;
                try { return TravelAcrossRegions(bot); }
                finally { _camp = previous; }
            }
            if (IsKeepOrKeepPatrolDestination(destination))
            {
                FollowKeepTravel(bot, destination);
                return true;
            }
            return IssuePath(bot, new(destination.X, destination.Y, destination.Z));
        }

        private bool TryResolveKeepTravelApproach(GameBot bot,CampDestination destination,IPathfindingMgr nav,Vector3 actor,out Vector3 approach)
        {
            Vector3 center=new(destination.X,destination.Y,destination.Z);
            var keep=GameServer.KeepManager.GetKeepsOfRegion(bot.CurrentRegionID).FirstOrDefault(k=>$"rvr-keep-{k.KeepID}"==destination.Id);
            if(keep!=null && keep.Realm!=bot.Realm)
            {
                var gates=keep.Doors.Values.Where(d=>d.IsAlive && d.IsAttackableDoor && d.State==eDoorState.Closed)
                    .Select(d=>new Vector3(d.X,d.Y,d.Z)).ToArray();
                int lateral = ((int)(bot.DatabaseID % 17) - 8) * 60;
                if(gates.Length>0 && AutonomousRvrApproach.TryGateApproach(nav,bot.CurrentRegion,bot.Realm,actor,center,gates,out approach,lateral))return true;
                // With every gate breached, enter on a real connected floor.
                if(gates.Length==0)
                {
                    // Do not stop at the courtyard center while the capturable
                    // lord is upstairs and out of sight. Keep native capture
                    // credit: every attacking realm routes to the same lord.
                    var lord = keep.Guards.Values.OfType<GuardLord>().FirstOrDefault(g => g.IsAlive);
                    if (lord != null) center = new(lord.X, lord.Y, lord.Z);
                    var zone=bot.CurrentRegion.GetZone(destination.X,destination.Y);
                    var floor=nav.GetClosestPoint(zone,center,48,48,256,nav.DefaultFilters);
                    if(floor.HasValue && RvrKeepRoute.TryBuild(bot.CurrentRegion,nav,bot.Realm,actor,floor.Value,out _))
                    { approach=floor.Value;return true; }
                }
            }
            return AutonomousRvrApproach.TryResolveAcrossZones(nav,bot.CurrentRegion,bot.Realm,actor,center,out approach);
        }

        private Vector3? _rvrTravelWaypoint;
        private Vector3 _rvrTravelGoal;
        private bool IssueVariedRvrPath(GameBot bot, Vector3 destination)
        {
            if (!_rvrTravelWaypoint.HasValue || Vector3.DistanceSquared(_rvrTravelGoal, destination) > 500 * 500 ||
                Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), _rvrTravelWaypoint.Value) < 180 * 180)
            {
                _rvrTravelGoal = destination;
                _rvrTravelWaypoint = AutonomousRvrTravel.ChooseWaypoint(bot, destination);
            }
            // ChooseWaypoint validates both legs when the final goal is in the
            // same zone. Continue that existing route without a full AI wake.
            // A changed goal or cross-zone route still needs its own planning.
            Vector3? continuation = _rvrTravelGoal == destination && _rvrTravelWaypoint.Value != destination &&
                bot.CurrentZone != null &&
                bot.CurrentRegion.GetZone((int)destination.X, (int)destination.Y) == bot.CurrentZone &&
                bot.CurrentRegion.GetZone((int)_rvrTravelWaypoint.Value.X, (int)_rvrTravelWaypoint.Value.Y) == bot.CurrentZone
                    ? destination : null;
            return IssuePath(bot, _rvrTravelWaypoint.Value, continuation);
        }

        private CampDestination ChooseRvrDestination(GameBot bot)
        {
            HashSet<ushort> reachable = ReachableRegions(bot.Realm, bot.CurrentRegionID);
            // A warband that chose a frontier stays on it for a while. Re-planning every minute across all
            // three frontiers sent bots home and out again through the portal keeps over and over
            // (run 16: 6,500 departures an hour, one Nightshade ported 11 times in 30 minutes). Relic
            // carriers anywhere and shared siege or defense calls (handled before this) still override.
            HashSet<ushort> local = GameLoop.GameLoopTime < _rvrFrontierCommittedUntil && reachable.Contains(_rvrFrontierRegion)
                ? [_rvrFrontierRegion] : reachable;
            var choices = new List<CampDestination>();
            var objectives = new List<AutonomousRvrEventLayer.LiveObjective>();
            RvrPlanningView planning = GetRvrPlanningView();
            foreach (GameBot enemy in planning.Candidates
                         .Where(candidate => candidate.IsAlive && candidate.Realm != eRealm.None && candidate.Realm != bot.Realm &&
                                             AutonomousObjectiveAssignments.Is(candidate, eAutonomousObjectiveKind.RvR) &&
                                             local.Contains(candidate.CurrentRegionID) && IsInFrontier(candidate))
                         .OrderBy(candidate => unchecked((ulong)(candidate.DatabaseID ^ bot.DatabaseID * 397) * 11400714819323198485UL))
                         .Take(24))
            {
                string id = $"rvr-enemy-{enemy.DatabaseID}";
                CampDestination destination = new(id, enemy.Name, enemy.CurrentZone?.Description ?? "frontier",
                    enemy.CurrentRegionID, enemy.X, enemy.Y, enemy.Z, 1, false, true);
                choices.Add(destination);
                objectives.Add(new(id, enemy.Name, AutonomousRvrEventLayer.Intent.HuntEnemy, enemy.Realm,
                    enemy.CurrentRegionID, enemy.X, enemy.Y, enemy.Z, false, 1, 0, 0, 0));
            }

            // Relic ownership remains wholly native. A player or bot carrier
            // is merely a live three-realm event anchor: allies escort, while
            // both other realms can contest the carrier under per-realm caps.
            foreach (GameRelic relic in RelicMgr.GetRelics())
            {
                GameLiving carrier = relic?.CurrentCarrier;
                if (carrier == null && !relic.IsMounted && relic.ObjectState == GameObject.eObjectState.Active &&
                    reachable.Contains(relic.CurrentRegionID))
                {
                    string groundId = RelicObjectiveId(relic);
                    choices.Add(new(groundId, $"dropped {relic.Name}", relic.CurrentZone?.Description ?? "frontier",
                        relic.CurrentRegionID, relic.X, relic.Y, relic.Z, 1, false, true));
                    objectives.Add(new(groundId, $"dropped {relic.Name}", AutonomousRvrEventLayer.Intent.HuntEnemy,
                        relic.OriginalRealm, relic.CurrentRegionID, relic.X, relic.Y, relic.Z, false, 1, 0, 0, 0, true));
                    continue;
                }
                if (carrier == null || !carrier.IsAlive || !reachable.Contains(carrier.CurrentRegionID) || !IsInFrontier(carrier))
                    continue;
                string id = RelicObjectiveId(relic);
                CampDestination destination = new(id, $"active {relic.Name}",
                    carrier.CurrentZone?.Description ?? "frontier", carrier.CurrentRegionID, carrier.X, carrier.Y, carrier.Z, 1, false, true);
                choices.Add(destination);
                objectives.Add(new(id, destination.MonsterName, AutonomousRvrEventLayer.Intent.HuntEnemy, carrier.Realm,
                    carrier.CurrentRegionID, carrier.X, carrier.Y, carrier.Z, false, 1, 0, 0, 0, true));
            }

            // Keep/relic locations are live server objectives, not synthesized
            // camps. All legal reachable frontier keeps get equal selection weight.
            foreach (Region region in WorldMgr.GetAllRegions().Where(region => region != null && local.Contains(region.ID)))
            {
                foreach (AbstractGameKeep keep in GameServer.KeepManager.GetKeepsOfRegion(region.ID)
                             .Where(keep => AutonomousRvrKeepPolicy.IsSiegeObjective(keep) && IsFrontierRegionPoint(keep.Region, keep.X, keep.Y)))
                {
                    string id = $"rvr-keep-{keep.KeepID}";
                    string label = keep.IsRelic ? $"relic keep {keep.Name}" : $"keep {keep.Name}";
                    CampDestination destination = new(id, label, keep.Name, keep.Region, keep.X, keep.Y, keep.Z, 1, false, true);
                    choices.Add(destination);
                    int closedDoors = keep.Doors.Values.Count(door => door.IsAlive && door.State == eDoorState.Closed);
                    int guards = keep.Guards.Values.Count(guard => guard.IsAlive);
                    objectives.Add(new(id, label,
                        keep.Realm == bot.Realm ? AutonomousRvrEventLayer.Intent.Roam :
                        keep.IsRelic ? AutonomousRvrEventLayer.Intent.AssaultRelicKeep : AutonomousRvrEventLayer.Intent.AssaultKeep,
                        keep.Realm, keep.Region, keep.X, keep.Y, keep.Z, keep.IsRelic,
                        planning.Count(keep.Region, bot.Realm).Enemies,
                        planning.Count(keep.Region, bot.Realm).Allies,
                        guards, closedDoors, UnderAttack:
                        keep.LastAttackedByEnemyTick > 0 && keep.CurrentRegion.Time - keep.LastAttackedByEnemyTick < 120_000 ||
                        keep.Guards.Values.Any(guard => guard.IsAlive && guard.InCombat)));
                    // Every live frontier keep also supplies a patrol anchor;
                    // small warbands can roam there without declaring a siege.
                    string roamId = $"rvr-roam-{keep.KeepID}";
                    choices.Add(new(roamId, $"frontier near {keep.Name}", keep.Name, keep.Region, keep.X, keep.Y, keep.Z, 1, false, true));
                    objectives.Add(new(roamId, $"frontier near {keep.Name}", AutonomousRvrEventLayer.Intent.Roam,
                        keep.Realm, keep.Region, keep.X, keep.Y, keep.Z, keep.IsRelic, 0, 0, guards, closedDoors));
                }
            }

            // Roaming is not limited to the road between keeps. Reuse the
            // already-built live camp catalog and sample a bounded set of
            // frontier clearings; this does not query the database or create
            // PvE goals. Combat remains normal RvR/nearby self-defense.
            var patrolNav = PathfindingProvider.Instance;
            if (patrolNav.IsAvailable)
            {
                foreach (CampCatalogCell cell in CampCatalogSnapshot()
                    .Where(c => c.IsFrontier && !c.IsDungeon && c.LiveMobCount > 0 && local.Contains(c.RegionId) &&
                        c.Zone != null && IsFrontierRegionPoint(c.RegionId, c.X, c.Y))
                    .OrderBy(_ => Random.Shared.Next()).Take(24))
                {
                    Vector3 raw = new(cell.X, cell.Y, cell.Z);
                    Vector3? floor = patrolNav.GetClosestPoint(cell.Zone, raw, 64, 64, 96, patrolNav.DefaultFilters);
                    if (!floor.HasValue || !AutonomousRendezvousNavigation.HasLocalExit(patrolNav, cell.Zone, floor.Value)) continue;
                    string id = "rvr-camp-" + cell.Id;
                    string name = "frontier patrol near " + cell.MonsterName;
                    int x = (int)floor.Value.X, y = (int)floor.Value.Y, z = (int)floor.Value.Z;
                    choices.Add(new(id, name, cell.ZoneName, cell.RegionId, x, y, z, 1, false, true));
                    objectives.Add(new(id, name, AutonomousRvrEventLayer.Intent.Roam, eRealm.None, cell.RegionId,
                        x, y, z, false, 0, 0, 0, 0));
                }
            }

            if (choices.Count == 0)
                return null;

            GameBot[] warband = bot.Group?.GetMembersInTheGroup().OfType<GameBot>()
                .Where(member => member.IsAutonomousWorldBot && AutonomousObjectiveAssignments.Is(member, eAutonomousObjectiveKind.RvR))
                .ToArray() ?? [];
            if (warband.Length == 0)
                warband = [bot];
            int averageLevel = (int)Math.Round(warband.Average(member => member.Level));
            int healers = warband.Count(member => member.ClassName.Contains("cleric", StringComparison.OrdinalIgnoreCase) ||
                                                    member.ClassName.Contains("healer", StringComparison.OrdinalIgnoreCase) ||
                                                    member.ClassName.Contains("druid", StringComparison.OrdinalIgnoreCase) ||
                                                    member.ClassName.Contains("bard", StringComparison.OrdinalIgnoreCase));
            bool siegeReady = warband.Length >= 4 && averageLevel >= 35 && CanSupplySiege(bot);
            AutonomousRvrEventLayer.Plan plan = AutonomousRvrEventLayer.ChooseOrJoin(
                new AutonomousRvrEventLayer.Force(_groupDirective?.GroupId ?? $"rvr-{bot.DatabaseID}", bot.Realm,
                    warband.Length, averageLevel, healers, siegeReady,
                    (unchecked((ulong)(_groupDirective?.Leader?.DatabaseID ?? bot.DatabaseID)) * 2654435761UL % 100) < 30,
                    warband.Select(member => member.DatabaseID).ToArray(), warband.Min(member => member.Level)),
                objectives, GameLoop.GameLoopTime, Random.Shared.NextDouble());
            _rvrSharedEvent = plan?.IsSharedEvent == true;
            _rvrIntent = plan?.Intent ?? AutonomousRvrEventLayer.Intent.Roam;
            bot.TempProperties.SetProperty("RvrWarbandIntent", (int)_rvrIntent);
            bot.TempProperties.SetProperty("RvrDefendingKeep",
                _rvrIntent == AutonomousRvrEventLayer.Intent.DefendEvent && plan.TargetId.StartsWith("rvr-keep-") &&
                int.TryParse(plan.TargetId.Substring(9), out int defendingKeep) ? defendingKeep : -1);
            CampDestination chosen = choices.FirstOrDefault(destination => destination.Id == plan?.TargetId) ??
                (plan == null ? null : new CampDestination(plan.TargetId, plan.Name, plan.Name,
                    plan.RegionId, plan.X, plan.Y, plan.Z, 1, false, true));
            if (chosen != null && chosen.RegionId is 1 or 100 or 200 &&
                (chosen.RegionId != _rvrFrontierRegion || GameLoop.GameLoopTime >= _rvrFrontierCommittedUntil))
            {
                _rvrFrontierRegion = chosen.RegionId;
                _rvrFrontierCommittedUntil = GameLoop.GameLoopTime + FrontierCommitMilliseconds + bot.DatabaseID % 300_000;
            }
            return chosen;
        }

        /// <summary>How long an ordinary warband keeps to the frontier it chose (plus up to five minutes per bot).</summary>
        internal const long FrontierCommitMilliseconds = 20 * 60_000;
        private ushort _rvrFrontierRegion;
        private long _rvrFrontierCommittedUntil;

        private AutonomousRvrEventLayer.Intent _rvrIntent;
        private long _nextRvrPlanReview;
        // Removed world objects have ObjectID zero. Realm/type remains unique
        // while two or more relics are simultaneously carried or dropped.
        private static string RelicObjectiveId(GameRelic relic) =>
            AutonomousRvrEventLayer.RelicCarrierTargetId(relic);

        private void PatrolRvr(GameBot bot)
        {
            if (_rvrDestination == null)
                return;
            Vector3 current = new(bot.X, bot.Y, bot.Z);
            if (!_patrolDestination.HasValue || Vector3.DistanceSquared(current, _patrolDestination.Value) < 120 * 120)
            {
                Zone zone = bot.CurrentZone;
                Vector3 center = _rvrApproachDestination ?? new(_rvrDestination.X, _rvrDestination.Y, _rvrDestination.Z);
                _patrolDestination = PathfindingProvider.Instance.GetRandomPoint(zone, center, 700,
                    PathfindingProvider.Instance.DefaultFilters) ?? center;
                if(IsKeepOrKeepPatrolDestination(_rvrDestination) &&
                    !AutonomousZoneItinerary.HasCompleteCorridor(AutonomousKeepApproachNavigation.ForRealm(PathfindingProvider.Instance,bot.CurrentRegion,bot.Realm),zone,current,_patrolDestination.Value))
                    _patrolDestination=center;
                _nextMoveOrderTick = 0;
            }
            IssuePath(bot, _patrolDestination.Value);
        }

        private void SetRvrStatus(GameBot bot, string activity, string progress, string detail, string target = "")
        {
            if (bot.PersistentRecord != null)
                bot.PersistentRecord.ObjectivePhase = activity;
            SetStatus(bot, activity, "Active frontier RvR", detail, target, _rvrDestination?.ZoneName ?? string.Empty, true);
        }

        private void BeginSavageMeleePull(GameBot bot, GameNPC target)
        {
            long nowTick = GameLoop.GameLoopTime;
            _savageMeleePullTarget = target;
            _savageMeleePullCampId = _camp?.Id ?? string.Empty;
            _savageMeleePullAssignmentId = bot.PersistentRecord?.ObjectiveAssignmentId ?? string.Empty;
            _savageMeleePullGroup = bot.Group;
            _savageMeleePullLastPosition = new(bot.X, bot.Y, bot.Z);
            _savageMeleePullLastDistance = (int)bot.GetDistanceTo(target);
            _savageMeleePullTargetHealth = target.Health;
            _savageMeleePullLastAttackTick = bot.LastAttackTick;
            _savageMeleePullLastAttackedTick = bot.LastAttackedByEnemyTick;
            _savageMeleePullStartedTick = nowTick;
            _savageMeleePullProgressTick = nowTick;
            _savageMeleePullRetried = false;
            bot.WakeRecoveryRest();
        }

        // A cast handler still attached well past its cast time freezes the bot:
        // the follow tick stops a casting NPC, and the brain waits for the active
        // cast before choosing anything else. Clear it once it is clearly stale.
        private void ClearStaleCast(GameBot bot)
        {
            object handler = bot.castingComponent?.SpellHandler;
            long nowTick = GameLoop.GameLoopTime;
            if (handler == null)
            {
                _pullWatchCastHandler = null;
                return;
            }
            if (!ReferenceEquals(handler, _pullWatchCastHandler))
            {
                _pullWatchCastHandler = handler;
                _pullWatchCastSinceTick = nowTick;
                return;
            }
            if (SavageBotCombatPolicy.IsStaleCast(bot.castingComponent.SpellHandler.Spell, _pullWatchCastSinceTick, nowTick))
            {
                bot.StopCurrentSpellcast();
                _pullWatchCastHandler = null;
            }
        }

        private void ClearSavageMeleePull()
        {
            _savageMeleePullTarget = null;
            _savageMeleePullCampId = string.Empty;
            _savageMeleePullAssignmentId = string.Empty;
            _savageMeleePullGroup = null;
            _savageMeleePullRetried = false;
        }

        // A follower can see a corridor blocker before its designated puller.
        // The follower publishes only a one-shot target; the puller's own Tick
        // starts and owns the recovery clock. Repeated reports never reset it.
        internal static void QueueSavageMeleePullHandoff(GameBot puller, GameNPC target)
        {
            if (puller?.CharacterClass != null && target != null &&
                SavageBotCombatPolicy.MustMeleePull((eCharacterClass)puller.CharacterClass.ID))
                puller.TempProperties.SetProperty(PendingSavageMeleePullHandoffKey,
                    new PendingSavageMeleePullHandoff(target, puller.Group,
                        puller.PersistentRecord?.ObjectiveAssignmentId ?? string.Empty,
                        puller.LastAttackTick, puller.LastAttackedByEnemyTick, target.Health));
        }

        internal static bool IsSavageMeleePullHandoffCoolingDown(GameBot puller, GameNPC target)
        {
            if (puller == null || target == null)
                return false;
            var failure = puller.TempProperties.GetProperty<FailedSavageMeleePullHandoff>(
                FailedSavageMeleePullHandoffKey);
            return failure.TargetId == target.ObjectID && failure.RegionId == target.CurrentRegionID &&
                failure.Group == puller.Group &&
                string.Equals(failure.AssignmentId,
                    puller.PersistentRecord?.ObjectiveAssignmentId ?? string.Empty, StringComparison.Ordinal) &&
                GameLoop.GameLoopTime < failure.RetryAfterTick;
        }

        private void AcceptSavageMeleePullHandoff(BotBrain brain, GameBot bot)
        {
            if (!bot.TempProperties.TryRemoveProperty(PendingSavageMeleePullHandoffKey, out object value) ||
                value is not PendingSavageMeleePullHandoff pending ||
                pending.Group == null || pending.Group != bot.Group ||
                !string.Equals(pending.AssignmentId,
                    bot.PersistentRecord?.ObjectiveAssignmentId ?? string.Empty, StringComparison.Ordinal))
                return;

            GameNPC target = pending.Target;
            if (target == null ||
                !SavageBotCombatPolicy.MustMeleePull((eCharacterClass)bot.CharacterClass.ID) ||
                _savageMeleePullTarget == target || target.IsAlive != true ||
                target.CurrentRegion != bot.CurrentRegion || bot.TargetObject != target ||
                brain.GetBaseAggroAmount(target) <= 0 ||
                SavageBotCombatPolicy.HasMeleePullContact(pending.AttackTick, bot.LastAttackTick,
                    pending.AttackedTick, bot.LastAttackedByEnemyTick, pending.TargetHealth,
                    target.Health))
                return;

            if (IsSavageMeleePullHandoffCoolingDown(bot, target))
            {
                ReleaseSavageMeleePullTarget(brain, bot, target);
                return;
            }
            BeginSavageMeleePull(bot, target);
        }

        // Consecutive pulls at the current camp that never reached or hit their
        // target. Failed targets are only skipped for 90 s, so a camp whose spawns
        // are all unreachable (bear cubs across water, corpse flickers on a ledge)
        // used to hold a solo bot standing still for over an hour.
        internal const int UnreachablePullsBeforeCampAbandon = 3;
        private int _campPullFailures;
        private string _campPullFailureCampId = string.Empty;

        private bool AbandonCampAfterUnreachablePulls(GameBot bot)
        {
            if (_camp == null || bot.Group != null)
                return false;
            if (!string.Equals(_campPullFailureCampId, _camp.Id, StringComparison.Ordinal))
            {
                _campPullFailureCampId = _camp.Id;
                _campPullFailures = 0;
            }
            if (++_campPullFailures < UnreachablePullsBeforeCampAbandon)
                return false;

            _campPullFailures = 0;
            if (AutonomousCampRouteQuarantine.ReportFailure(_camp.Id, bot.DatabaseID))
                Log.Warn($"AUTONOMOUS_CAMP_ROUTE_QUARANTINE camp=\"{_camp.Id}\" target=\"{_camp.MonsterName}\" " +
                         $"zone=\"{_camp.ZoneName}\" bots={AutonomousCampRouteQuarantine.DistinctBotsToQuarantine} " +
                         $"hours={AutonomousCampRouteQuarantine.QuarantineDuration.TotalHours:0} reason=unreachable-pulls");
            Log.Warn($"AUTONOMOUS_CAMP_UNREACHABLE_PULLS bot={bot.Name} id={bot.DatabaseID} level={bot.Level} " +
                     $"class=\"{bot.ClassName}\" camp=\"{_camp.Id}\" target=\"{_camp.MonsterName}\" " +
                     $"region={bot.CurrentRegionID} x={bot.X} y={bot.Y} z={bot.Z}");
            AbandonCamp(bot, $"{UnreachablePullsBeforeCampAbandon} pulls in a row never reached a {_camp.MonsterName}");
            return true;
        }

        private static bool ReleaseSavageMeleePullTarget(BotBrain brain, GameBot bot, GameNPC target)
        {
            brain.RemoveFromAggroList(target);
            if (bot.TargetObject == target)
            {
                bot.StopAttack();
                bot.TargetObject = null;
            }
            if (bot.FollowTarget == target)
            {
                bot.StopFollowing();
                bot.StopMovingOnPath();
                bot.StopMoving();
            }
            if (!brain.HasAggro && brain.FSM.GetCurrentState()?.StateType == eFSMStateType.AGGRO)
                brain.FSM.SetCurrentState(eFSMStateType.IDLE);
            return !brain.HasAggro;
        }

        private bool ObserveSavageMeleePull(BotBrain brain, GameBot bot)
        {
            GameNPC target = _savageMeleePullTarget;
            if (target == null)
                return false;

            if (_savageMeleePullCampId.Length == 0 && _camp != null)
                _savageMeleePullCampId = _camp.Id;
            bool sameGoal = string.Equals(_savageMeleePullAssignmentId,
                bot.PersistentRecord?.ObjectiveAssignmentId ?? string.Empty, StringComparison.Ordinal) &&
                _savageMeleePullGroup == bot.Group &&
                (_savageMeleePullCampId.Length == 0 ||
                    string.Equals(_savageMeleePullCampId, _camp?.Id ?? string.Empty,
                        StringComparison.Ordinal));
            if (!SavageBotCombatPolicy.IsMeleePullTargetValid(
                    target.IsAlive, target.ObjectState == GameObject.eObjectState.Active,
                    target.CurrentRegion == bot.CurrentRegion,
                    sameGoal) ||
                bot.IsPlayerLedGroup)
            {
                ClearSavageMeleePull();
                return false;
            }
            eCharacterClass pullClass = (eCharacterClass)bot.CharacterClass.ID;
            bool meleePuller = SavageBotCombatPolicy.MustMeleePull(pullClass) ||
                !BotBrain.PrefersSpellRange(bot.CharacterClass, bot.Group?.MemberCount > 1, true);

            // A real swing (including a miss), incoming hit, or health lost by
            // the target belongs to ordinary combat. Never cancel that fight.
            if (SavageBotCombatPolicy.HasMeleePullContact(
                    _savageMeleePullLastAttackTick, bot.LastAttackTick,
                    _savageMeleePullLastAttackedTick, bot.LastAttackedByEnemyTick,
                    _savageMeleePullTargetHealth, target.Health))
            {
                _campPullFailures = 0;
                ClearSavageMeleePull();
                return false;
            }

            // Crowd control prevents a legal approach; the native combat state
            // resumes once it ends. A long control effect is not a path failure.
            if (bot.IsCrowdControlled)
                return false;

            long nowTick = GameLoop.GameLoopTime;
            Vector3 position = new(bot.X, bot.Y, bot.Z);
            int distance = (int)bot.GetDistanceTo(target);
            if (SavageBotCombatPolicy.MadeMeleePullProgress(
                    _savageMeleePullLastPosition, position, _savageMeleePullLastDistance, distance))
            {
                _savageMeleePullLastPosition = position;
                _savageMeleePullLastDistance = distance;
                _savageMeleePullProgressTick = nowTick;
                _savageMeleePullRetried = false;
            }

            switch (SavageBotCombatPolicy.EvaluatePull(pullClass, nowTick, _savageMeleePullStartedTick,
                        _savageMeleePullProgressTick, _savageMeleePullRetried))
            {
                case SavageBotCombatPolicy.MeleePullDecision.RetryApproach:
                    _savageMeleePullRetried = true;
                    bot.WakeRecoveryRest();
                    if (!meleePuller)
                    {
                        // Casters: open with the pet again and walk well inside
                        // spell range; the brain keeps that closer range for a while.
                        bot.ControlledBrain?.Attack(target);
                        brain.RequestCloserSpellApproach(nowTick + SavageBotCombatPolicy.PullGiveUpAfterNoProgressMilliseconds);
                        bot.ForcePathReplot();
                    }
                    else if (BotRangedCombat.UsesAutonomousBowPositioning(bot) && distance <= ArcherStallMeleeDistance)
                    {
                        // Archer stalled near its target without releasing a shot:
                        // fight it in melee for a while (the brain honours this and
                        // the native melee follow closes the last few steps).
                        // Ending the stalled draw lets the brain's melee path pick
                        // the right melee weapon and start the attack next turn.
                        brain.RequestArcherMeleeFallback(nowTick + ArcherMeleeFallbackMilliseconds);
                        bot.StopAttack();
                    }
                    else if (distance > bot.MeleeAttackRange)
                    {
                        bot.StopFollowing();
                        bot.ForcePathReplot();
                        bot.Follow(target, bot.StickMinimumRange, BotBrain.MAX_AGGRO_LIST_DISTANCE);
                    }
                    else
                        bot.StartAttack(target);
                    break;

                case SavageBotCombatPolicy.MeleePullDecision.GiveUp:
                    long retryAfter = nowTick + SavageBotCombatPolicy.FailedMeleePullRetryMilliseconds;
                    _failedSavageMeleePullTargets[target.ObjectID] = retryAfter;
                    bot.TempProperties.SetProperty(FailedSavageMeleePullHandoffKey,
                        new FailedSavageMeleePullHandoff(target.ObjectID, target.CurrentRegionID,
                            bot.Group, bot.PersistentRecord?.ObjectiveAssignmentId ?? string.Empty,
                            retryAfter));
                    bool noOtherAggro = ReleaseSavageMeleePullTarget(brain, bot, target);
                    ClearSavageMeleePull();
                    _nextTargetSearchTick = nowTick + 1_500;
                    SetStatus(bot, meleePuller ? "Seeking another melee pull" : "Seeking another pull", GoalText(),
                        "The selected target was never reached or hit; trying another live spawn",
                        target.Name, _camp?.ZoneName ?? string.Empty);
                    if (SavageBotCombatPolicy.MustMeleePull(pullClass))
                        Log.Warn($"SAVAGE_MELEE_PULL_TIMEOUT bot={bot.Name} target={target.Name} region={bot.CurrentRegionID} x={bot.X} y={bot.Y} z={bot.Z}");
                    else
                        Log.Warn($"AUTONOMOUS_PULL_TIMEOUT bot={bot.Name} class={pullClass} level={bot.Level} target=\"{target.Name}\" " +
                            $"targetLevel={target.Level} distance={distance} casting={bot.IsCasting} " +
                            $"pendingCast={bot.castingComponent?.HasPendingSkillRequests == true} resting={bot.IsRecoveryResting} " +
                            $"manaPercent={bot.ManaPercent} moving={bot.IsMoving} pet={bot.ControlledBrain?.Body?.Name ?? "none"} " +
                            $"region={bot.CurrentRegionID} x={bot.X} y={bot.Y} z={bot.Z}");
                    if (AbandonCampAfterUnreachablePulls(bot))
                        return true;
                    return noOtherAggro;
            }

            // The weapon action can drop an invalid target before the next
            // controller pulse. Keep this one bounded approach alive without
            // resetting its progress clock or choosing the same spawn anew.
            if (!brain.HasAggro && !bot.IsAttacking)
            {
                bot.WakeRecoveryRest();
                bot.TargetObject = target;
                brain.AddToAggroList(target, Math.Max(25, target.EffectiveLevel * 10));
                brain.CommitDungeonPull(target);
                if (brain.FSM.GetCurrentState()?.StateType != eFSMStateType.AGGRO)
                    brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
            }
            return false;
        }

        private bool WorkCamp(BotBrain brain, GameBot bot)
        {
            if (HoldBeforeNewGroupPull(brain, bot)) return true;
            if (bot.GoalDiagnosticAttempt is { ArrivedUtc: null } attempt)
                attempt.Arrive(DateTime.UtcNow);
            // Group support resolves before personal recovery. A healer tops up
            // the party, and BotBrain's earlier maintenance pass refreshes
            // expired buffs, before this actor spends power by sitting down.
            if (brain.CheckHeals())
                return true;
            if (HandleCampRecovery(bot, _recoverBeforeNextCampTarget))
                return true;

            _recoverBeforeNextCampTarget = false;

            if (GameLoop.GameLoopTime < _nextTargetSearchTick)
                return true;
            _nextTargetSearchTick = GameLoop.GameLoopTime + 900 + bot.ObjectID % 500;

            GameNPC target = FindCampTarget(bot);
            // A level-up can turn a whole low-level camp grey. Those kills give no
            // experience, so a solo bot fought on in place until the stuck watchdog
            // rescued it fifteen minutes later. Targets are sorted by con, so a grey
            // pick means nothing better is near: plan a camp for the new level.
            if (target != null && bot.Group == null && !_camp.IsDungeon &&
                ConLevels.GetConColor(bot.GetConLevel(target)) == ConColor.GREY)
            {
                Log.Info($"AUTONOMOUS_CAMP_OUTLEVELED bot={bot.Name} id={bot.DatabaseID} level={bot.Level} " +
                         $"class=\"{bot.ClassName}\" camp=\"{_camp.Id}\" target=\"{target.Name}\" targetLevel={target.Level}");
                AbandonCamp(bot, $"Outleveled {_camp.MonsterName}; the camp no longer gives experience");
                return true;
            }
            if (target != null)
            {
                if (AutonomousDefensivePull.TryBeginFlyingHandoff(bot, target))
                {
                    SetStatus(bot, $"Ranged pulling {target.Name}", GoalText(),
                        "The designated puller handed an unreachable flying target to a ranged party member",
                        target.Name, _camp.ZoneName);
                    return true;
                }
                if (GuardDungeonTravel(bot, new(target.X, target.Y, target.Z)))
                    return !brain.HasAggro;
                _emptyCampSinceTick = 0;
                _reportedEmptySharedCampId = string.Empty;
                _patrolDestination = null;
                bot.StopMovingOnPath();
                bot.StopMoving();
                if (!SavageBotCombatPolicy.MustMeleePull((eCharacterClass)bot.CharacterClass.ID) &&
                    AutonomousDefensivePull.TryBegin(bot, target))
                {
                    SetStatus(bot, $"Ranged pulling {target.Name}", GoalText(),
                        "Party holds formation; the ranged puller brings the target back before everyone engages",
                        target.Name, _camp.ZoneName);
                    return true;
                }
                bot.TargetObject = target;
                _lastEngagedCon = ConLevels.GetConColor(bot.GetConLevel(target));
                AutonomousBotGroupCoordinator.MarkCombatObserved(bot.Group);
                brain.AddToAggroList(target, Math.Max(25, target.EffectiveLevel * 10));
                brain.CommitDungeonPull(target);
                brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                // Every class's pull is watched (Savages keep their own timings):
                // a pull that never connects is retried once, then abandoned
                // for another spawn instead of freezing until the stuck watchdog.
                BeginSavageMeleePull(bot, target);
                SetStatus(bot, $"Pulling {target.Name}", GoalText(),
                    $"Selected level {target.EffectiveLevel} {target.Name} at the live camp", target.Name, _camp.ZoneName);
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Objective);
                return false;
            }

            _emptyCampSinceTick = _emptyCampSinceTick == 0 ? GameLoop.GameLoopTime : _emptyCampSinceTick;
            if (GameLoop.GameLoopTime - _emptyCampSinceTick >= EmptyCampReplanMilliseconds)
            {
                if (AutonomousBotGroupCoordinator.ShouldHoldCampThroughRespawn(_groupDirective))
                {
                    if (!string.Equals(_reportedEmptySharedCampId, _camp.Id, StringComparison.Ordinal))
                    {
                        _reportedEmptySharedCampId = _camp.Id;
                        AutonomousBotGroupCoordinator.ReportCampTemporarilyEmpty(
                            bot, GameLoop.GameLoopTime - _emptyCampSinceTick);
                    }
                    long waited = GameLoop.GameLoopTime - _emptyCampSinceTick;
                    if (waited >= EmptyGroupCampRoamMilliseconds &&
                        TryFindSameGoalSearchAnchor(bot, out Vector3 nextAnchor) &&
                        AutonomousBotGroupCoordinator.TryRepositionEmptyCamp(bot, _camp.Id, nextAnchor, waited))
                    {
                        _emptyCampSinceTick = 0;
                        _reportedEmptySharedCampId = string.Empty;
                        _patrolDestination = null;
                        ResetRouteOrderState();
                        _groupDirective = AutonomousBotGroupCoordinator.Pulse(bot);
                        if (_groupDirective?.Camp != null)
                            _camp = FromSharedCamp(_groupDirective.Camp);
                        SetStatus(bot, $"Searching another {_camp.MonsterName} spawn", GoalText(),
                            "The party kept its original goal and is moving together to another verified spawn point",
                            _camp.MonsterName, _camp.ZoneName);
                        return true;
                    }
                    _patrolDestination = null;
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                    SetStatus(bot, $"Waiting for {_camp.MonsterName} to respawn", GoalText(),
                        "The party keeps its selected grind goal and will resume pulling when a live target returns",
                        _camp.MonsterName, _camp.ZoneName);
                    return true;
                }
                if (_camp.RegionId == AutonomousDarknessFallsPolicy.RegionId &&
                    (bot.Group?.MemberCount ?? 1) <= 1)
                {
                    // One certified DF room can contain only one spawn. Its
                    // ordinary respawn is longer than the outdoor empty-camp
                    // timeout. First try another live, certified room in this
                    // realm's wing; otherwise allow the original to respawn.
                    if (TryContinueNearbyDarknessFallsGrind(bot)) return true;
                    if (GameLoop.GameLoopTime - _emptyCampSinceTick <
                        DarknessFallsSoloRespawnHoldMilliseconds)
                    {
                        _patrolDestination = null;
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                        SetStatus(bot, $"Waiting for {_camp.MonsterName} to respawn", GoalText(),
                            "No reachable live copy remains in this wing; holding the certified room for its respawn",
                            _camp.MonsterName, _camp.ZoneName);
                        return true;
                    }
                }
                AbandonCamp(bot, $"No live {_camp.MonsterName} remained at the camp");
                return true;
            }

            PatrolCamp(bot);
            if (_camp == null) return true;
            SetStatus(bot, $"Searching the {_camp.MonsterName} camp", GoalText(),
                $"Scanning within {TargetSearchRadius:N0} units and moving between nearby spawn points for the assigned target",
                _camp.MonsterName, _camp.ZoneName);
            return true;
        }

        private long _nextSameGoalAnchorSearchTick;
        private bool TryFindSameGoalSearchAnchor(GameBot bot, out Vector3 anchor)
        {
            anchor = default;
            if (_camp == null || bot?.CurrentRegion == null)
                return false;
            // A DF goal is one certified room/wing, not every creature of the
            // same name in the entire shared dungeon.  Never roam through the
            // center into an enemy entrance wing after its local pack dies.
            if (_camp.RegionId == AutonomousDarknessFallsPolicy.RegionId)
                return false;
            long now = GameLoop.GameLoopTime;
            if (now < _nextSameGoalAnchorSearchTick) return false;
            _nextSameGoalAnchorSearchTick = now + 15_000;
            Vector3 currentAnchor = new(_camp.X, _camp.Y, _camp.Z);
            // A catalog cell is a spawn location, not evidence that a creature
            // is alive there. Prefer a real, same-name target and its connected
            // approach. This is a search route, not a through-wall attack: the
            // normal corridor clearing, rest, formation and attack LOS remain.
            if (bot.CurrentZone != null)
            {
                IPathfindingMgr nav = PathfindingProvider.Instance;
                foreach (GameNPC npc in bot.GetNPCsInRadius(TargetSearchRadius)
                    .Where(npc => npc.IsAlive && IsExperienceMonster(npc) && npc.CurrentRegionID == _camp.RegionId &&
                        npc.CurrentZone == bot.CurrentZone && GameServer.ServerRules.IsAllowedToAttack(bot, npc, true) &&
                        string.Equals(npc.Name, _camp.MonsterName, StringComparison.OrdinalIgnoreCase) &&
                        !AutonomousAuditedCampPolicy.IsBotExcludedNpc(npc) &&
                        Vector3.DistanceSquared(currentAnchor, new(npc.X, npc.Y, npc.Z)) >= 300 * 300)
                    .OrderBy(npc => bot.GetDistanceTo(npc)).Take(16))
                {
                    Vector3 target = new(npc.X, npc.Y, npc.Z);
                    if (AutonomousZonePointApproach.TryResolve(nav, bot.CurrentZone,
                            new(bot.X, bot.Y, bot.Z), target, 112, out Vector3 approach) &&
                        nav.HasLineOfSight(bot.CurrentZone, approach, target, nav.DefaultFilters) &&
                        AutonomousZoneItinerary.HasCompleteCorridor(nav, bot.CurrentZone, approach, new(bot.X, bot.Y, bot.Z)))
                    {
                        anchor = approach;
                        return true;
                    }
                }
            }
            CampCatalogCell candidate = CampCatalogSnapshot()
                .Where(cell => cell.RegionId == _camp.RegionId && cell.IsDungeon == _camp.IsDungeon &&
                               string.Equals(cell.MonsterName, _camp.MonsterName, StringComparison.OrdinalIgnoreCase) &&
                               Vector2.DistanceSquared(new(bot.X, bot.Y), new(cell.X, cell.Y)) <=
                                   TargetSearchRadius * TargetSearchRadius &&
                               Vector3.DistanceSquared(currentAnchor, new(cell.X, cell.Y, cell.Z)) >= 300 * 300)
                .OrderBy(cell => Vector2.DistanceSquared(new(bot.X, bot.Y), new(cell.X, cell.Y)))
                .ThenBy(cell => cell.Id, StringComparer.Ordinal)
                .FirstOrDefault(cell =>
                    AutonomousDungeonTargetRoute.CanReach(PathfindingProvider.Instance, bot.CurrentZone,
                        new(bot.X, bot.Y, bot.Z), new(cell.X, cell.Y, cell.Z)));
            if (candidate == null)
                return false;
            anchor = new(candidate.X, candidate.Y, candidate.Z);
            return true;
        }

        private static bool IsKeepOrKeepPatrolDestination(CampDestination destination) =>
            destination != null && (destination.Id.StartsWith("rvr-keep-", StringComparison.Ordinal) ||
                                    destination.Id.StartsWith("rvr-roam-", StringComparison.Ordinal));

        private bool StageSoloRvrAtBorderKeep(GameBot bot)
        {
            // Preserve a resumed roamer already beyond its home frontier gate;
            // a restart must not send it all the way back to staging.
            if (_soloRvrBorderStaged || IsInFrontier(bot))
            {
                _soloRvrBorderStaged = true;
                return true;
            }

            // The nearer of the realm's two border keeps, kept for this staging (the staging point belongs to it).
            if (_soloRvrBorderKeep == null && AutonomousRvrStaging.TryGetNearestBorderKeep(bot, out var nearest))
            {
                _soloRvrBorderKeep = nearest;
                Log.Info($"RVR_BORDER_STAGING bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} keep=\"{nearest.Name}\" from={bot.CurrentRegionID}:{bot.X},{bot.Y}");
            }
            if (_soloRvrBorderKeep is not AutonomousRvrStaging.BorderKeep keep)
            {
                SetRvrStatus(bot, "Awaiting a border keep", "Stage safely before frontier roaming",
                    "No realm border-keep contract is configured");
                return false;
            }

            Vector3 destination;
            if (_soloRvrStagingPoint.HasValue)
                destination = _soloRvrStagingPoint.Value;
            else if (TryResolveRvrBorderStaging(keep, out destination, bot.DatabaseID))
                _soloRvrStagingPoint = destination;
            else
            {
                SetRvrStatus(bot, $"Awaiting {keep.Name}", "Stage safely before frontier roaming",
                    "The border keep has no currently validated connected staging floor");
                return false;
            }
            if (bot.CurrentRegionID == keep.RegionId &&
                Distance(bot.X, bot.Y, (int)destination.X, (int)destination.Y) <= 650)
            {
                _soloRvrBorderStaged = true;
                bot.StopMovingOnPath();
                bot.StopMoving();
                SetRvrStatus(bot, $"Staged at {keep.Name}", "Enter the frontier and roam for enemy forces",
                    "Safe border staging complete; selecting a frontier objective");
                return true;
            }

            if (bot.CurrentRegionID != keep.RegionId)
            {
                CampDestination previous = _camp;
                _camp = new($"rvr-stage-{bot.Realm}", keep.Name, keep.Name, keep.RegionId,
                    (int)destination.X, (int)destination.Y, (int)destination.Z, 0, false, false);
                bool moving = TravelAcrossRegions(bot);
                _camp = previous;
                SetRvrStatus(bot, $"Traveling to {keep.Name}", "Stage safely before frontier roaming",
                    "Using the normal collision-safe cross-region route to the realm border keep");
                return moving;
            }

            if (!TryBeginFasterStableRoute(bot, destination, keep.Name))
                IssuePath(bot, destination);
            SetRvrStatus(bot, $"Traveling to {keep.Name}", "Stage safely before frontier roaming",
                "Approaching the safe faction-side border staging point");
            return false;
        }

        private static bool TryResolveRvrBorderStaging(AutonomousRvrStaging.BorderKeep keep, out Vector3 point, long formationKey = 0)
        {
            Region region = WorldMgr.GetRegion(keep.RegionId);
            foreach (Vector3 anchor in AutonomousRvrStaging.CandidateAnchors(keep, formationKey))
            {
                Zone zone = region?.GetZone((int)anchor.X, (int)anchor.Y);
                if (AutonomousRendezvousNavigation.TryChoosePoint(
                        PathfindingProvider.Instance, zone, anchor, out point))
                    return true;
            }
            point = default;
            return false;
        }

        private bool HoldSoloDeathRecovery(GameBot bot)
        {
            bool full = AutonomousRestPolicy.IsFullyRecovered(bot.HealthPercent, bot.ManaPercent,
                bot.EndurancePercent, bot.MaxMana > 0);
            if (full && GameLoop.GameLoopTime >= _soloDeathRecoveryUntilTick)
            {
                _recoverAfterSoloDeath = false;
                bot.WakeRecoveryRest();
                return false;
            }

            bot.StopMovingOnPath();
            bot.StopMoving();
            if (!BotRestRecovery.BlocksRest(bot))
                bot.BeginRecoveryRest();
            SetStatus(bot, "Recovering after release", "Resume the safer solo XP route",
                full ? "Pausing after repeated defeats before another route" :
                $"Waiting for full health, power and endurance: HP {bot.HealthPercent}% • power {bot.ManaPercent}% • endurance {bot.EndurancePercent}%");
            return true;
        }

        private bool HandleCampRecovery(GameBot bot, bool requireFullRecovery = false)
        {
            bool usesPower = bot.MaxMana > 0;
            bool combatBlocked = BotRestRecovery.BlocksRest(bot);
            bool fullyRecovered = AutonomousRestPolicy.IsFullyRecovered(
                bot.HealthPercent, bot.ManaPercent, bot.EndurancePercent, usesPower);
            if (requireFullRecovery && !fullyRecovered && combatBlocked)
            {
                bot.WakeRecoveryRest();
                return true;
            }
            // This method is reached at the camp (or a cleared dungeon rest
            // point), so an outstanding patrol is cancelled when recovery starts.
            bool shouldRest = requireFullRecovery
                ? !combatBlocked && !fullyRecovered
                : AutonomousRestPolicy.ShouldRest(true, false, combatBlocked, bot.IsRecoveryResting,
                    bot.HealthPercent, bot.ManaPercent, bot.EndurancePercent, usesPower);
            if (!shouldRest)
            {
                _restingCamp = null;
                bot.WakeRecoveryRest();
                return false;
            }

            _restingCamp = _camp;
            bot.BeginRecoveryRest();
            SetStatus(bot, "Resting between pulls", GoalText(),
                $"At the live camp: HP {bot.HealthPercent}% • power {bot.ManaPercent}% • endurance {bot.EndurancePercent}%");
            return true;
        }

        private GameNPC FindCampTarget(GameBot bot)
        {
            bool savage = SavageBotCombatPolicy.MustMeleePull((eCharacterClass)bot.CharacterClass.ID);
            bool soloSavage = SavageBotCombatPolicy.NeedsVerifiedSoloPullRoute(
                (eCharacterClass)bot.CharacterClass.ID, _groupDirective?.IsDynamic == true,
                bot.CurrentZone?.IsDungeon == true || _camp.IsDungeon,
                AutonomousAuditedCampPolicy.RequiresVerifiedTargetRoute(_camp.RegionId, _camp.MonsterName) ||
                    IsSourceEmptyCamp(_camp.Id));
            long nowTick = GameLoop.GameLoopTime;
            if (nowTick >= _nextFailedSavageMeleePullPruneTick)
            {
                foreach (ushort id in _failedSavageMeleePullTargets
                    .Where(pair => pair.Value <= nowTick).Select(pair => pair.Key).ToArray())
                    _failedSavageMeleePullTargets.Remove(id);
                _nextFailedSavageMeleePullPruneTick = nowTick + 10_000;
            }
            if (soloSavage && nowTick >= _nextFailedSoloPullRoutePruneTick)
            {
                foreach (ushort id in _failedSoloPullRoutes
                    .Where(pair => nowTick < pair.Value.FailedAtTick ||
                        nowTick - pair.Value.FailedAtTick >=
                            SavageBotCombatPolicy.FailedSoloPullRouteRetryMilliseconds)
                    .Select(pair => pair.Key).ToArray())
                    _failedSoloPullRoutes.Remove(id);
                _nextFailedSoloPullRoutePruneTick = nowTick + 10_000;
            }
            GameNPC FindWithin(ushort radius) => bot.GetNPCsInRadius(radius)
                .Where(npc => IsExperienceMonster(npc) && npc.IsAlive && npc.CurrentRegionID == _camp.RegionId &&
                    !AutonomousAuditedCampPolicy.IsBotExcludedNpc(npc) &&
                    !AutonomousPveTargetPolicy.IsUnreachableFlyer(npc.Flags, npc.Z, bot.Z))
                .Where(npc => _camp.RegionId != AutonomousDarknessFallsPolicy.RegionId ||
                    AutonomousDarknessFallsNavigation.TryGetProof(npc.InternalID, out _))
                .Where(npc => _camp.RegionId != AutonomousDarknessFallsPolicy.RegionId ||
                    AutonomousDarknessFallsNavigation.IsInAssignedRoom(
                        new(_camp.X, _camp.Y, _camp.Z), new(npc.X, npc.Y, npc.Z)))
                // Planning owns level selection. Once assigned, neither solo
                // nor group execution may reject that named monster by level.
                .Where(npc => AutonomousPveTargetPolicy.IsAssignedTarget(
                    _camp.MonsterName, npc.Name, npc.EffectiveLevel))
                // Level 50 solo bots never pull below the solo camp floor (no waiting on a
                // rare green among greys: with none left the empty-camp timeout moves on).
                .Where(npc => _groupDirective?.IsDynamic == true ||
                    AutonomousSoloCampFloor.Allows(bot.Level, npc.EffectiveLevel))
                .Where(npc => !_failedSavageMeleePullTargets.TryGetValue(npc.ObjectID,
                    out long retryTick) || retryTick <= nowTick)
                // Ordinary outdoor Savages use the same live-target search as
                // other melee classes. Only dungeons and audited risky camps
                // require the extra reversible approach proof below.
                .Where(npc => GameServer.ServerRules.IsAllowedToAttack(bot, npc, true))
                .OrderByDescending(npc => _groupDirective?.IsDynamic == true ? npc.EffectiveLevel : 0)
                .ThenBy(npc => bot.GetDistanceTo(npc) + Math.Abs((int)ConLevels.GetConColor(bot.GetConLevel(npc))) * 220)
                // Run native checks only after cheap filters and sorting, and
                // stop at the first reachable target. Self-defense is handled
                // separately and must never be suppressed by this pull gate.
                .FirstOrDefault(npc =>
                {
                    // Dungeon line of sight is a pure navmesh raycast; testing
                    // it here (in sorted order) picks the same first target as
                    // filtering every candidate before the sort, with fewer casts.
                    if (bot.CurrentZone?.IsDungeon == true &&
                        !PathfindingProvider.Instance.HasLineOfSight(bot.CurrentZone, new(bot.X, bot.Y, bot.Z),
                            new(npc.X, npc.Y, npc.Z), PathfindingProvider.Instance.DefaultFilters))
                        return false;

                    bool verifyRoute = soloSavage || bot.CurrentZone?.IsDungeon == true ||
                        AutonomousAuditedCampPolicy.RequiresVerifiedTargetRoute(
                            npc.CurrentRegionID, npc.Name) ||
                        IsSourceEmptyCamp(_camp?.Id);
                    if (!verifyRoute)
                        return true;

                    Vector3 origin = new(bot.X, bot.Y, bot.Z);
                    Vector3 target = new(npc.X, npc.Y, npc.Z);
                    int originZoneId = bot.CurrentZone?.ID ?? 0;
                    int targetZoneId = npc.CurrentZone?.ID ?? 0;
                    if (soloSavage && _failedSoloPullRoutes.TryGetValue(npc.ObjectID, out var failure) &&
                        SavageBotCombatPolicy.ShouldDelayFailedSoloPullRetry(failure,
                            nowTick, bot.CurrentRegionID, originZoneId, targetZoneId, origin, target))
                        return false;

                    bool reachable = AutonomousDungeonTargetRoute.CanReach(
                        PathfindingProvider.Instance, bot.CurrentZone, origin, target);
                    if (!reachable && (npc.Flags & GameNPC.eFlags.FLYING) != 0 &&
                        AutonomousDefensivePull.CanAttemptFlyingHandoff(bot, npc))
                        reachable = true;
                    if (soloSavage)
                    {
                        if (reachable)
                            _failedSoloPullRoutes.Remove(npc.ObjectID);
                        else
                            _failedSoloPullRoutes[npc.ObjectID] = new(nowTick, bot.CurrentRegionID,
                                originZoneId, targetZoneId, origin, target);
                    }
                    return reachable;
                });

            // Preserve the old cheap local lookup for ordinary pulls. Only an
            // empty local ring pays for the requested 5,000-unit expansion.
            return FindWithin(ImmediateTargetSearchRadius) ?? FindWithin(TargetSearchRadius);
        }

        private void PatrolCamp(GameBot bot)
        {
            Vector3 current = new(bot.X, bot.Y, bot.Z);
            if (!_patrolDestination.HasValue || Vector3.DistanceSquared(current, _patrolDestination.Value) < 120 * 120)
            {
                if (_camp.IsDungeon)
                {
                    // Random points inside a radius can belong to another room
                    // or floor. Solo search follows the same proven same-goal
                    // corridor as a group; group anchor changes are coordinated
                    // by TryRepositionEmptyCamp after its existing wait period.
                    if (_groupDirective?.IsDynamic == true || !TryFindSameGoalSearchAnchor(bot, out Vector3 next))
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                        return;
                    }
                    _patrolDestination = next;
                    _camp = _camp with { X = (int)Math.Round(next.X), Y = (int)Math.Round(next.Y), Z = (int)Math.Round(next.Z) };
                    _emptyCampSinceTick = 0;
                    _nextMoveOrderTick = 0;
                    IssuePath(bot, next);
                    return;
                }
                Vector3 center = new(_camp.X, _camp.Y, _camp.Z);
                Vector3? random = PathfindingProvider.Instance.GetRandomPoint(
                    bot.CurrentZone,
                    center,
                    850,
                    PathfindingProvider.Instance.DefaultFilters);
                _patrolDestination = random ?? center;
                _nextMoveOrderTick = 0;
            }
            IssuePath(bot, _patrolDestination.Value);
        }

        private void SelectCamp(GameBot bot)
        {
            if (_groupDirective?.IsDynamic == true && _groupDirective.Leader == bot &&
                AutonomousRealmRaid.TryJoin(bot, out var raid))
            {
                _camp = FromSharedCamp(raid.Camp);
                return;
            }
            if (!_hasCampCatalog)
            {
                _nextPlanTick = GameLoop.GameLoopTime + 250;
                SetStatus(bot, "Preparing live XP camps", "Find a reachable level-appropriate XP camp",
                    "Waiting for the first complete camp catalog; combat defense remains active");
                return;
            }
            _nextPlanTick = GameLoop.GameLoopTime + 30_000 + bot.ObjectID % 8_000;
            int groupSize = Math.Max(1, (int)(bot.Group?.MemberCount ?? 1));
            bool sharedGroup = _groupDirective?.IsDynamic == true;
            int planningLevel = sharedGroup ? _groupDirective.AverageLevel : bot.Level;
            int highestMemberLevel = sharedGroup
                ? bot.Group.GetMembersInTheGroup().Max(member => member.EffectiveLevel) : bot.EffectiveLevel;
            ConColor maximumTargetCon = MaximumTargetCon(groupSize);
            ConColor naturalMaximumTargetCon = NaturalMaximumTargetCon(groupSize);
            // Normal groups start at yellow-or-better, but every death retry is
            // allowed to step down through green. Grey creatures are rejected
            // when candidates are built and can never become a recovery goal.
            ConColor minimumTargetCon = _deathDifficultySteps > 0 || (_groupDirective?.WipePenalty ?? 0) > 0
                ? ConColor.GREEN
                : groupSize >= 2 ? ConColor.YELLOW : ConColor.GREEN;
            // A battleground has no zone connections; plan from the home region (the bot leaves by the exit)
            // and add the battleground of the bot's bracket when the whole party fits it (level only).
            HashSet<ushort> reachableRegions = ReachableRegions(bot.Realm,
                BattlegroundBrackets.IsBattlegroundRegion(bot.CurrentRegionID) ? RealmRaidNeutralEvents.HomeRegion(bot.Realm) : bot.CurrentRegionID);
            BattlegroundBrackets.Bracket campBattleground = AutonomousTownTeleporters.IsEnabled ? BattlegroundBrackets.ForLevel(bot.Level) : null;
            if (campBattleground != null && (!sharedGroup || bot.Group == null ||
                    bot.Group.GetMembersInTheGroup().All(member => BattlegroundBrackets.Allows(campBattleground, member.Level))))
                reachableRegions.Add(campBattleground.RegionId);
            HashSet<string> rejectedDungeons = AutonomousBotGroupCoordinator.RejectedDungeonCamps(bot);
            foreach (string id in _rejectedDungeonCamps.Where(pair => pair.Value <= GameLoop.GameLoopTime).Select(pair => pair.Key).ToArray())
                _rejectedDungeonCamps.Remove(id);
            foreach (string id in _recentFailedSoloCamps.Where(pair => pair.Value <= GameLoop.GameLoopTime).Select(pair => pair.Key).ToArray())
                _recentFailedSoloCamps.Remove(id);
            foreach (string name in _recentFailedSoloTargets.Where(pair => pair.Value <= GameLoop.GameLoopTime).Select(pair => pair.Key).ToArray())
                _recentFailedSoloTargets.Remove(name);
            rejectedDungeons.UnionWith(_rejectedDungeonCamps.Keys);
            Dictionary<string, CampDestination> destinations = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, (eRealm Wing, float Distance)> darknessFallsRoutes = new(StringComparer.OrdinalIgnoreCase);
            List<AutonomousBotDecisionEngine.Camp> camps = new();

            // The live world used to be regrouped by every individual bot.
            // With a large roster that meant thousands of full region/object
            // scans and navmesh random-point queries. One short-lived shared
            // catalog keeps actors real and live while making each bot's
            // realm/level/death filtering a cheap in-memory operation.
            foreach (CampCatalogCell cell in CampCatalogSnapshot()
                         .Where(cell => !rejectedDungeons.Contains(cell.Id) && reachableRegions.Contains(cell.RegionId) &&
                                        (sharedGroup || !_recentFailedSoloCamps.ContainsKey(cell.Id)) &&
                                        !AutonomousCampRouteQuarantine.IsQuarantined(cell.Id) &&
                                        (sharedGroup || !AutonomousCampDangerBench.IsBenched(cell.Id)) &&
                                        IsZoneAccessible(bot.Realm, cell.Zone, bot.CurrentRegionID) &&
                                        AutonomousOrdinaryPveRealmPolicy.CanAssignCamp(bot.Realm, cell.RegionId,
                                            cell.Zone?.ID ?? 0) &&
                                        AutonomousAuditedCampPolicy.CanAssignToParty(cell.Id, groupSize)))
            {
                int[] validLevels = cell.Levels.Where(level =>
                    {
                        if (sharedGroup)
                            return level >= planningLevel + 3 &&
                                level <= planningLevel + _groupDirective.PreferredLevelBonus &&
                                ConLevels.GetConColor(ConLevels.GetConLevel(highestMemberLevel, level)) > ConColor.GREY;
                        ConColor con = ConLevels.GetConColor(ConLevels.GetConLevel(bot.EffectiveLevel, level));
                        // Retain every naturally valid non-grey option in the
                        // catalog. The requested death ceiling is applied below,
                        // allowing a safe fallback when that ceiling has no
                        // XP-bearing creature at this level.
                        return con >= ConColor.GREEN && con <= naturalMaximumTargetCon &&
                            AutonomousSoloCampFloor.Allows(bot.Level, level);
                    })
                    .ToArray();
                if (validLevels.Length == 0 ||
                    !sharedGroup && !AutonomousSoloCampFloor.CampQualifies(bot.Level, validLevels.Length, cell.Levels.Length))
                    continue;

                // Solo bots keep Darkness Falls, but only once they can survive
                // it: level 25+ and targets that con blue or easier. (Solo
                // level 15-24 bots died there more than they killed.)
                if (cell.RegionId == AutonomousDarknessFallsPolicy.RegionId && !sharedGroup)
                {
                    if (bot.Level < AutonomousDarknessFallsPolicy.SoloMinimumLevel)
                        continue;
                    validLevels = validLevels.Where(level => ConLevels.GetConColor(
                        ConLevels.GetConLevel(bot.EffectiveLevel, level)) <= AutonomousDarknessFallsPolicy.SoloMaximumCon).ToArray();
                    if (validLevels.Length == 0)
                        continue;
                }

                if (cell.RegionId == AutonomousDarknessFallsPolicy.RegionId)
                {
                    float entryDistance = bot.Realm switch
                    {
                        eRealm.Albion => cell.DarknessFallsAlbionDistance,
                        eRealm.Midgard => cell.DarknessFallsMidgardDistance,
                        eRealm.Hibernia => cell.DarknessFallsHiberniaDistance,
                        _ => float.PositiveInfinity
                    };
                    float nearestEntranceDistance = MathF.Min(cell.DarknessFallsAlbionDistance,
                        MathF.Min(cell.DarknessFallsMidgardDistance, cell.DarknessFallsHiberniaDistance));
                    if (!AutonomousDarknessFallsNavigation.CanUseOrdinaryCamp(bot.Realm,
                            cell.DarknessFallsWing, validLevels.Min(), entryDistance,
                            nearestEntranceDistance)) continue;
                    darknessFallsRoutes[cell.Id] = (cell.DarknessFallsWing, entryDistance);
                }

                ConColor[] cons = validLevels
                    .Select(level => ConLevels.GetConColor(ConLevels.GetConLevel(bot.EffectiveLevel, level)))
                    .OrderBy(con => con)
                    .ToArray();
                int averageLevel = sharedGroup
                    ? AutonomousGroupTargetPolicy.SelectFixedEightManLevel(validLevels, planningLevel,
                        _groupDirective.PreferredLevelBonus)
                    : (int)Math.Round(validLevels.Average());

                destinations[cell.Id] = new(cell.Id, cell.MonsterName, cell.ZoneName, cell.RegionId,
                    cell.X, cell.Y, cell.Z, cell.LiveMobCount, cell.IsDungeon, cell.IsFrontier);
                camps.Add(new(
                    cell.Id,
                    cell.ZoneName,
                    cell.MonsterName,
                    bot.Realm,
                    cell.RegionId,
                    cons[0],
                    cons[cons.Length / 2],
                    true,
                    cell.IsDungeon,
                    cell.IsFrontier,
                    cell.LiveMobCount,
                    string.Equals(cell.Id, bot.PersistentRecord?.CurrentCampId, StringComparison.OrdinalIgnoreCase) ? bot.PersistentRecord.DeathCount : 0,
                    0,
                    averageLevel,
                    cell.IsDungeon ? AutonomousDungeonPopulationPolicy.Population(cell.RegionId) : 0,
                    cell.IsDungeon ? AutonomousDungeonPopulationPolicy.Capacity(cell.RegionId, cell.LiveMobCount) : 0));
            }

            // Once realm/accessibility/level/con filters have been applied, each
            // distinct location is drawn in proportion to its live spawns (capped).
            // Distance, local density, and monster-name scoring may not pull a
            // population into one nearby camp.
            IEnumerable<AutonomousBotDecisionEngine.Camp> legal = camps.Where(camp =>
                camp.Reachable && camp.Realm == bot.Realm && camp.LiveMobCount > 0);
            AutonomousBotDecisionEngine.PveEnvironment environment;
            if (sharedGroup)
            {
                // A party already inside Darkness Falls chooses its next camp
                // there when one fits. Walking a whole group back out through
                // the DF corridors to an outdoor camp failed repeatedly and
                // split parties (corpses outside, healers inside).
                GameBot partyLeader = _groupDirective.Leader ?? bot;
                if (partyLeader.CurrentRegionID == AutonomousDarknessFallsPolicy.RegionId)
                {
                    AutonomousBotDecisionEngine.Camp[] inside = legal
                        .Where(camp => camp.RegionId == AutonomousDarknessFallsPolicy.RegionId).ToArray();
                    if (inside.Length > 0)
                        legal = inside;
                }
                // Pick dungeon versus outdoor while every valid group level is
                // still present. Selecting the exact level first could remove
                // every dungeon candidate before the preference was rolled.
                AutonomousBotDecisionEngine.Camp[] categoryCandidates = legal.ToArray();
                environment = AutonomousBotDecisionEngine.SelectPveEnvironment(
                    categoryCandidates, groupSize, planningLevel, Random.Shared);
                legal = categoryCandidates.Where(camp => environment == AutonomousBotDecisionEngine.PveEnvironment.Dungeon
                    ? camp.IsDungeon : !camp.IsDungeon);
                GameBot groupLeader = _groupDirective.Leader ?? bot;
                legal = AutonomousDungeonPolicy.PreferFirstCursedTombGroupCamps(legal,
                    formedGroup: true,
                    hasInteriorProgress: !AutonomousDungeonPolicy.IsCursedTombEntryStage(
                        groupLeader.CurrentRegionID, new Vector3(groupLeader.X, groupLeader.Y, groupLeader.Z)),
                    region: camp => camp.RegionId,
                    position: camp => new Vector3(destinations[camp.Id].X,
                        destinations[camp.Id].Y, destinations[camp.Id].Z));
                int selectedLevel = AutonomousGroupTargetPolicy.SelectFixedEightManLevel(
                    legal.Select(camp => camp.AverageMobLevel), planningLevel,
                    _groupDirective.PreferredLevelBonus);
                legal = legal.Where(camp => camp.AverageMobLevel == selectedLevel);
            }
            else
            {
                legal = legal.Where(camp => camp.LowestCon >= minimumTargetCon && camp.TypicalCon <= maximumTargetCon);
                // A bot grinding while it waits for group matchmaking stays
                // outdoors: recruitment needs it in an overworld region, and a
                // dungeon camp would only be abandoned when the party forms.
                if (AutonomousObjectiveAssignments.IsAwaitingGroupMatchmaking(bot))
                    legal = legal.Where(camp => !camp.IsDungeon);
                legal = AutonomousDeathRecoveryPolicy.PreferFreshTargets(
                    legal, _recentFailedSoloTargets, GameLoop.GameLoopTime);
                AutonomousBotDecisionEngine.Camp[] categoryCandidates = legal.ToArray();
                environment = AutonomousBotDecisionEngine.SelectPveEnvironment(
                    categoryCandidates, groupSize, planningLevel, Random.Shared);
                legal = categoryCandidates.Where(camp => environment == AutonomousBotDecisionEngine.PveEnvironment.Dungeon
                    ? camp.IsDungeon : !camp.IsDungeon);
            }
            AutonomousBotDecisionEngine.Camp[] legalCells = legal.ToArray();
            if (darknessFallsRoutes.Count > 0 &&
                legalCells.Any(camp => camp.RegionId == AutonomousDarknessFallsPolicy.RegionId))
            {
                // Only DF changes its camp weighting: first keep own-wing
                // copies of a same-name monster, then choose nearby verified
                // rooms by actual navigated distance from this realm's entry.
                // All other dungeons retain their existing uniform draw.
                var df = AutonomousDarknessFallsNavigation.PreferEntranceSide(
                    legalCells.Where(camp => camp.RegionId == AutonomousDarknessFallsPolicy.RegionId),
                    bot.Realm, camp => camp.MonsterName, camp => camp.AverageMobLevel,
                    camp => darknessFallsRoutes[camp.Id].Wing,
                    camp => darknessFallsRoutes[camp.Id].Distance);
                legalCells = legalCells.Where(camp => camp.RegionId != AutonomousDarknessFallsPolicy.RegionId)
                    .Concat(df).ToArray();
            }
            // This is the deployed selection point. Each distinct verified
            // location has one entry; outdoor draws are weighted by live spawns
            // (capped) with no distance or name preference.
            AutonomousBotDecisionEngine.Camp chosen = AutonomousBotDecisionEngine.SelectWithinEnvironment(
                legalCells, environment, Random.Shared);
            bool usedDeathFallback = false;
            if (chosen == null && !sharedGroup && _deathDifficultySteps > 0)
            {
                chosen = AutonomousBotDecisionEngine.SelectSafestAvailableAfterDeath(
                    AutonomousDeathRecoveryPolicy.PreferFreshTargets(
                        camps.Where(camp => camp.TypicalCon <= naturalMaximumTargetCon),
                        _recentFailedSoloTargets, GameLoop.GameLoopTime),
                    _lastFailedCampId,
                    _lastFailedTargetName,
                    Random.Shared);
                usedDeathFallback = chosen != null;
                if (usedDeathFallback)
                {
                    Log.Warn($"AUTONOMOUS_DEATH_TARGET_FALLBACK bot={bot.Name} id={bot.DatabaseID} " +
                             $"level={bot.Level} requested_max={maximumTargetCon} available_con={chosen.TypicalCon} " +
                             $"failed_target=\"{_lastFailedTargetName}\" selected=\"{chosen.MonsterName}\" camp={chosen.Id}");
                }
            }
            AutonomousBotDecisionEngine.Decision decision = chosen == null
                ? new(eAutonomousActivity.Travel, string.Empty, "No reachable level-valid XP-bearing spawn cell currently matches the objective.")
                : new(chosen.IsDungeon ? eAutonomousActivity.Dungeon : sharedGroup ? eAutonomousActivity.GroupGrind : eAutonomousActivity.Grind,
                    chosen.Id, usedDeathFallback
                        ? $"No {maximumTargetCon.ToString().ToLowerInvariant()}-or-easier XP camp exists; selected the safest available non-grey alternative."
                        : $"Selected a live {environment.ToString().ToLowerInvariant()} camp from {legalCells.Length:N0} reachable level-valid spawn cells.");
            if (!destinations.TryGetValue(decision.TargetId, out CampDestination selected))
                return;

            // A level-one bot cannot find a green XP mob. If planning selected
            // the safest available yellow instead, permit that same con here
            // for this camp only; never silently reject it as an empty camp.
            _camp = sharedGroup ? selected with { TargetLevel = chosen.AverageMobLevel }
                : usedDeathFallback ? selected with { FallbackMaximumCon = chosen.TypicalCon } : selected;
            BeginCampDiagnostics(bot);
            _lastFailedCampId = string.Empty;
            _lastFailedTargetName = string.Empty;
            _campStartedTick = GameLoop.GameLoopTime;
            _emptyCampSinceTick = 0;
            _patrolDestination = null;
            _pendingStableChoice = null;
            bot.PersistentRecord.CurrentCampId = selected.Id;
            SetStatus(bot, $"Planning route to {selected.MonsterName}", GoalText(), decision.Reason,
                selected.MonsterName, selected.ZoneName, true);
        }

        private void BeginCampDiagnostics(GameBot bot)
        {
            try
            {
            if (!AutonomousDiagnosticsProperties.GoalAttempts || _camp == null)
                return;
            GameLiving[] members = bot.Group?.GetMembersInTheGroup().ToArray() ?? [bot];
            if (members.Length == 0) members = [bot];
            AutonomousGoalDiagnostics.Begin(bot, _camp.Id, _camp.MonsterName, _camp.ZoneName,
                _camp.RegionId, _camp.X, _camp.Y, _camp.Z, _groupDirective?.GroupId ?? string.Empty,
                members.Length, members.Average(member => member.Level),
                string.Join(",", members.Select(member => member is GameBot other ? other.ClassName : "Player")));
            }
            catch { } // Observability must never replan a working route.
        }

        private static CampCatalogCell[] CampCatalogSnapshot()
        {
            return Volatile.Read(ref _campCatalog);
        }

        public const int StarterCampMaximumMobLevel = 1;
        // First live run: 109 level 1-3 bots used 2-spawn camps and most goals
        // ended "no live wolf nipper remained". Fewer, fuller camps (ordinary
        // 4,200-unit cell, at least three spawns) hold a small crowd.
        private const int StarterCampMinimumSpawns = 3;
        internal const string StarterCampPrefix = "starter-live:";

        /// <summary>
        /// The authored catalogs (CapnBry, restored 1.65 spawns, audited
        /// anchors) contain almost no level 0-1 creatures, so a level 1 bot
        /// found nothing blue and was sent to yellow targets it could not beat.
        /// Real starter creatures around every home town (wolf nippers,
        /// green snakes, badger cubs, ...) become small live camps here, only
        /// where no authored camp already covers that creature nearby. Each
        /// anchor is an actual live spawn; the projection step then requires a
        /// walkable navmesh point and a complete corridor from the zone's
        /// stable master before the camp is published.
        /// </summary>
        private static void AddStarterCamps(List<CampCatalogCell> cells, CampMonster[] snapshot)
        {
            var covered = cells
                .GroupBy(cell => (cell.Zone?.ID ?? 0, cell.MonsterName.Trim().ToLowerInvariant()))
                .ToDictionary(group => group.Key, group => group.ToArray());
            foreach (var group in snapshot
                         .Where(npc => npc.EffectiveLevel <= StarterCampMaximumMobLevel &&
                                       npc.CurrentZone?.IsDungeon != true &&
                                       npc.CurrentZone?.ZoneRegion?.IsDungeon != true &&
                                       !IsFrontierZone(npc.CurrentRegionID, npc.CurrentZone.ID))
                         .GroupBy(npc => (npc.CurrentZone.ID, npc.CurrentRegionID,
                             CellX: npc.X / CampCellSize, CellY: npc.Y / CampCellSize,
                             Name: npc.Name.Trim().ToLowerInvariant())))
            {
                CampMonster[] members = group.ToArray();
                if (members.Length < StarterCampMinimumSpawns)
                    continue;
                float averageX = (float)members.Average(npc => npc.X);
                float averageY = (float)members.Average(npc => npc.Y);
                CampMonster representative = members
                    .OrderBy(npc => Math.Abs(npc.X - averageX) + Math.Abs(npc.Y - averageY))
                    .ThenBy(npc => npc.InternalId, StringComparer.Ordinal)
                    .First();
                if (covered.TryGetValue((group.Key.ID, group.Key.Name), out CampCatalogCell[] existing) &&
                    existing.Any(cell => DistanceSquared(cell.X, cell.Y, representative.X, representative.Y) <=
                        TargetSearchRadius * TargetSearchRadius))
                    continue;
                cells.Add(new CampCatalogCell(
                    $"{StarterCampPrefix}{group.Key.ID}:{group.Key.CurrentRegionID}:{group.Key.CellX}:{group.Key.CellY}:{group.Key.Name}",
                    representative.Name,
                    representative.CurrentZone.Description ?? representative.CurrentZone.ZoneRegion?.Description ?? $"region {group.Key.CurrentRegionID}",
                    group.Key.CurrentRegionID,
                    representative.X, representative.Y, representative.Z,
                    members.Select(npc => npc.EffectiveLevel).OrderBy(level => level).ToArray(),
                    members.Length, representative.CurrentZone, false, false));
            }
        }

        private static CampCatalogCell[] BuildCampCatalogDraft(CampMonster[] snapshot)
        {
                var cells = new List<CampCatalogCell>();
                Dictionary<(ushort ZoneId, string Name), CampMonster[]> liveByZoneAndName = snapshot
                    .GroupBy(npc => (npc.CurrentZone.ID, npc.Name.Trim().ToLowerInvariant()))
                    .ToDictionary(group => group.Key, group => group.ToArray());

                // These four audited camps advertised historical levels and
                // overlapping empty anchors, sometimes miles from a valid kill.
                // Use one real, level-specific spawn cluster; never change mobs.
                foreach (var cluster in snapshot.Where(npc =>
                             AutonomousAuditedCampPolicy.UsesLiveAnchor(npc.CurrentRegionID, npc.Name))
                         .GroupBy(npc => (npc.CurrentZone.ID, npc.Name, npc.EffectiveLevel,
                             CellX: npc.X / 1500, CellY: npc.Y / 1500)))
                {
                    CampMonster anchor = cluster.OrderBy(npc => npc.InternalId, StringComparer.Ordinal).First();
                    cells.Add(new($"audited-live:{cluster.Key.ID}:{cluster.Key.Name}:{cluster.Key.EffectiveLevel}:{cluster.Key.CellX}:{cluster.Key.CellY}",
                        anchor.Name, anchor.CurrentZone.Description, anchor.CurrentRegionID,
                        anchor.X, anchor.Y, anchor.Z, [anchor.EffectiveLevel], cluster.Count(),
                        anchor.CurrentZone, false, IsFrontierZone(anchor.CurrentRegionID, anchor.CurrentZone.ID)));
                }

                Dictionary<(ushort ZoneId, string Name, int CellX, int CellY), CampMonster[]> restoredGroups = snapshot
                    .Where(npc => !AutonomousAuditedCampPolicy.UsesLiveAnchor(npc.CurrentRegionID, npc.Name) &&
                                  AutonomousClassic165RestoredSpawnCatalog.Contains(npc.InternalId) &&
                                  npc.CurrentZone.IsDungeon != true && npc.CurrentZone.ZoneRegion?.IsDungeon != true)
                    .GroupBy(npc => (npc.CurrentZone.ID, npc.Name.Trim().ToLowerInvariant(),
                        npc.X / CampCellSize, npc.Y / CampCellSize))
                    .ToDictionary(group => group.Key, group => group.ToArray());
                Dictionary<(ushort ZoneId, string Name), CampMonster[]> restoredByZoneAndName = restoredGroups
                    .SelectMany(pair => pair.Value)
                    .GroupBy(npc => (npc.CurrentZone.ID, npc.Name.Trim().ToLowerInvariant()))
                    .ToDictionary(group => group.Key, group => group.ToArray());

                // These coordinates survived both period-source correlation and
                // an installed-navmesh route audit. Prefer a real representative
                // NPC over a cell average, which could fall between two walkable
                // surfaces. The ordinary live target requirement still applies.
                foreach (((ushort zoneId, string name, int cellX, int cellY), CampMonster[] members) in restoredGroups)
                {
                    CampMonster representative = members[0];
                    cells.Add(new CampCatalogCell(
                        $"classic165-restored:{zoneId}:{cellX}:{cellY}:{name}",
                        representative.Name,
                        representative.CurrentZone.Description ?? representative.CurrentZone.ZoneRegion?.Description ?? $"region {representative.CurrentRegionID}",
                        representative.CurrentRegionID,
                        representative.X, representative.Y, representative.Z,
                        members.Select(npc => npc.EffectiveLevel).OrderBy(level => level).ToArray(),
                        members.Length, representative.CurrentZone,
                        representative.CurrentZone.ZoneRegion?.IsDungeon == true || representative.CurrentZone.IsDungeon,
                        IsFrontierZone(representative.CurrentRegionID, representative.CurrentZone.ID)));
                }

                // Battleground monsters (goal 9): live clusters, offered only to bots whose level fits that
                // battleground's bracket (SelectCamp adds the region only for them).
                foreach (var cluster in snapshot.Where(npc => BattlegroundBrackets.IsBattlegroundRegion(npc.CurrentRegionID))
                             .GroupBy(npc => (npc.CurrentZone.ID, Name: npc.Name.Trim().ToLowerInvariant(),
                                 CellX: npc.X / CampCellSize, CellY: npc.Y / CampCellSize)))
                {
                    CampMonster representative = cluster.OrderBy(npc => npc.InternalId, StringComparer.Ordinal).First();
                    cells.Add(new CampCatalogCell(
                        $"battleground-live:{cluster.Key.ID}:{cluster.Key.CellX}:{cluster.Key.CellY}:{cluster.Key.Name}",
                        representative.Name, representative.CurrentZone.Description ?? $"region {representative.CurrentRegionID}",
                        representative.CurrentRegionID, representative.X, representative.Y, representative.Z,
                        cluster.Select(npc => npc.EffectiveLevel).OrderBy(level => level).ToArray(),
                        cluster.Count(), representative.CurrentZone, false, true));
                }

                foreach (AutonomousCapnBryGoalCatalog.Entry entry in AutonomousCapnBryGoalCatalog.Entries)
                {
                    if (AutonomousAuditedCampPolicy.UsesLiveAnchor(entry.RegionId, entry.Name)) continue;
                    Zone zone = WorldMgr.GetZone(entry.ZoneId);
                    if (zone?.IsDungeon == true) continue; // Audited dungeon corridors are added below.
                    if (zone?.ZoneRegion == null || zone.ZoneRegion.ID != entry.RegionId ||
                        !AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(zone.ZoneRegion.Expansion))
                        continue;
                    if (!liveByZoneAndName.TryGetValue((entry.ZoneId, entry.Name.Trim().ToLowerInvariant()), out CampMonster[] candidates))
                        continue;

                    int authoritativeX = zone.XOffset + entry.LocalX;
                    int authoritativeY = zone.YOffset + entry.LocalY;
                    if (restoredByZoneAndName.TryGetValue((entry.ZoneId,
                        entry.Name.Trim().ToLowerInvariant()), out CampMonster[] restoredMembers) &&
                        restoredMembers.Any(npc => DistanceSquared(npc.X, npc.Y, authoritativeX, authoritativeY) <=
                            TargetSearchRadius * TargetSearchRadius))
                        continue;
                    CampMonster[] members = candidates.Where(npc =>
                            DistanceSquared(npc.X, npc.Y, authoritativeX, authoritativeY) <= TargetSearchRadius * TargetSearchRadius)
                        .ToArray();
                    if (members.Length == 0)
                        continue;

                    // A source name/location can be period-correct while its
                    // historic level does not match any present live spawn.
                    // Advertising that absent level makes a bot arrive at a
                    // populated camp yet see zero valid targets forever.
                    int[] observedLevels = AutonomousCampLevelPolicy.ObservedAuthoredLevels(
                        entry.Levels, members.Select(npc => npc.EffectiveLevel));
                    if (observedLevels.Length == 0)
                        continue;

                    int x = authoritativeX;
                    int y = authoritativeY;
                    int z = entry.Z;
                    cells.Add(new CampCatalogCell(entry.Id, entry.Name, entry.Zone, entry.RegionId,
                        x, y, z, observedLevels, members.Length, zone,
                        zone.ZoneRegion.IsDungeon || zone.IsDungeon,
                        IsFrontierZone(entry.RegionId, entry.ZoneId)));
                }

                // CapnBry has completely empty bestiary pages for a finite set
                // of otherwise compatible Classic/SI zones (notably all
                // Hibernia SI maps). Preserve the old live-spawn behavior only
                // for those explicitly source-empty zones. A local coordinate
                // can never override a CapnBry coordinate in a covered zone.
                foreach (IGrouping<(ushort ZoneId, ushort RegionId, int CellX, int CellY, string Name, int Level), CampMonster> group in
                         liveByZoneAndName
                             .Where(pair => AutonomousCapnBryGoalCatalog.SourceEmptySupportedZoneIds.Contains(pair.Key.ZoneId))
                             .SelectMany(pair => pair.Value)
                             .Where(npc => npc.CurrentZone?.IsDungeon != true)
                             .GroupBy(npc => (npc.CurrentZone.ID, npc.CurrentRegionID,
                                 npc.X / SourceEmptyCampCellSize, npc.Y / SourceEmptyCampCellSize,
                                 npc.Name.Trim().ToLowerInvariant(), npc.EffectiveLevel)))
                {
                    CampMonster[] members = group.ToArray();
                    // Pick an actual live NPC nearest the cluster centroid.
                    // Never publish the arithmetic average as a destination:
                    // that point can be between disconnected spawn surfaces.
                    float averageX = (float)members.Average(npc => npc.X);
                    float averageY = (float)members.Average(npc => npc.Y);
                    CampMonster representative = members
                        .OrderBy(npc => Math.Abs(npc.X - averageX) + Math.Abs(npc.Y - averageY))
                        .ThenBy(npc => npc.InternalId, StringComparer.Ordinal)
                        .First();
                    bool overlapsRestoredCell = restoredGroups.Keys.Any(key =>
                        key.ZoneId == representative.CurrentZone.ID &&
                        string.Equals(key.Name, group.Key.Name, StringComparison.OrdinalIgnoreCase) &&
                        key.CellX == representative.X / CampCellSize &&
                        key.CellY == representative.Y / CampCellSize);
                    if (overlapsRestoredCell)
                        continue;
                    string id = $"capnbry-source-empty:{group.Key.ZoneId}:{group.Key.RegionId}:{group.Key.CellX}:{group.Key.CellY}:{group.Key.Name}:{group.Key.Level}";
                    cells.Add(new CampCatalogCell(id, group.Key.Name,
                        representative.CurrentZone.Description ?? representative.CurrentZone.ZoneRegion?.Description ?? $"region {group.Key.RegionId}",
                        group.Key.RegionId,
                        representative.X, representative.Y, representative.Z,
                        members.Select(npc => npc.EffectiveLevel).OrderBy(level => level).ToArray(),
                        members.Length, representative.CurrentZone,
                        representative.CurrentZone.ZoneRegion?.IsDungeon == true || representative.CurrentZone.IsDungeon,
                        IsFrontierZone(representative.CurrentRegionID, representative.CurrentZone.ID)));
                }

                AddStarterCamps(cells, snapshot);
                AddVerifiedDungeonCamps(cells, liveByZoneAndName);
                return cells.ToArray();
        }

        private static bool IsSourceEmptyCamp(string campId) =>
            !string.IsNullOrWhiteSpace(campId) &&
            campId.StartsWith("capnbry-source-empty:", StringComparison.OrdinalIgnoreCase);

        private void AbandonCamp(GameBot bot, string reason)
        {
            if (AutonomousRealmRaid.GetView(bot.Group) != null)
            {
                // A failed member route is retryable; it is not a vote to
                // destroy the expedition or select an unrelated grind task.
                ResetRouteOrderState();
                bot.StopMovingOnPath();
                bot.StopMoving();
                _expeditionRouteRetry = GameLoop.GameLoopTime + 30_000;
                Log.Warn($"REALM_EXPEDITION_ROUTE_RETRY bot={bot.Name} id={bot.DatabaseID} region={bot.CurrentRegionID} position={bot.X},{bot.Y},{bot.Z} reason={reason}");
                SetStatus(bot, "Expedition route retry", _camp?.MonsterName ?? "Rejoin expedition", reason);
                TryRegroupStrandedExpeditionMember(bot);
                return;
            }
            if (_camp != null)
            {
                // Apply the existing bounded camp cooldown to outdoor failures
                // too. Otherwise the coordinator immediately republishes the same
                // failed shared camp and all members retry it every few seconds.
                _rejectedDungeonCamps[_camp.Id] = GameLoop.GameLoopTime + 30 * 60_000;
                AutonomousBotGroupCoordinator.RejectUnreachableCamp(bot, _camp.Id, reason);
            }
            AutonomousGoalDiagnostics.End(bot,
                reason.StartsWith("No live ", StringComparison.Ordinal) ? GoalAttemptEnd.EmptyCamp :
                reason.StartsWith("Outleveled ", StringComparison.Ordinal) ? GoalAttemptEnd.Expired :
                GoalAttemptEnd.RouteFailure,
                reason);
            string oldTarget = _camp?.MonsterName ?? string.Empty;
            _camp = null;
            _campStartedTick = 0;
            _emptyCampSinceTick = 0;
            _reportedEmptySharedCampId = string.Empty;
            _patrolDestination = null;
            _pendingStableChoice = null;
            // A route failure must not immediately generate another goal from
            // the same disconnected source polygon. Empty live camps can replan
            // quickly; physical route failures get one quiet recovery window.
            _nextPlanTick = GameLoop.GameLoopTime + AutonomousRouteRecoveryPolicy.ReplanDelayMilliseconds(
                reason.StartsWith("No live ", StringComparison.Ordinal), bot.ObjectID);
            ResetRouteOrderState();
            bot.StopMovingOnPath();
            bot.StopMoving();
            if (bot.PersistentRecord != null)
                bot.PersistentRecord.CurrentCampId = string.Empty;
            SetStatus(bot, "Choosing a new live goal", "Find a reachable level-appropriate XP camp", reason, oldTarget);
        }

        private void ObserveDeaths(GameBot bot)
        {
            int deathCount = Math.Max(0, bot.PersistentRecord?.DeathCount ?? 0);
            int groupSize = Math.Max(1, (int)(bot.Group?.MemberCount ?? 1));
            ConColor naturalMaximum = NaturalMaximumTargetCon(groupSize);
            int maximumSafetySteps = (int)naturalMaximum - (int)ConColor.GREEN;

            if (_observedDeathCount < 0)
            {
                _observedDeathCount = deathCount;
                // DeathCount is a lifetime statistic, not defeats on this task.
                // Only newly observed defeats lower the current route difficulty.
                _deathDifficultySteps = 0;
                return;
            }

            if (deathCount <= _observedDeathCount)
                return;

            if (_groupDirective?.IsDynamic == true)
            {
                _observedDeathCount = deathCount;
                _lastEngagedCon = null;
                SetStatus(bot, "Regrouping after defeat", _groupDirective.SharedGoal,
                    "The party will regroup before another pull; its original task deadline keeps running", bot.PersistentRecord?.TargetName ?? string.Empty, forceSave: true);
                return;
            }

            long nowTick = GameLoop.GameLoopTime;
            _recentSoloDeathsWithoutExperience = AutonomousDeathRecoveryPolicy.NextNoExperienceDeathStreak(
                _recentSoloDeathsWithoutExperience, _lastSoloDeathTick, nowTick,
                bot.Level > _levelAtLastSoloDeath || bot.Experience > _experienceAtLastSoloDeath);
            _lastSoloDeathTick = nowTick;
            _experienceAtLastSoloDeath = bot.Experience;
            _levelAtLastSoloDeath = bot.Level;
            _soloDeathRecoveryUntilTick = nowTick +
                AutonomousDeathRecoveryPolicy.RetryDelayMilliseconds(_recentSoloDeathsWithoutExperience);
            _recoverAfterSoloDeath = true;

            ConColor failedCon = _lastEngagedCon ?? MaximumTargetCon(groupSize);
            ConColor saferCon = (ConColor)Math.Max((int)ConColor.GREEN, (int)failedCon - 1);
            int requiredSteps = (int)naturalMaximum - (int)saferCon;
            _deathDifficultySteps = Math.Clamp(
                Math.Max(_deathDifficultySteps + deathCount - _observedDeathCount, requiredSteps),
                0,
                maximumSafetySteps);
            _observedDeathCount = deathCount;
            _lastEngagedCon = null;

            string failedTarget = _camp?.MonsterName ?? bot.PersistentRecord?.TargetName ?? "the previous target";
            AutonomousGoalDiagnostics.End(bot, GoalAttemptEnd.Defeated, "Solo defeat caused a safer camp replan");
            _lastFailedCampId = _camp?.Id ?? bot.PersistentRecord?.CurrentCampId ?? string.Empty;
            _lastFailedTargetName = failedTarget;
            if (!string.IsNullOrWhiteSpace(_lastFailedCampId))
                _recentFailedSoloCamps[_lastFailedCampId] = nowTick + AutonomousDeathRecoveryPolicy.FailureMemoryMilliseconds;
            if (AutonomousCampDangerBench.RecordDeath(_lastFailedCampId, bot.DatabaseID) is TimeSpan bench)
                Log.Warn($"AUTONOMOUS_CAMP_DANGER_BENCH camp=\"{_lastFailedCampId}\" target=\"{failedTarget}\" " +
                         $"bots={AutonomousCampDangerBench.DistinctBotsToBench} hours={bench.TotalHours:0}");
            if (_recentSoloDeathsWithoutExperience >= 2 && _camp != null &&
                !string.IsNullOrWhiteSpace(_camp.MonsterName))
                _recentFailedSoloTargets[_camp.MonsterName] = nowTick + AutonomousDeathRecoveryPolicy.FailureMemoryMilliseconds;
            _camp = null;
            _campStartedTick = 0;
            _emptyCampSinceTick = 0;
            _patrolDestination = null;
            _nextPlanTick = GameLoop.GameLoopTime + 2_500 + bot.ObjectID % 1_500;
            if (bot.PersistentRecord != null)
                bot.PersistentRecord.CurrentCampId = string.Empty;
            Log.Warn($"AUTONOMOUS_DEATH_ROUTE_REPLAN bot={bot.Name} id={bot.DatabaseID} " +
                     $"level={bot.Level} realm={GlobalConstants.RealmToName(bot.Realm)} class=\"{bot.ClassName}\" deaths={deathCount} " +
                     $"failed_target=\"{failedTarget}\" new_max_con={MaximumTargetCon(groupSize)} " +
                     $"no_xp_death_streak={_recentSoloDeathsWithoutExperience} " +
                     $"recovery_wait_ms={Math.Max(0, _soloDeathRecoveryUntilTick - nowTick)} " +
                     $"region={bot.CurrentRegionID} position={bot.X},{bot.Y},{bot.Z}");
            SetStatus(
                bot,
                "Recovering from defeat",
                "Find a safer XP-bearing grind camp",
                $"Defeat against {failedTarget} lowered the target ceiling to {MaximumTargetCon(groupSize).ToString().ToLowerInvariant()}",
                failedTarget,
                forceSave: true);
        }

        private static ConColor NaturalMaximumTargetCon(int groupSize) => groupSize switch
        {
            >= 6 => ConColor.PURPLE,
            >= 4 => ConColor.RED,
            >= 2 => ConColor.ORANGE,
            _ => ConColor.YELLOW,
        };

        private ConColor MaximumTargetCon(int groupSize) =>
            (ConColor)Math.Max(
                (int)ConColor.GREEN,
                (int)NaturalMaximumTargetCon(groupSize) - Math.Max(_deathDifficultySteps, _groupDirective?.WipePenalty ?? 0));

        private bool HandleStableTravel(GameBot bot)
        {
            if (!bot.IsOnStableMasterRoute)
                return false;
            bot.MaintainStableMasterRoute();
            if (bot.IsMovingOnPath || bot.CurrentPathPoint != null)
            {
                SetStatus(bot, "Riding a stablemaster horse route", GoalText(),
                    $"Traveling toward {bot.StableRouteDestination}", _camp?.MonsterName ?? string.Empty, bot.StableRouteDestination);
                return true;
            }
            if (!bot.TryCompleteStableMasterRouteAfterArrival())
            {
                SetStatus(bot, "Riding a stablemaster horse route", GoalText(),
                    $"The authoritative ticket route is recovering toward {bot.StableRouteDestination}",
                    _camp?.MonsterName ?? string.Empty, bot.StableRouteDestination);
                return true;
            }
            ResetRouteOrderState();
            _nextStableCheckTick = 0;
            return false;
        }

        private bool TryResolveConnectedApproach(
            GameBot bot,
            Vector3 rawTarget,
            int arrivalRadius,
            out Vector3 approach,
            Func<Vector3, bool> preferredCandidate = null)
        {
            approach = rawTarget;
            Region region = bot.CurrentRegion;
            Zone currentZone = bot.CurrentZone;
            Zone targetZone = region?.GetZone((int)rawTarget.X, (int)rawTarget.Y);
            if (region == null || currentZone == null || targetZone == null)
                return false;
            if (currentZone != targetZone)
                return true; // IssuePath validates and retains a connected zone itinerary.

            IPathfindingMgr nav = PathfindingProvider.Instance;
            if (!nav.IsAvailable || !nav.HasNavmesh(currentZone))
                return true; // Preserve the legacy mover where no Detour mesh exists.
            TryRepairNavigationFloor(bot);
            if (_routeInterruptedByCombat)
            {
                _verifiedApproachTarget = null;
                _verifiedApproachPoint = null;
                _verifiedApproachZone = null;
                _verifiedApproachRadius = 0;
            }
            if (_verifiedApproachPoint.HasValue && _verifiedApproachTarget.HasValue &&
                _verifiedApproachZone == currentZone &&
                _verifiedApproachRadius == arrivalRadius &&
                Vector3.DistanceSquared(_verifiedApproachTarget.Value, rawTarget) <= 16 * 16)
            {
                approach = _verifiedApproachPoint.Value;
                return true;
            }

            Vector3 from = new(bot.X, bot.Y, bot.Z);
            long now = GameLoop.GameLoopTime;
            if (!_routeInterruptedByCombat && now < _failedApproachUntilTick &&
                _failedApproachTarget.HasValue && _failedApproachZone == currentZone &&
                _failedApproachRadius == arrivalRadius &&
                Vector3.DistanceSquared(_failedApproachTarget.Value, rawTarget) <= 16 * 16 &&
                Vector3.DistanceSquared(_failedApproachFrom, from) <= 16 * 16)
            {
                approach = rawTarget;
                return false;
            }

            int searchRadius = arrivalRadius == ZonePointArrivalRadius ? arrivalRadius - 16 : arrivalRadius;
            // A preferred-candidate rule is tried first; without a match the
            // unfiltered search runs exactly as before.
            bool Resolve(Func<Vector3, bool> filter, out Vector3 point) =>
                AutonomousZonePointApproach.TryResolve(nav, currentZone, from, rawTarget, searchRadius, out point, filter) ||
                searchRadius != arrivalRadius && AutonomousZonePointApproach.TryResolve(nav, currentZone,
                    from, rawTarget, arrivalRadius, out point, filter);
            if (!(preferredCandidate != null && Resolve(preferredCandidate, out approach)) &&
                !Resolve(null, out approach))
            {
                _failedApproachTarget = rawTarget;
                _failedApproachFrom = from;
                _failedApproachZone = currentZone;
                _failedApproachRadius = arrivalRadius;
                _failedApproachUntilTick = now + 3_000;
                return false;
            }
            _failedApproachTarget = null;

            _verifiedApproachTarget = rawTarget;
            _verifiedApproachPoint = approach;
            _verifiedApproachZone = currentZone;
            _verifiedApproachRadius = arrivalRadius;
            return true;
        }

        private void RejectTrainerAnchor(GameBot bot, GameTrainer trainer, string reason)
        {
            if (trainer != null)
                _failedTrainerAnchors[trainer] = GameLoop.GameLoopTime + 30 * 60_000;
            _trainingTrainer = null;
            _nextTrainingDecisionTick = GameLoop.GameLoopTime + 60_000;
            ResetRouteOrderState();
            SetStatus(bot, "Postponing class training", "Resume ordinary progression",
                reason + "; another trainer may be selected at the next maintenance opportunity");
        }

        private bool IsStableSourceQuarantined(GameBot bot)
        {
            return _stableSourceQuarantine.IsActive(bot.CurrentRegionID, bot.CurrentZone?.ID ?? 0,
                new(bot.X, bot.Y, bot.Z));
        }

        private Vector3 _boardingProgressPosition;
        private long _boardingProgressTick;

        private long _meetupBoardingStartedTick;

        private bool HandleRealmRaidStableTravel(GameBot bot)
        {
            if (!RealmRaidStableTravel.TryOrder(bot, GameLoop.GameLoopTime, out var order, out string failure))
            {
                if (failure != null)
                {
                    _nextStableCheckTick = GameLoop.GameLoopTime + 120_000;
                    ResetRouteOrderState();
                    Log.Warn($"REALM_RAID_TRAVEL_CANCEL bot=\"{bot.Name}\" reason=\"{failure}\"");
                }
                return false;
            }
            if (order.Action == RealmRaidStableTravel.Action.Walk)
            {
                if (!IssuePath(bot, order.Position, preciseArrival: true))
                {
                    RealmRaidStableTravel.Cancel(bot.Group);
                    _nextStableCheckTick = GameLoop.GameLoopTime + 120_000;
                    Log.Warn($"REALM_RAID_TRAVEL_CANCEL bot=\"{bot.Name}\" reason=\"No connected ticket formation approach\"");
                    return false;
                }
            }
            else if (order.Action == RealmRaidStableTravel.Action.Wait)
            {
                bot.StopMovingOnPath();
                bot.StopMoving();
            }
            else
            {
                var stable = order.Choice;
                long price = Math.Max(0, stable.Ticket.Price);
                bool valid = stable.Master.ObjectState == GameObject.eObjectState.Active &&
                    stable.Master.CurrentRegion == bot.CurrentRegion &&
                    bot.IsWithinRadius(stable.InteractionPoint, stable.Master.InteractDistance);
                bool paid = valid && (price == 0 || AutonomousBotEconomy.TrySpend(bot.DatabaseID, price));
                bool boarded = false;
                try
                {
                    if (paid)
                        boarded = bot.BeginStableMasterRoute(stable.DestinationName, TimeSpan.FromSeconds(stable.RideSeconds),
                            AutonomousStableRoutePlanner.CloneRoute(stable.Route), stable.Ticket);
                }
                finally
                {
                    if (paid && !boarded && price > 0) AutonomousBotEconomy.AddMoney(bot.DatabaseID, price);
                    RealmRaidStableTravel.Boarded(bot, boarded, GameLoop.GameLoopTime);
                }
                if (!boarded)
                {
                    _nextStableCheckTick = GameLoop.GameLoopTime + 120_000;
                    Log.Warn($"REALM_RAID_TRAVEL_CANCEL bot=\"{bot.Name}\" reason=\"Ticket could not be purchased or boarded\"");
                    return false;
                }
            }
            SetStatus(bot, "Expedition group transport", GoalText(),
                order.Action == RealmRaidStableTravel.Action.Board ? "Boarding the party's real ticket route" :
                order.Action == RealmRaidStableTravel.Action.Walk ? "Taking a spaced party transport position" :
                "Waiting for all eight party members before continuing", _camp?.MonsterName ?? string.Empty, order.Choice.DestinationName);
            return true;
        }

        private bool TryBeginFasterStableRoute(GameBot bot, Vector3 waypoint, string destinationName,
            Vector3? connectedPortal = null)
        {
            _connectedPortalRideSearchFoundNoChoice = false;
            // Town teleporters first: taken only when clearly faster than walking and any horse for
            // the same goal (assembled parties, raid parties and warbands go together via their leader).
            if (!connectedPortal.HasValue && TryTownTeleport(bot, bot.CurrentRegionID, waypoint,
                    Vector3.Distance(new(bot.X, bot.Y, bot.Z), waypoint) / Math.Max(150, (int)bot.MaxSpeed)))
                return true;
            var expedition = AutonomousRealmRaid.GetTravelView(bot);
            bool independentExpeditionTravel = expedition != null && (expedition.Muster ||
                _groupDirective?.Leader == null || !_groupDirective.Leader.IsAlive ||
                _groupDirective.Leader.CurrentRegionID != bot.CurrentRegionID || bot.GetDistanceTo(_groupDirective.Leader) > 1800 ||
                bot.Group.MemberCount < 8);
            bool meetup = _groupDirective?.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
                AutonomousBotGroupCoordinator.IsAssemblyPhase(_groupDirective.Phase);
            if (!AutonomousStableRoutePlanner.MayPlanMeetupHorse(meetup,
                    _groupDirective?.GroupId, _meetupHorseFailedGroupId))
                return false;
            if (AutonomousStableRoutePlanner.FinishMeetupOnFoot(meetup,
                    Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), waypoint)))
            {
                // Finish the local formation approach on foot. A cached horse
                // plan must not carry an almost-present member away again.
                _pendingStableChoice = null;
                return false;
            }
            // Town matchmaking may still use horses. Once formed, PvE parties
            // walk the shared route instead of splitting onto individual rides.
            if (_groupDirective?.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
                !AutonomousBotGroupCoordinator.IsAssemblyPhase(_groupDirective.Phase) && !independentExpeditionTravel)
            {
                // Only an enrolled epic expedition may opt into coordinated
                // party tickets. Ordinary PvE and RvR formations are unchanged.
                if (_groupDirective.Leader == bot && AutonomousRealmRaid.GetView(bot.Group) is { Muster: false } &&
                    bot.CurrentZone?.IsDungeon == false && GameLoop.GameLoopTime >= _nextStableCheckTick &&
                    !IsStableSourceQuarantined(bot))
                {
                    _nextStableCheckTick = GameLoop.GameLoopTime + 30_000;
                    var choice = AutonomousStableRoutePlanner.FindBest(bot, waypoint);
                    if (RealmRaidStableTravel.TryStart(bot, choice, GameLoop.GameLoopTime))
                        return HandleRealmRaidStableTravel(bot);
                }
                return false;
            }
            // An assembled warband walks together, preserving mutual defense
            // and speed-song range; rendezvous travelers may still ride alone.
            if (_groupDirective?.IsDynamic == true && _groupDirective.ObjectiveKind == eAutonomousObjectiveKind.RvR &&
                !AutonomousBotGroupCoordinator.IsAssemblyPhase(_groupDirective.Phase)) return false;
            if (GameRelic.IsPlayerCarryingRelic(bot))
                return false;
            if (bot.CurrentRegion == null)
                return false;
            if (IsStableSourceQuarantined(bot))
                return false;

            bool waypointChanged = Vector3.DistanceSquared(_pendingStableWaypoint, waypoint) > 500 * 500;
            bool pendingInvalid = _pendingStableChoice != null &&
                                  (_pendingStableChoice.Master.ObjectState is not GameObject.eObjectState.Active ||
                                   _pendingStableChoice.Master.CurrentRegion != bot.CurrentRegion || waypointChanged ||
                                   _pendingStablePortalRide != connectedPortal.HasValue);
            if (pendingInvalid)
                _pendingStableChoice = null;

            if (_pendingStableChoice == null)
            {
                if (GameLoop.GameLoopTime < _nextStableCheckTick)
                    return false;
                foreach (GameStableMaster expired in _failedBoardingMasters
                             .Where(pair => pair.Value <= GameLoop.GameLoopTime).Select(pair => pair.Key).ToArray())
                    _failedBoardingMasters.Remove(expired);
                IReadOnlySet<GameStableMaster> excluded = _failedBoardingMasters.Count == 0
                    ? null : _failedBoardingMasters.Keys.ToHashSet();
                if (connectedPortal.HasValue)
                {
                    _pendingStableChoice = AutonomousStableRoutePlanner.FindDirectRideToConnectedPortal(
                        bot, connectedPortal.Value, ZonePointArrivalRadius - 16, excluded);
                    _connectedPortalRideSearchFoundNoChoice = _pendingStableChoice == null;
                }
                else
                    _pendingStableChoice = AutonomousStableRoutePlanner.FindBest(bot, waypoint, excluded,
                        expedition == null && _groupDirective?.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
                        AutonomousBotGroupCoordinator.IsAssemblyPhase(_groupDirective.Phase));
                _pendingStablePortalRide = connectedPortal.HasValue;
                _pendingStableWaypoint = waypoint;
                _boardingProgressPosition = new(bot.X, bot.Y, bot.Z);
                _boardingProgressTick = GameLoop.GameLoopTime;
                _meetupBoardingStartedTick = GameLoop.GameLoopTime;
                if (_pendingStableChoice == null)
                {
                    _nextStableCheckTick = GameLoop.GameLoopTime + 20_000 + bot.ObjectID % 5_000;
                    return false;
                }
            }

            AutonomousStableRoutePlanner.Choice stable = _pendingStableChoice;
            if (AutonomousStableRoutePlanner.MeetupBoardingExpired(meetup && expedition == null, _meetupBoardingStartedTick, GameLoop.GameLoopTime, _boardingProgressTick))
            {
                RejectPendingBoarding(bot, "Meetup boarding stopped progressing or reached its five-minute limit; continuing toward the same rendezvous");
                ResetRouteOrderState();
                return false;
            }
            Vector3 boardingActor = new(bot.X, bot.Y, bot.Z);
            if (Vector3.DistanceSquared(boardingActor, _boardingProgressPosition) >= 64 * 64)
            {
                _boardingProgressPosition = boardingActor;
                _boardingProgressTick = GameLoop.GameLoopTime;
            }
            else if (GameLoop.GameLoopTime - _boardingProgressTick >= 90_000)
            {
                RejectPendingBoarding(bot, "No boarding-approach progress for ninety seconds; retaining the destination and trying another route");
                ResetRouteOrderState();
                return false;
            }
            if (!bot.IsWithinRadius(stable.BoardingPoint, AutonomousStableRoutePlanner.BoardingArrivalRadius))
            {
                // Boarding uses a narrow 3-D check, tighter than ordinary
                // travel arrival (48 horizontal / 96 vertical). Finish the
                // validated approach instead of stopping outside boarding range.
                if (!IssuePath(bot, stable.BoardingPoint, preciseArrival: true))
                {
                    RejectPendingBoarding(bot, "No connected route to this horse; continuing toward the destination");
                    return false;
                }
                SetStatus(bot, $"Approaching {stable.Master.Name}'s horse", GoalText(),
                    $"Walking to the real starting point for {stable.Ticket.Name}", _camp?.MonsterName ?? string.Empty, stable.Master.Name);
                return true;
            }

            // Use the validated service surface, not stale imported NPC height.
            // Horizontal proximity/range and a local mesh corridor are preserved.
            if (!bot.IsWithinRadius(stable.InteractionPoint, stable.Master.InteractDistance))
            {
                RejectPendingBoarding(bot, "Horse origin is outside the master's interaction range");
                return false;
            }

            long price = Math.Max(0, stable.Ticket.Price);
            if (price > 0 && !AutonomousBotEconomy.TrySpend(bot.DatabaseID, price))
            {
                _pendingStableChoice = null;
                _nextStableCheckTick = GameLoop.GameLoopTime + 20_000;
                return false;
            }

            bot.StopMovingOnPath();
            bot.StopMoving();
            if (!bot.BeginStableMasterRoute(stable.DestinationName, TimeSpan.FromSeconds(stable.RideSeconds), stable.Route, stable.Ticket))
            {
                if (price > 0)
                    AutonomousBotEconomy.AddMoney(bot.DatabaseID, price);
                _pendingStableChoice = null;
                return false;
            }
            _stableSourceQuarantine.Clear();
            _pendingStableChoice = null;
            SetStatus(bot, "Boarding a stablemaster horse", GoalText(),
                price > 0
                    ? $"Paid {price:N0} copper for {stable.Ticket.Name}; {stable.PlannedHops} planned leg(s)"
                    : $"Boarded free {stable.Ticket.Name}; {stable.PlannedHops} planned leg(s)",
                _camp?.MonsterName ?? string.Empty, stable.DestinationName, true);
            return true;
        }

        private bool _groupPaceWaiting;

        /// <summary>
        /// The group leader keeps moving at full speed unless a member is far behind; then it
        /// stops until that member is close again (see AutonomousGroupPace).
        /// </summary>
        private bool KeepGroupMovingAtPace(GameBot bot)
        {
            if (_groupDirective?.IsDynamic != true || _groupDirective.Leader != bot) return false;
            bool move = AutonomousGroupPace.Decide(
                AutonomousBotGroupCoordinator.FarthestMemberDistance(_groupDirective), _groupPaceWaiting) ==
                AutonomousGroupPace.Decision.FullSpeed;
            _groupPaceWaiting = !move;
            return move;
        }

        private bool IssuePath(GameBot bot, Vector3 destination, Vector3? validatedContinuation = null,
            bool preciseArrival = false, bool retargetWhileMoving = false)
        {
            TryRepairNavigationFloor(bot);
            if (AutonomousRvrTravel.TraverseFriendlyDoor(bot, destination))
            {
                bot.ForcePathReplot();
                _nextMoveOrderTick = 0;
                return true;
            }
            Vector3 current = new(bot.X, bot.Y, bot.Z);
            // A handful of real portal, formation, and dungeon points report a
            // final PathFound/PartialPath callback after the actor is already at
            // the endpoint. Treat physical arrival as success before consuming
            // that stale failure; otherwise the controller side-steps away from
            // a valid point and can eventually reject the whole objective.
            if (IsRouteDestinationReached(current, destination, preciseArrival))
            {
                bot.TryConsumeAutonomousPathFailure(out _, out _);
                bot.StopMovingOnPath();
                bot.StopMoving();
                ResetRouteOrderState();
                return true;
            }
            if (GuardDungeonTravel(bot, destination)) return false;
            _restingCamp = null;
            bot.WakeRecoveryRest();
            long now = GameLoop.GameLoopTime;
            bool destinationChanged = !_issuedRouteDestination.HasValue ||
                                      Vector3.DistanceSquared(_issuedRouteDestination.Value, destination) > 96 * 96;
            // Arrival is distinct from a failed/repeated movement order. Only a
            // real completed segment releases the retry gate early.
            if (bot.movementComponent is NpcMovementComponent movement && movement.ConsumeAutonomousTravelArrival())
                _nextMoveOrderTick = 0;
            if (destinationChanged)
            {
                _issuedRouteDestination = destination;
                _resolvedBoundaryStep = null;
                _resolvedBoundaryZone = null;
                _routeRecoveryWaypoint = null;
                _nextMoveOrderTick = 0;
                _lastRoutePosition = current;
                _lastRouteProgressTick = now;
                _routeStallReplans = 0;
                _routeRecoveryBaselineDistance = -1f;
            }

            if (_routeInterruptedByCombat)
            {
                _routeInterruptedByCombat = false;
                _verifiedApproachTarget = null;
                _verifiedApproachPoint = null;
                _verifiedApproachZone = null;
                _verifiedApproachRadius = 0;
                _routeRecoveryWaypoint = null;
                _nextMoveOrderTick = 0;
                _lastRoutePosition = current;
                _lastRouteProgressTick = now;
                bot.ForcePathReplot();
            }

            if (bot.TryConsumeAutonomousPathFailure(out PathfindingStatus failureStatus, out Vector3 failedDestination) &&
                AutonomousRouteRecoveryPolicy.AppliesToCurrentDestination(destinationChanged, failedDestination, destination))
            {
                _verifiedApproachTarget = null;
                _verifiedApproachPoint = null;
                _verifiedApproachZone = null;
                _verifiedApproachRadius = 0;
                if (AutonomousRouteRecoveryPolicy.CanContinuePartial(failureStatus, _pathSegmentOrigin, current))
                {
                    _pathSegmentOrigin = current;
                    bot.ForcePathReplot();
                    bot.PathTo(failedDestination, bot.MaxSpeed);
                    _nextMoveOrderTick = now + 1_500;
                    return true;
                }
                if (!TryBeginLocalRouteRecovery(bot, current, destination, failureStatus, failedDestination))
                    return false;
                return true;
            }

            if (_routeRecoveryWaypoint.HasValue)
            {
                Vector3 recovery = _routeRecoveryWaypoint.Value;
                if (Vector3.DistanceSquared(current, recovery) <= _routeRecoveryArrivalRadius * _routeRecoveryArrivalRadius)
                {
                    _routeRecoveryWaypoint = null;
                    _nextMoveOrderTick = 0;
                    _lastRoutePosition = current;
                    _lastRouteProgressTick = now;
                    bot.ForcePathReplot();
                }
                else
                {
                    // One order owns the detour until it arrives or explicitly
                    // reports a path failure. Do not pulse PathTo every AI turn.
                    if (AutonomousRouteRecoveryPolicy.ShouldRetainMovementOrder(bot.IsMoving, now, _nextMoveOrderTick))
                        return true;
                    bot.ForcePathReplot();
                    _pathSegmentOrigin = current;
                    bot.PathTo(recovery, bot.MaxSpeed);
                    _nextMoveOrderTick = now + 1_500;
                    return true;
                }
            }

            if (_lastRouteProgressTick == 0 || Vector3.DistanceSquared(current, _lastRoutePosition) >= 96 * 96)
            {
                _lastRoutePosition = current;
                _lastRouteProgressTick = now;
                if (_routeStallReplans > 0 && _routeRecoveryBaselineDistance >= 0)
                {
                    float currentDistance = Vector2.Distance(
                        new(current.X, current.Y), new(destination.X, destination.Y));
                    if (AutonomousRouteRecoveryPolicy.HasMeaningfulForwardProgress(
                            _routeRecoveryBaselineDistance, currentDistance))
                    {
                        _routeStallReplans = 0;
                        _routeRecoveryBaselineDistance = -1f;
                        // A bot that edges forward and sticks again inside the same
                        // small pocket has not escaped it. Keep the repeated-failure
                        // count until it actually leaves, or the safe relocation never
                        // triggers (10 bots at Druim Ligen looped ~60 times each).
                        if (AutonomousRouteRecoveryPolicy.HasLeftFailurePocket(
                                _lastTerminalRouteFailureRegion, bot.CurrentRegionID,
                                _lastTerminalRouteFailurePosition, current))
                            ResetRepeatedRouteFailures();
                    }
                }
            }
            else if (AutonomousRouteRecoveryPolicy.ShouldRecoverActiveRouteStall(
                         _lastRouteProgressTick, now, RouteStallReplotMilliseconds))
            {
                // Collision normally changes IsMoving to false. Requiring it
                // here made a fully stopped actor reissue the identical order
                // forever. Arrival and combat holds were handled above, so this
                // is still an active-route stall and uses the existing bounded
                // side-step/same-service recovery before resuming the goal.
                if (!TryBeginLocalRouteRecovery(bot, current, destination,
                        PathfindingStatus.NoPathFound, destination))
                    return false;
                return true;
            }

            // A live path is continuous inside NpcMovementComponent. The AI is
            // not a metronome for walking and must not replace the same order.
            // A follower chasing a moving formation spot is the exception: its
            // destination moved, so it heads for the new spot right away instead of
            // finishing the walk to the old one and stopping there first.
            if (!(retargetWhileMoving && destinationChanged) &&
                AutonomousRouteRecoveryPolicy.ShouldRetainMovementOrder(bot.IsMoving, now, _nextMoveOrderTick))
                return true;
            _nextMoveOrderTick = now + 1_500;

            Zone currentZone = bot.CurrentZone;
            Zone destinationZone = bot.CurrentRegion?.GetZone((int)destination.X, (int)destination.Y);
            if (currentZone == null || destinationZone == null)
            {
                AbandonCamp(bot, "The selected route ends outside a legal zone");
                return false;
            }

            if (destinationZone != currentZone)
            {
                if (!_resolvedBoundaryStep.HasValue || _resolvedBoundaryZone != currentZone)
                {
                    IPathfindingMgr nav = PathfindingProvider.Instance;
                    if (!AutonomousZoneItinerary.TryNextStep(bot.CurrentRegion, currentZone, destinationZone,
                            current, destination, nav, out var resolved,
                            zone => AutonomousRealmBoundary.Allows(bot.Realm, bot.CurrentRegionID, zone.ID)))
                    {
                        if (TryPlanCapitalTransit(bot, destination))
                            return HandleCapitalTransit(bot);
                        // Four live-audited Hibernian source components have no
                        // safe seam despite valid destinations. Only those exact
                        // pockets may use a mesh-proven escape; everyone else
                        // retains ordinary rejection and cooldown behavior.
                        if (TryEscapeTerminalRoutePocket(bot, current))
                            return true;
                        RejectPendingBoarding(bot, "No safe connected zone seam on the approach");
                        AbandonCamp(bot, "No safe connected zone seam on the selected route");
                        return false;
                    }
                    _resolvedBoundaryStep = resolved;
                    _resolvedBoundaryZone = currentZone;
                }
                AutonomousZoneBoundaryRouting.Step step = _resolvedBoundaryStep.Value;
                if (Vector3.DistanceSquared(current, step.Inside) <= 96 * 96)
                    bot.WalkTo(step.Outside, bot.MaxSpeed);
                else
                {
                    _pathSegmentOrigin = current;
                    if (bot.movementComponent is NpcMovementComponent mover)
                    {
                        // Both seam points were validated by the itinerary. If
                        // this enters the goal zone, its final corridor was too.
                        Vector3? final = destinationZone == bot.CurrentRegion.GetZone((int)step.Outside.X, (int)step.Outside.Y)
                            ? destination : null;
                        mover.PathAcrossValidatedSeam(step.Inside, step.Outside, final, bot.MaxSpeed);
                    }
                    else bot.PathTo(step.Inside, bot.MaxSpeed);
                }
                return true;
            }

            // The canonical Detour destination is authoritative. Do not insert
            // per-bot lateral corridor waypoints: those cosmetic offsets made
            // otherwise valid routes cut toward wall and collision edges.
            bot.ForcePathReplot();
            _pathSegmentOrigin = current;
            if (validatedContinuation.HasValue && bot.movementComponent is NpcMovementComponent continuousMover)
                continuousMover.PathViaValidatedWaypoint(destination, validatedContinuation.Value, bot.MaxSpeed);
            else
                bot.PathTo(destination, bot.MaxSpeed);
            return true;
        }

        private bool TryBeginLocalRouteRecovery(
            GameBot bot,
            Vector3 current,
            Vector3 destination,
            PathfindingStatus failureStatus,
            Vector3 failedDestination)
        {
            bot.GoalDiagnosticAttempt?.RouteFailed(failureStatus.ToString());

            // The Jordheim exchange room has one collision pocket whose Detour
            // polygon is connected on paper but whose physical approach stops at
            // the same coordinates. Use the existing validated same-service
            // surface recovery immediately for that audited pocket instead of
            // making three bots at a time repeat identical side-step attempts.
            if (AutonomousRouteHotspotRepair.IsJordheimServicePocket(bot.CurrentRegionID, current) &&
                TryRecoverDisconnectedServiceApproach(bot))
                return true;

            if (_routeStallReplans == 0 || _routeRecoveryBaselineDistance < 0)
            {
                _routeRecoveryBaselineDistance = Vector2.Distance(
                    new(current.X, current.Y), new(destination.X, destination.Y));
            }
            _routeStallReplans++;
            // A PathTo order aimed at a short recovery corner can report a
            // partial path even while the actor still has a complete native
            // corridor to the real camp goal. Do not make the failed corner
            // the next recovery's onward goal or select it again.
            bool bypassFailedCorner = !AutonomousRouteRecoveryPolicy.ShouldAbandon(_routeStallReplans) &&
                AutonomousCorridorRecovery.ShouldBypassFailedCorner(
                    PathfindingProvider.Instance, bot.CurrentZone,
                    bot.CurrentRegion?.GetZone((int)destination.X, (int)destination.Y),
                    current, destination, _routeRecoveryWaypoint, failedDestination);
            Vector3 onwardDestination = bypassFailedCorner ? destination : failedDestination;
            Vector3? rejectedCorner = bypassFailedCorner ? failedDestination : null;
            if (AutonomousRouteRecoveryPolicy.ShouldAbandon(_routeStallReplans) ||
                !TryFindLocalRecoveryWaypoint(bot, current, destination, onwardDestination,
                    _routeStallReplans, rejectedCorner, out Vector3 recovery))
            {
                Log.Warn($"AUTONOMOUS_ROUTE_RECOVERY bot={bot.Name} id={bot.DatabaseID} " +
                         $"level={bot.Level} realm={GlobalConstants.RealmToName(bot.Realm)} class=\"{bot.ClassName}\" " +
                         $"goal=\"{bot.PersistentRecord?.CurrentGoal}\" target=\"{bot.PersistentRecord?.TargetName}\" " +
                         $"region={bot.CurrentRegionID} position={bot.X},{bot.Y},{bot.Z} " +
                         $"destination={(int)destination.X},{(int)destination.Y},{(int)destination.Z} " +
                         $"failedDestination={(int)failedDestination.X},{(int)failedDestination.Y},{(int)failedDestination.Z} " +
                         $"pathStatus={failureStatus} reason=\"three verified local recovery attempts failed; selecting a new goal\"");
                RejectPendingBoarding(bot, "The boarding approach exhausted collision-safe recovery");
                // Service and trainer interiors in the installed client meshes
                // sometimes live on a disconnected decorative component.  Once
                // three real path attempts prove that condition, place only this
                // autonomous actor on a validated point beside the same NPC.  It
                // can then complete the real interaction instead of retrying and
                // logging the identical partial path every twenty seconds.
                if (TryRecoverDisconnectedServiceApproach(bot))
                    return true;
                if (_trainingTrainer != null)
                {
                    RejectTrainerAnchor(bot, _trainingTrainer,
                        "The trainer approach exhausted collision-safe recovery");
                    return false;
                }

                // Capital egress already has a tightly-scoped authoritative
                // zone-point recovery. Apply it to a physical-stall failure as
                // well as an up-front corridor rejection.
                if (_camp != null && TryRecoverBlockedCapitalEgress(bot, _camp))
                    return true;
                if (_groupDirective != null &&
                    (AutonomousBotGroupCoordinator.IsAssemblyPhase(_groupDirective.Phase) ||
                     _groupDirective.Phase == "Regrouping") &&
                    AutonomousBotGroupCoordinator.ReportUnreachableRendezvous(bot, _groupDirective.GroupId,
                        "The assigned formation route exhausted collision-safe recovery"))
                {
                    ResetRouteOrderState();
                    return false;
                }
                if (TryEscapeTerminalRoutePocket(bot, current))
                    return true;
                if (_camp != null && AutonomousCampRouteQuarantine.ReportFailure(_camp.Id, bot.DatabaseID))
                    Log.Warn($"AUTONOMOUS_CAMP_ROUTE_QUARANTINE camp=\"{_camp.Id}\" target=\"{_camp.MonsterName}\" " +
                             $"zone=\"{_camp.ZoneName}\" bots={AutonomousCampRouteQuarantine.DistinctBotsToQuarantine} " +
                             $"hours={AutonomousCampRouteQuarantine.QuarantineDuration.TotalHours:0}");
                AbandonCamp(bot, "The collision-safe route could not recover after three verified side steps");
                return false;
            }

            bot.StopMoving();
            bot.ForcePathReplot();
            _routeRecoveryWaypoint = recovery;
            _routeRecoveryArrivalRadius = Math.Clamp(Vector3.Distance(current, recovery) / 4, 4, 70);
            _nextMoveOrderTick = GameLoop.GameLoopTime + 1_500;
            _lastRoutePosition = current;
            _lastRouteProgressTick = GameLoop.GameLoopTime;
            _pathSegmentOrigin = current;
            bot.PathTo(recovery, bot.MaxSpeed);
            SetStatus(bot, "Recovering around blocked terrain", GoalText(),
                $"Taking verified navmesh side step {_routeStallReplans}/{RouteRecoveryAttemptsBeforeNewGoal} before resuming the same route",
                _camp?.MonsterName ?? string.Empty, _camp?.ZoneName ?? string.Empty);
            return true;
        }

        private float _routeRecoveryArrivalRadius = 70;
        private static bool TryFindLocalRecoveryWaypoint(
            GameBot bot,
            Vector3 current,
            Vector3 destination,
            Vector3 onwardDestination,
            int attempt,
            Vector3? rejectedCorner,
            out Vector3 recovery)
        {
            recovery = default;
            Zone zone = bot.CurrentZone;
            if (zone == null || !PathfindingProvider.Instance.IsAvailable)
                return false;

            // Recover along the existing corridor before trying geometric side
            // steps. A direction drawn straight at the final target can point
            // across a cliff/wall even when the real route is fully connected.
            if (!rejectedCorner.HasValue && AutonomousCorridorRecovery.TryNextCorner(PathfindingProvider.Instance, zone,
                    current, onwardDestination, out recovery))
                return true;

            Vector2 forward = new(destination.X - current.X, destination.Y - current.Y);
            if (forward.LengthSquared() < 1f)
                forward = new(0, 1);
            else
                forward = Vector2.Normalize(forward);

            int[][] angleOrders =
            [
                [90, -90, 135, -135, 180, 45, -45],
                [-90, 90, -135, 135, 180, -45, 45],
                [135, -135, 90, -90, 180, 45, -45],
            ];
            int[] angles = angleOrders[Math.Clamp(attempt - 1, 0, angleOrders.Length - 1)];
            float bestScore = float.MinValue;

            foreach (float radius in new[] { 220f, 340f, 460f })
            {
                foreach (int degrees in angles)
                {
                    float radians = degrees * MathF.PI / 180f;
                    Vector2 direction = new(
                        forward.X * MathF.Cos(radians) - forward.Y * MathF.Sin(radians),
                        forward.X * MathF.Sin(radians) + forward.Y * MathF.Cos(radians));
                    Vector3 raw = new(current.X + direction.X * radius, current.Y + direction.Y * radius, current.Z);
                    Vector3? surface = AutonomousNavigationSurface.MoveAlongGround(
                        PathfindingProvider.Instance, zone, current, raw);
                    if (!surface.HasValue || Vector3.DistanceSquared(current, surface.Value) < 80 * 80 ||
                        !AutonomousCorridorRecovery.IsOutsideFailedCorner(surface.Value, rejectedCorner) ||
                        !PathfindingProvider.Instance.HasLineOfSight(
                            zone, current, surface.Value, PathfindingProvider.Instance.DefaultFilters))
                        continue;

                    Zone onwardZone = bot.CurrentRegion?.GetZone((int)onwardDestination.X, (int)onwardDestination.Y);
                    if (onwardZone == zone && !AutonomousZoneItinerary.HasCompleteCorridor(
                            PathfindingProvider.Instance, zone, surface.Value, onwardDestination))
                        continue;

                    Vector2 achieved = new(surface.Value.X - current.X, surface.Value.Y - current.Y);
                    float lateral = MathF.Abs(forward.X * achieved.Y - forward.Y * achieved.X);
                    float clearance = achieved.Length();
                    float score = lateral * 2f + clearance;
                    if (score <= bestScore)
                        continue;
                    bestScore = score;
                    recovery = surface.Value;
                }
            }
            return bestScore > float.MinValue;
        }

        private bool TryEscapeTerminalRoutePocket(GameBot bot, Vector3 current)
        {
            long now = GameLoop.GameLoopTime;
            bool repeated = AutonomousRouteRecoveryPolicy.IsSameRepeatedFailurePocket(
                _lastTerminalRouteFailureRegion, bot.CurrentRegionID,
                _lastTerminalRouteFailurePosition, current, now - _lastTerminalRouteFailureTick);
            _terminalRouteFailuresInPocket = repeated ? _terminalRouteFailuresInPocket + 1 : 1;
            _lastTerminalRouteFailureRegion = bot.CurrentRegionID;
            // Anchor the pocket at its first failure; moving it with every
            // failure let a bot alternating between two spots reset the count.
            if (!repeated)
                _lastTerminalRouteFailurePosition = current;
            _lastTerminalRouteFailureTick = now;

            Vector3 escape;
            ushort escapeRegion;
            string escapeName;
            if (_camp != null &&
                AutonomousRouteHotspotRepair.TryGetAuditedCampEscape(
                    PathfindingProvider.Instance, bot.CurrentRegion, bot.CurrentRegionID,
                    _camp.MonsterName, current, out escape))
            {
                escapeRegion = bot.CurrentRegionID;
                escapeName = _camp.ZoneName;
            }
            else if (AutonomousRouteHotspotRepair.TryGetImmediateEscape(
                    PathfindingProvider.Instance, bot.CurrentRegion, bot.CurrentRegionID, current, out escape))
            {
                escapeRegion = bot.CurrentRegionID;
                escapeName = bot.CurrentZone?.Description ?? $"region {escapeRegion}";
            }
            else if (_camp != null &&
                     AutonomousRouteHotspotRepair.TryResolveStarterDungeonRouteSurface(
                         PathfindingProvider.Instance, bot.CurrentRegion, bot.CurrentRegionID,
                         _camp.RegionId, current, out escape))
            {
                escapeRegion = bot.CurrentRegionID;
                escapeName = bot.CurrentZone?.Description ?? $"region {escapeRegion}";
            }
            else
            {
                // A walled-off pocket (navmesh/pockets.json) has no way out: relocate at the first
                // failure instead of after several.
                if (_terminalRouteFailuresInPocket < AutonomousRouteRecoveryPolicy.FailuresBeforeSafeRelocation &&
                    !(bot.CurrentZone is Zone pocketZone && AutonomousZonePockets.Contains(pocketZone.ID, current.X, current.Y, current.Z)))
                    return false;
                AutonomousStuckWatchdog.CapitalLocation capital = AutonomousStuckWatchdog.SafeCapitalFor(bot.Realm);
                if (capital.RegionId == 0)
                    return false;
                escapeRegion = capital.RegionId;
                escape = new(capital.X, capital.Y, capital.Z);
                escapeName = capital.Name;
            }

            ushort failedSourceRegion = bot.CurrentRegionID;
            ushort failedSourceZone = bot.CurrentZone?.ID ?? 0;
            ushort failedTargetRegion = _camp?.RegionId ?? 0;
            bool moved = escapeRegion == bot.CurrentRegionID
                ? bot.MoveInRegion(escapeRegion, (int)Math.Round(escape.X), (int)Math.Round(escape.Y),
                    (int)Math.Round(escape.Z), bot.Heading, true)
                : bot.MoveTo(escapeRegion, (int)Math.Round(escape.X), (int)Math.Round(escape.Y),
                    (int)Math.Round(escape.Z), bot.Heading);
            if (!moved)
                return false;

            ClearZonePointQuarantine(bot, failedSourceRegion, failedSourceZone, failedTargetRegion);

            Log.Warn($"AUTONOMOUS_ROUTE_POCKET_ESCAPE bot={bot.Name} id={bot.DatabaseID} " +
                     $"from={_lastTerminalRouteFailureRegion}:{(int)current.X},{(int)current.Y},{(int)current.Z} " +
                     $"to={escapeRegion}:{(int)escape.X},{(int)escape.Y},{(int)escape.Z} " +
                     $"terminalFailures={_terminalRouteFailuresInPocket}");
            ResetRouteOrderState();
            ResetRepeatedRouteFailures();
            bot.MarkAutonomousStateDirty();
            AutonomousBotStatusPersistence.Queue(bot);
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Recovery);
            SetStatus(bot, "Escaped disconnected terrain", GoalText(),
                $"Reached validated ground in {escapeName}; resuming the same goal",
                _camp?.MonsterName ?? string.Empty, _camp?.ZoneName ?? escapeName, true);
            return true;
        }

        private static void ClearZonePointQuarantine(GameBot bot, ushort sourceRegion,
            ushort sourceZone, ushort targetRegion)
        {
            if (bot == null || sourceRegion == 0 || targetRegion == 0)
                return;
            long botId = bot.DatabaseID > 0 ? bot.DatabaseID : bot.ObjectID;
            string prefix = $"{botId}:{sourceRegion}:{sourceZone}:{targetRegion}:";
            // Enumerate the dictionary itself: .Keys takes every bucket lock of
            // this server-wide table on each call; the enumerator is lock-free.
            foreach (KeyValuePair<string, long> entry in FailedZonePointUntil)
                if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                    FailedZonePointUntil.TryRemove(entry.Key, out _);
        }

        private void ResetRepeatedRouteFailures()
        {
            _lastTerminalRouteFailureRegion = 0;
            _lastTerminalRouteFailurePosition = default;
            _lastTerminalRouteFailureTick = 0;
            _terminalRouteFailuresInPocket = 0;
        }

        private void RejectPendingBoarding(GameBot bot, string reason)
        {
            if (_pendingStableChoice is not { } stable)
                return;
            if (_groupDirective?.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
                AutonomousBotGroupCoordinator.IsAssemblyPhase(_groupDirective.Phase))
                _meetupHorseFailedGroupId = _groupDirective.GroupId;
            _failedBoardingMasters[stable.Master] = GameLoop.GameLoopTime + 10 * 60_000;
            if (reason.Contains("exhausted", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("connected zone seam", StringComparison.OrdinalIgnoreCase))
            {
                _stableSourceQuarantine.Mark(bot.CurrentRegionID, bot.CurrentZone?.ID ?? 0,
                    new(bot.X, bot.Y, bot.Z));
            }
            _pendingStableChoice = null;
            _nextStableCheckTick = 0;
            Log.Warn($"AUTONOMOUS_STABLE_APPROACH_FAILED bot=\"{bot.Name}\" id={bot.DatabaseID} level={bot.Level} " +
                     $"realm={GlobalConstants.RealmToName(bot.Realm)} group=\"{_groupDirective?.GroupId}\" master=\"{stable.Master.Name}\" " +
                     $"ticket=\"{stable.Ticket.Id_nb}\" region={bot.CurrentRegionID} position={bot.X},{bot.Y},{bot.Z} " +
                     $"reason=\"{reason}\" retryAfterSeconds=600");
        }

        private void ResetRouteOrderState()
        {
            _capitalTransit = null;
            _resolvedBoundaryStep = null;
            _resolvedBoundaryZone = null;
            _nextMoveOrderTick = 0;
            _issuedRouteDestination = null;
            _routeRecoveryWaypoint = null;
            _routeRecoveryBaselineDistance = -1f;
            _routeInterruptedByCombat = false;
            _lastRouteProgressTick = 0;
            _routeStallReplans = 0;
            _verifiedApproachTarget = null;
            _verifiedApproachPoint = null;
            _verifiedApproachZone = null;
            _verifiedApproachRadius = 0;
        }

        private void SetStatus(
            GameBot bot,
            string activity,
            string goal,
            string progress,
            string target = "",
            string destination = "",
            bool forceSave = false)
        {
            OfflineWorldBotRecord record = bot.PersistentRecord;
            if (record == null)
                return;

            bool durableChanged = record.Activity != activity || record.CurrentGoal != goal ||
                                  record.TargetName != target || record.TravelDestination != destination;
            bool progressChanged = record.ObjectiveProgress != progress;
            bool changed = durableChanged || progressChanged;
            if (!changed && !forceSave)
                return;

            record.Activity = activity;
            record.CurrentGoal = goal;
            record.ObjectiveProgress = progress;
            record.TargetName = target ?? string.Empty;
            record.TravelDestination = destination ?? string.Empty;
            // Activity changes are immediately visible in memory and queued,
            // while rapidly changing progress text is coalesced. Never block an
            // NPC think thread on a SQLite transaction.
            if (durableChanged || forceSave || GameServiceUtils.ShouldTick(_nextStatusSaveTick))
            {
                _nextStatusSaveTick = GameLoop.GameLoopTime + 12_000 + bot.ObjectID % 4_000;
                bot.MarkAutonomousStateDirty();
                AutonomousBotStatusPersistence.Queue(bot);
            }
        }

        private string GoalText() => _groupDirective?.IsDynamic == true &&
            AutonomousBotGroupCoordinator.IsAssemblyPhase(_groupDirective.Phase)
            ? _groupDirective.SharedGoal
            : _camp == null
            ? "Find a reachable level-appropriate XP camp"
            : $"Grind {_camp.MonsterName} in {_camp.ZoneName}";

        // Level 0 creatures are real XP targets (5 XP each; blue to a level 1
        // character), so they are no longer excluded. Pets, guards, service
        // NPCs and untargetable/peaceful objects remain excluded below.
        private static bool IsExperienceMonster(GameNPC npc) =>
            npc?.ObjectState is GameObject.eObjectState.Active && npc.Realm == eRealm.None && npc.Brain != null &&
            (npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) == 0 &&
            npc is not GameMerchant && npc is not GameGuard && npc is not GameTaxi &&
            npc is not GameSummonedPet && npc is not GameKeepGuard && npc is not GameTrainer &&
            npc is not GameTeleporter && npc is not GameHealer && npc is not CraftNPC &&
            !AutonomousNeutralTownPolicy.IsTownResident(npc);

        private static bool IsZoneAccessible(eRealm realm, Zone zone)
        {
            if (zone?.ZoneRegion == null ||
                !AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(zone.ZoneRegion.Expansion))
                return false;
            if (zone.IsDungeon && (!AutonomousDungeonPolicy.IsSupportedDungeonZone(zone.ID) ||
                                   !PathfindingProvider.Instance.HasNavmesh(zone)))
                return false;
            if (zone.ZoneRegion.ID == AutonomousDarknessFallsPolicy.RegionId)
                return AutonomousDungeonGoalCatalog.HasCompleteDarknessFallsCatalog;
            eRealm owner = ProtectedRealm(zone.ZoneRegion.ID, zone.ID);
            return owner == eRealm.None || owner == realm;
        }

        private static bool IsZoneAccessible(eRealm realm, Zone zone, ushort currentRegion) =>
            IsZoneAccessible(realm, zone);

        private static bool IsRegionEdgeAccessible(eRealm realm, ushort sourceRegion, ushort targetRegion) =>
            AutonomousDarknessFallsPolicy.CanUseRegionEdge(realm, sourceRegion, targetRegion, DFEnterJumpPoint.CanRealmEnter);

        private static bool IsRegionPointAccessible(eRealm realm, ushort regionId, int x, int y)
        {
            Region region = WorldMgr.GetRegion(regionId);
            if (region == null || !AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(region.Expansion))
                return false;
            Zone zone = region.GetZone(x, y) ?? region.Zones.FirstOrDefault();
            return IsZoneAccessible(realm, zone);
        }

        private static bool IsCrossingSourceAccessible(eRealm realm, ushort currentRegion, DbZonePoint point) =>
            AutonomousDarknessFallsPolicy.CanEvacuateThroughHomeExit(realm, currentRegion, point) ||
            IsRegionPointAccessible(realm, point.SourceRegion, point.SourceX, point.SourceY);

        private bool RetireStaleDarknessFallsGoalAndEvacuate(GameBot bot)
        {
            if (AutonomousDungeonGoalCatalog.HasCompleteDarknessFallsCatalog ||
                AutonomousRealmRaid.GetView(bot.Group) != null)
                return false;

            if (_groupDirective?.IsDynamic == true &&
                _groupDirective.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
                _groupDirective.Camp != null &&
                AutonomousDarknessFallsPolicy.MustRetireOrdinaryGoal(_groupDirective.Camp.RegionId, false))
            {
                AutonomousBotGroupCoordinator.RejectUnreachableCamp(bot, _groupDirective.Camp.Id,
                    "Darkness Falls ordinary-goal certificate is unavailable");
                _groupDirective = AutonomousBotGroupCoordinator.Pulse(bot);
            }
            if (_camp != null && AutonomousDarknessFallsPolicy.MustRetireOrdinaryGoal(_camp.RegionId, false))
                AbandonCamp(bot, "Darkness Falls ordinary-goal certificate is unavailable");

            return bot.CurrentRegionID == AutonomousDarknessFallsPolicy.RegionId &&
                EvacuateUncertifiedDarknessFalls(bot);
        }

        private bool EvacuateUncertifiedDarknessFalls(GameBot bot)
        {
            DbZonePoint ownExit = ZonePoints().FirstOrDefault(point =>
                AutonomousDarknessFallsPolicy.CanEvacuateThroughHomeExit(bot.Realm,
                    bot.CurrentRegionID, point));
            if (ownExit == null)
            {
                bot.StopMoving();
                SetStatus(bot, "Holding inside Darkness Falls", "Reach own realm exit",
                    "No authoritative home-realm exit is available; no other faction portal will be used");
                return true;
            }

            int distance = Distance(bot.X, bot.Y, ownExit.SourceX, ownExit.SourceY);
            if (AtRegionCrossing(bot, ownExit, distance))
            {
                bot.StopMovingOnPath();
                bot.StopMoving();
                if (AutonomousZonePointArrival.TryResolve(ownExit, out Vector3 arrival) &&
                    bot.MoveTo(ownExit.TargetRegion, (int)arrival.X, (int)arrival.Y,
                        (int)arrival.Z, ownExit.TargetHeading))
                {
                    _nextDarknessFallsEvacuationProbeTick = 0;
                    _lastDarknessFallsEvacuationProgressTick = 0;
                    ResetRouteOrderState();
                    AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Movement);
                    SetStatus(bot, "Left Darkness Falls", "Return to own realm",
                        "Used the authoritative home-side exit after the ordinary-goal proof closed");
                }
                return true;
            }

            long now = GameLoop.GameLoopTime;
            if (bot.IsCasting || bot.IsCrowdControlled || bot.IsRecoveryResting)
                return true;
            if (_lastDarknessFallsEvacuationProgressTick == 0 && bot.IsMoving)
                bot.StopMoving(); // Discard a saved goal's old route first.
            Vector3 current = new(bot.X, bot.Y, bot.Z);
            if (bot.IsMoving)
            {
                if (_lastDarknessFallsEvacuationProgressTick == 0 ||
                    Vector3.DistanceSquared(current, _lastDarknessFallsEvacuationPosition) >= 96 * 96)
                {
                    _lastDarknessFallsEvacuationPosition = current;
                    _lastDarknessFallsEvacuationProgressTick = now;
                    return true;
                }
                if (now - _lastDarknessFallsEvacuationProgressTick < 12_000)
                    return true;
                // Replot only after a real stall. Never relocate through a
                // wall or accept a partial corridor as an evacuation shortcut.
                bot.StopMoving();
            }
            if (now < _nextDarknessFallsEvacuationProbeTick)
                return true;

            Vector3 target = new(ownExit.SourceX, ownExit.SourceY, ownExit.SourceZ);
            IPathfindingMgr nav = PathfindingProvider.Instance;
            Zone sourceZone = bot.CurrentZone;
            Zone targetZone = bot.CurrentRegion?.GetZone(ownExit.SourceX, ownExit.SourceY);
            Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[256];
            bool safe = nav.IsAvailable && sourceZone != null && sourceZone == targetZone &&
                nav.HasNavmesh(sourceZone) &&
                AutonomousDarknessFallsNavigation.MayQueueOwnExitGroundPath(bot.Realm,
                    nav.GetPathStraight(sourceZone, current, target, nav.DefaultFilters, nodes),
                    nodes, current, target);
            if (!safe)
            {
                bot.StopMoving();
                _nextDarknessFallsEvacuationProbeTick = now + 15_000;
                SetStatus(bot, "Holding inside Darkness Falls", "Reach own realm exit",
                    "No complete walk-only route to the home exit is proven; avoiding partial paths and cliff jumps");
                return true;
            }

            bot.ForcePathReplot();
            bot.PathTo(target, bot.MaxSpeed);
            _nextDarknessFallsEvacuationProbeTick = now + 3_000;
            _lastDarknessFallsEvacuationPosition = current;
            _lastDarknessFallsEvacuationProgressTick = now;
            SetStatus(bot, "Leaving Darkness Falls", "Return to own realm",
                $"Walking to the home-side exit; {distance:N0} units remain");
            return true;
        }

        public static eRealm ProtectedRealm(ushort regionId, ushort zoneId)
        {
            if (regionId == 1)
                return zoneId is 11 or 12 or 14 or 15 ? eRealm.None : eRealm.Albion;
            if (regionId == 100)
                return zoneId is 111 or 112 or 113 or 115 ? eRealm.None : eRealm.Midgard;
            if (regionId == 200)
                return zoneId is 210 or 211 or 212 or 214 ? eRealm.None : eRealm.Hibernia;
            if (regionId is 10 or 20 or 21 or 22 or 23 or 24 or 50 or 51 or 60 or 61 or 62)
                return eRealm.Albion;
            if (regionId is 101 or 125 or 126 or 127 or 128 or 129 or 150 or 151 or 160 or 161)
                return eRealm.Midgard;
            if (regionId is 180 or 181 or 190 or 191 or 192 or 193 or 194 or 201 or 220 or 221 or 222 or 223 or 224)
                return eRealm.Hibernia;
            return eRealm.None;
        }

        private static bool IsFrontierZone(ushort regionId, ushort zoneId) =>
            AutonomousDungeonPolicy.IsSharedFrontierDungeon(regionId) ||
            regionId == 1 && zoneId is 11 or 12 or 14 or 15 ||
            regionId == 100 && zoneId is 111 or 112 or 113 or 115 ||
            regionId == 200 && zoneId is 210 or 211 or 212 or 214;

        private static bool IsInFrontier(GameLiving bot) =>
            bot?.CurrentZone != null && IsFrontierZone(bot.CurrentRegionID, bot.CurrentZone.ID);

        private static bool IsInFrontier(GamePlayer player) =>
            player?.CurrentZone != null && IsFrontierZone(player.CurrentRegionID, player.CurrentZone.ID);

        private static bool IsSafeArea(GameObject obj)
        {
            List<IArea> areas = obj?.CurrentZone?.GetAreasOfSpot(obj);
            if (areas == null) return false;
            for (int i = 0; i < areas.Count; i++)
                if (areas[i] is AbstractArea area && (area.IsSafeArea || area is Area.BindArea)) return true;
            return false;
        }

        private static bool IsFrontierRegionPoint(ushort regionId, int x, int y)
        {
            Region region = WorldMgr.GetRegion(regionId);
            Zone zone = region?.GetZone(x, y);
            return zone != null && IsFrontierZone(regionId, zone.ID);
        }

        private static eRealm OpposingRealmFor(eRealm realm) => realm switch
        {
            eRealm.Albion => eRealm.Midgard,
            eRealm.Midgard => eRealm.Hibernia,
            _ => eRealm.Albion,
        };

        private static HashSet<ushort> ReachableRegions(eRealm realm, ushort startRegion)
        {
            var seen = new HashSet<ushort> { startRegion };
            var queue = new Queue<ushort>();
            queue.Enqueue(startRegion);
            DbZonePoint[] points = ZonePoints();
            while (queue.Count > 0)
            {
                ushort region = queue.Dequeue();
                foreach (DbZonePoint point in points.Where(point => point.SourceRegion == region &&
                                                                    (point.Realm == 0 || point.Realm == (ushort)realm) &&
                                                                    AutonomousDarknessFallsPolicy.CanUsePortalRow(realm, point) &&
                                                                    IsRegionEdgeAccessible(realm, point.SourceRegion, point.TargetRegion) &&
                                                                    IsCrossingSourceAccessible(realm, startRegion, point) &&
                                                                    IsRegionPointAccessible(realm, point.TargetRegion, point.TargetX, point.TargetY)))
                {
                    if (seen.Add(point.TargetRegion))
                        queue.Enqueue(point.TargetRegion);
                }
            }
            return seen;
        }

        private static DbZonePoint FindNextCrossing(GameBot bot, ushort targetRegion, int targetX, int targetY)
        {
            return FindNextCrossing(bot.Realm, bot.CurrentRegionID, targetRegion, targetX, targetY, bot);
        }

        private static DbZonePoint FindNextCrossing(eRealm realm, ushort currentRegion, ushort targetRegion, int targetX, int targetY) =>
            FindNextCrossing(realm, currentRegion, targetRegion, targetX, targetY, null);

        private static DbZonePoint FindNextCrossing(eRealm realm, ushort currentRegion, ushort targetRegion,
            int targetX, int targetY, GameBot quarantineBot)
        {
            DbZonePoint[] points = ZonePoints()
                .Where(point => IsAuthoritativeZonePointEdge(point) &&
                                AutonomousDungeonGoalCatalog.CanUseEntrance(point, targetRegion, targetX, targetY) &&
                                (point.Realm == 0 || point.Realm == (ushort)realm) &&
                                AutonomousDarknessFallsPolicy.CanUsePortalRow(realm, point) &&
                                IsRegionEdgeAccessible(realm, point.SourceRegion, point.TargetRegion) &&
                                IsCrossingSourceAccessible(realm, currentRegion, point) &&
                                IsRegionPointAccessible(realm, point.TargetRegion, point.TargetX, point.TargetY))
                .ToArray();

            DbZonePoint direct = points.Where(point => point.SourceRegion == currentRegion &&
                                                       (quarantineBot == null || !IsZonePointQuarantined(quarantineBot, point)) &&
                                                       point.TargetRegion == targetRegion)
                .OrderBy(point => DistanceSquared(point.TargetX, point.TargetY, targetX, targetY) +
                                  (quarantineBot == null ? 0 :
                                      DistanceSquared(point.SourceX, point.SourceY, quarantineBot.X, quarantineBot.Y)))
                .FirstOrDefault();
            if (direct != null)
                return direct;

            var previous = new Dictionary<ushort, DbZonePoint>();
            var seen = new HashSet<ushort> { currentRegion };
            var queue = new Queue<ushort>();
            queue.Enqueue(currentRegion);
            while (queue.Count > 0)
            {
                ushort region = queue.Dequeue();
                IEnumerable<DbZonePoint> outgoing = points.Where(point => point.SourceRegion == region &&
                    (quarantineBot == null || region != currentRegion || !IsZonePointQuarantined(quarantineBot, point)));
                if (quarantineBot != null && region == currentRegion)
                {
                    outgoing = outgoing.OrderBy(point =>
                        DistanceSquared(point.SourceX, point.SourceY, quarantineBot.X, quarantineBot.Y) +
                        DistanceSquared(point.TargetX, point.TargetY, targetX, targetY));
                }
                foreach (DbZonePoint edge in outgoing)
                {
                    if (!seen.Add(edge.TargetRegion))
                        continue;
                    previous[edge.TargetRegion] = edge;
                    if (edge.TargetRegion == targetRegion)
                    {
                        DbZonePoint first = edge;
                        while (first.SourceRegion != currentRegion && previous.TryGetValue(first.SourceRegion, out DbZonePoint prior))
                            first = prior;
                        return first;
                    }
                    queue.Enqueue(edge.TargetRegion);
                }
            }
            return null;
        }

        internal static IReadOnlyList<DbZonePoint> RealmEventCrossings() => ZonePoints();

        private static DbZonePoint[] ZonePoints()
        {
            if (_zonePoints != null)
                return _zonePoints;
            lock (ZonePointLock)
            {
                _zonePoints ??= DOLDB<DbZonePoint>.SelectAllObjects()
                    .Where(IsAuthoritativeZonePointEdge)
                    .ToArray();
                return _zonePoints;
            }
        }

        /// <summary>Pure validation seam for DB-backed portal/zone-point edges.</summary>
        public static bool IsAuthoritativeZonePointEdge(DbZonePoint point) =>
            point != null && point.SourceRegion != 0 && point.TargetRegion != 0 &&
            point.SourceRegion != point.TargetRegion;

        public static string ZonePointQuarantineKey(long botId, ushort sourceRegion, ushort sourceZone,
            ushort targetRegion, int pointId) =>
            $"{botId}:{sourceRegion}:{sourceZone}:{targetRegion}:{pointId}";

        private static string ZonePointKey(GameBot bot, DbZonePoint point)
        {
            if (point == null || bot == null)
                return string.Empty;
            long botId = bot.DatabaseID > 0 ? bot.DatabaseID : bot.ObjectID;
            return ZonePointQuarantineKey(botId, point.SourceRegion,
                bot.CurrentZone?.ID ?? 0, point.TargetRegion, point.Id);
        }

        private static bool IsZonePointQuarantined(GameBot bot, DbZonePoint point)
        {
            string key = ZonePointKey(bot, point);
            if (key.Length == 0 || !FailedZonePointUntil.TryGetValue(key, out long until))
                return false;
            if (until > GameLoop.GameLoopTime)
                return true;
            FailedZonePointUntil.TryRemove(key, out _);
            return false;
        }

        private bool TryRepairNavigationFloor(GameBot bot)
        {
            var nav = PathfindingProvider.Instance;
            Vector3 current = new(bot.X, bot.Y, bot.Z);
            if (bot.InCombat || bot.IsAttacking || bot.IsCasting || bot.IsOnStableMasterRoute ||
                (bot.Brain as BotBrain)?.HasAggro == true ||
                !AutonomousRouteHotspotRepair.IsKnownFloorDriftArea(bot.CurrentRegionID, current, out _) ||
                AutonomousNavigationSurface.TryFloor(nav, bot.CurrentZone, current, out _) ||
                !AutonomousRouteHotspotRepair.TryResolveFloor(nav, bot.CurrentZone, bot.CurrentRegionID, current, out var floor))
                return false;
            bot.StopMovingOnPath();
            bot.StopMoving();
            if (!bot.MoveInRegion(bot.CurrentRegionID, (int)Math.Round(floor.X), (int)Math.Round(floor.Y),
                    (int)Math.Round(floor.Z), bot.Heading, true)) return false;
            bot.TryConsumeAutonomousPathFailure(out _, out _);
            bot.ForcePathReplot();
            ResetRouteOrderState();
            Log.Info($"AUTONOMOUS_ROUTE_FLOOR_CORRECTION bot={bot.Name} region={bot.CurrentRegionID} from={current} to={floor}");
            return true;
        }

        private bool TryRecoverDisconnectedServiceApproach(GameBot bot)
        {
            GameNPC service = _serviceNpc ?? _trainingTrainer;
            if (bot == null || service?.ObjectState != GameObject.eObjectState.Active ||
                service.CurrentRegionID != bot.CurrentRegionID || service.CurrentZone == null ||
                Distance(bot.X, bot.Y, service.X, service.Y) > 12_000)
                return false;

            IPathfindingMgr nav = PathfindingProvider.Instance;
            Vector3 safe;
            bool resolved = AutonomousRouteHotspotRepair.TryResolveJordheimServiceApproach(nav,
                service.CurrentZone, bot.CurrentRegionID, new(bot.X, bot.Y, bot.Z),
                new(service.X, service.Y, service.Z), Math.Max(32, GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE), out safe);
            if (!resolved)
                resolved = AutonomousRendezvousNavigation.TryChoosePoint(nav, service.CurrentZone,
                    new(service.X, service.Y, service.Z), out safe);
            if (!nav.HasNavmesh(service.CurrentZone) || !resolved ||
                Vector2.Distance(new(safe.X, safe.Y), new(service.X, service.Y)) > 420)
                return false;

            ushort region = bot.CurrentRegionID;
            int oldX = bot.X, oldY = bot.Y, oldZ = bot.Z;
            bot.StopMovingOnPath();
            bot.StopMoving();
            if (!bot.MoveTo(region, (int)Math.Round(safe.X), (int)Math.Round(safe.Y),
                    (int)Math.Round(safe.Z), bot.Heading))
                return false;

            ResetRouteOrderState();
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Recovery);
            Log.Warn($"AUTONOMOUS_SERVICE_SURFACE_RECOVERY bot=\"{bot.Name}\" id={bot.DatabaseID} " +
                     $"service=\"{service.Name}\" from={region}:{oldX},{oldY},{oldZ} " +
                     $"to={region}:{bot.X},{bot.Y},{bot.Z}");
            return true;
        }

        private static void QuarantineZonePoint(GameBot bot, DbZonePoint point)
        {
            string key = ZonePointKey(bot, point);
            if (key.Length > 0)
                FailedZonePointUntil[key] = GameLoop.GameLoopTime + FailedZonePointQuarantineMilliseconds;
        }

        private bool TryRepairAuditedCrossingSource(GameBot bot, DbZonePoint crossing)
        {
            // Already inside the real activation radius: use the portal now,
            // instead of continually resetting a valid crossing for minor Z drift.
            if (bot != null && crossing != null && AtRegionCrossing(bot, crossing,
                    Distance(bot.X, bot.Y, crossing.SourceX, crossing.SourceY))) return false;
            if (bot?.CurrentRegion == null || crossing == null ||
                !AutonomousRouteHotspotRepair.TryResolveAuditedCrossingSource(
                    PathfindingProvider.Instance, bot.CurrentRegion, crossing,
                    new Vector3(bot.X, bot.Y, bot.Z), out Vector3 floor) ||
                Vector3.DistanceSquared(new Vector3(bot.X, bot.Y, bot.Z), floor) <= 4)
                return false;

            ushort region = bot.CurrentRegionID;
            int oldX = bot.X, oldY = bot.Y, oldZ = bot.Z;
            bot.StopMovingOnPath();
            bot.StopMoving();
            if (!bot.MoveTo(region, (int)Math.Round(floor.X), (int)Math.Round(floor.Y),
                    (int)Math.Round(floor.Z), bot.Heading))
                return false;

            bot.TryConsumeAutonomousPathFailure(out _, out _);
            bot.ForcePathReplot();
            ResetRouteOrderState();
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Recovery);
            bot.MarkAutonomousStateDirty();
            AutonomousBotStatusPersistence.Queue(bot);
            Log.Warn($"AUTONOMOUS_CROSSING_SOURCE_RECOVERY bot=\"{bot.Name}\" id={bot.DatabaseID} " +
                     $"edge={crossing.Id} from={region}:{oldX},{oldY},{oldZ} " +
                     $"to={region}:{bot.X},{bot.Y},{bot.Z} goal=\"{bot.PersistentRecord?.CurrentGoal}\"");
            return true;
        }

        private static double EstimateTravelMinutes(GameBot bot, ushort regionId, int x, int y)
        {
            if (bot.CurrentRegionID == regionId)
                return Distance(bot.X, bot.Y, x, y) / Math.Max(1d, bot.MaxSpeed) / 60d;

            DbZonePoint crossing = FindNextCrossing(bot, regionId, x, y);
            if (crossing == null)
                return 9999;
            double firstLeg = Distance(bot.X, bot.Y, crossing.SourceX, crossing.SourceY) / Math.Max(1d, bot.MaxSpeed) / 60d;
            double finalBias = crossing.TargetRegion == regionId
                ? Distance(crossing.TargetX, crossing.TargetY, x, y) / Math.Max(1d, bot.MaxSpeed) / 60d
                : 6;
            return firstLeg + finalBias + 1;
        }

        /// <summary>
        /// EstimateTravelMinutes for many bots toward one point: the next
        /// region crossing depends only on realm and region, so it is resolved
        /// once per pair (without per-bot quarantine) and reused.
        /// </summary>
        public static double EstimateTravelMinutes(GameBot bot, ushort regionId, int x, int y,
            Dictionary<(eRealm Realm, ushort Region), DbZonePoint> crossings)
        {
            if (bot.CurrentRegionID == regionId)
                return Distance(bot.X, bot.Y, x, y) / Math.Max(1d, bot.MaxSpeed) / 60d;
            var key = (bot.Realm, bot.CurrentRegionID);
            if (!crossings.TryGetValue(key, out DbZonePoint crossing))
                crossings[key] = crossing = FindNextCrossing(bot.Realm, bot.CurrentRegionID, regionId, x, y);
            if (crossing == null)
                return 9999;
            double firstLeg = Distance(bot.X, bot.Y, crossing.SourceX, crossing.SourceY) / Math.Max(1d, bot.MaxSpeed) / 60d;
            double finalBias = crossing.TargetRegion == regionId
                ? Distance(crossing.TargetX, crossing.TargetY, x, y) / Math.Max(1d, bot.MaxSpeed) / 60d
                : 6;
            return firstLeg + finalBias + 1;
        }

        private static int Distance(int ax, int ay, int bx, int by) =>
            (int)Math.Sqrt(DistanceSquared(ax, ay, bx, by));

        public static bool IsRouteDestinationReached(Vector3 current, Vector3 destination, bool preciseArrival = false) =>
            preciseArrival ? Vector3.DistanceSquared(current, destination) <=
                AutonomousRendezvousAttendance.EndpointTolerance * AutonomousRendezvousAttendance.EndpointTolerance :
            Vector2.DistanceSquared(new(current.X, current.Y), new(destination.X, destination.Y)) <= 48 * 48 &&
            MathF.Abs(current.Z - destination.Z) <= 96;

        private static long DistanceSquared(int ax, int ay, int bx, int by)
        {
            long dx = (long)ax - bx;
            long dy = (long)ay - by;
            return dx * dx + dy * dy;
        }
    }
}

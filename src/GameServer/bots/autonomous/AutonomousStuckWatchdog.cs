using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Reflection;
using DOL.AI.Brain;
using DOL.Logging;

namespace DOL.GS;

public enum eAutonomousProgressKind
{
    Movement,
    Combat,
    Experience,
    RealmPoints,
    Loot,
    Inventory,
    Money,
    Training,
    Crafting,
    ServiceInteraction,
    Objective,
    StableTravel,
    Recovery,
    SiegeParticipation,
}

/// <summary>
/// Detects genuinely stuck autonomous world actors. Movement and goal outcome
/// clocks are deliberately independent: walking cannot hide a fruitless goal,
/// and combat/casting cannot hide a character that has stopped moving.
/// </summary>
public static class AutonomousStuckWatchdog
{
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    public static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan GoalProgressThreshold = TimeSpan.FromMinutes(45);
    private const int MeaningfulMovementDistance = 96;
    private static readonly TimeSpan PositionValidationInterval = TimeSpan.FromSeconds(30);
    private const float PositionSnapRange = 768f;
    private const float MaximumGroundDelta = 96f;

    private sealed class Observation
    {
        public ushort RegionId;
        public int X;
        public int Y;
        public int Z;
        public long Experience;
        public long RealmPoints;
        public long MoneyCopper;
        public string Goal = string.Empty;
        public DateTime LastMovementUtc;
    }

    private sealed class GoalObservation
    {
        public string AssignmentId = string.Empty;
        public string Goal = string.Empty;
        public string Target = string.Empty;
        public DateTime LastProgressUtc;
    }

    public readonly record struct CapitalLocation(
        string Name,
        ushort RegionId,
        int X,
        int Y,
        int Z,
        ushort Heading);

    private static readonly ConcurrentDictionary<long, Observation> Observations = new();
    private static readonly ConcurrentDictionary<long, GoalObservation> GoalObservations = new();
    private static readonly ConcurrentDictionary<long, DateTime> LastPositionValidation = new();

    public static CapitalLocation CapitalFor(eRealm realm) => realm switch
    {
        eRealm.Albion => new("Camelot", 10, 35990, 30298, 8000, 3072),
        eRealm.Midgard => new("Jordheim", 101, 32020, 28294, 8819, 2048),
        eRealm.Hibernia => new("Tir na Nog", 201, 33197, 31200, 8000, 1024),
        _ => default,
    };

    /// <summary>Returns the capital anchor corrected onto the loaded navigation mesh.</summary>
    public static CapitalLocation SafeCapitalFor(eRealm realm)
    {
        CapitalLocation capital = CapitalFor(realm);
        Region region = WorldMgr.GetRegion(capital.RegionId);
        Zone zone = region?.GetZone(capital.X, capital.Y);
        if (zone == null || !PathfindingProvider.Instance.IsAvailable || !PathfindingProvider.Instance.HasNavmesh(zone))
            return capital;

        Vector3 position = new(capital.X, capital.Y, capital.Z);
        if (!PathfindingProvider.Instance.TrySnapToMesh(zone, ref position, PositionSnapRange))
            return capital;

        return new CapitalLocation(capital.Name, capital.RegionId, (int)Math.Round(position.X),
            (int)Math.Round(position.Y), (int)Math.Round(position.Z), capital.Heading);
    }

    /// <summary>
    /// Repairs persisted coordinates before AddToWorld. A missing zone or a
    /// substantial offset from the walkable mesh is never allowed to strand a bot.
    /// </summary>
    public static bool RepairInvalidLoginPosition(GameBot bot)
    {
        if (bot.IsAutonomousWorldBot && BotReleaseBindPoints.IsEnemyBindPosition(
                bot.CurrentRegionID, bot.X, bot.Y, bot.Z, bot.Realm))
            return RelocateToSafeCapital(bot, DateTime.UtcNow, "saved at an enemy realm bindstone", false);
        if (!TryDescribeInvalidPosition(bot, out string reason))
        {
            Zone zone = WorldMgr.GetRegion(bot.CurrentRegionID)?.GetZone(bot.X, bot.Y);
            var nav = PathfindingProvider.Instance;
            if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
            Vector3 position = new(bot.X, bot.Y, bot.Z);
            AutonomousNavigationSurface.TryFloor(nav, zone, position, out position);
            if (AutonomousZonePockets.Contains(zone.ID, position.X, position.Y, position.Z))
                reason = "saved position is in a walled-off pocket with no walkable way to real ground";
            else if (AutonomousRendezvousNavigation.HasLocalExit(nav, zone, position)) return false;
            else reason = "saved position is on an isolated walkable prop without a verified local exit";
        }

        // A handful of audited transition landings persist the correct XY but
        // a stale floor height. Repair those exact signatures before AddToWorld
        // instead of converting a healthy route into a capital recovery.
        Region savedRegion = WorldMgr.GetRegion(bot.CurrentRegionID);
        Zone savedZone = savedRegion?.GetZone(bot.X, bot.Y);
        if (AutonomousRouteHotspotRepair.TryResolveFloor(PathfindingProvider.Instance,
                savedZone, bot.CurrentRegionID, new(bot.X, bot.Y, bot.Z), out Vector3 floor))
        {
            bot.X = (int)Math.Round(floor.X);
            bot.Y = (int)Math.Round(floor.Y);
            bot.Z = (int)Math.Round(floor.Z);
            if (bot.PersistentRecord != null)
            {
                bot.PersistentRecord.ObjectiveProgress =
                    $"Corrected a known saved transition floor ({reason}); continuing the same goal";
                bot.MarkAutonomousStateDirty();
            }
            return true;
        }

        return RelocateToSafeCapital(bot, DateTime.UtcNow, $"login position invalid: {reason}", false);
    }

    public static bool ShouldRecoverMovement(
        DateTime lastMovementUtc,
        DateTime nowUtc,
        bool isAlive,
        bool inPlayerLedGroup,
        bool protectedStableTravel)
    {
        return isAlive && !inPlayerLedGroup && !protectedStableTravel &&
               nowUtc - lastMovementUtc >= StuckThreshold;
    }

    public static bool ShouldRecoverGoal(
        DateTime lastGoalProgressUtc,
        DateTime nowUtc,
        bool isAlive,
        bool inPlayerLedGroup,
        bool protectedStableTravel,
        bool isRvrRoaming = false) =>
        isAlive && !inPlayerLedGroup && !protectedStableTravel && !isRvrRoaming &&
        nowUtc - lastGoalProgressUtc >= GoalProgressThreshold;

    public static bool CountsAsGoalProgress(eAutonomousProgressKind kind) => kind is
        eAutonomousProgressKind.SiegeParticipation or
        eAutonomousProgressKind.Combat or
        eAutonomousProgressKind.Experience or
        eAutonomousProgressKind.RealmPoints or
        eAutonomousProgressKind.Loot or
        eAutonomousProgressKind.Inventory or
        eAutonomousProgressKind.Money or
        eAutonomousProgressKind.Training or
        eAutonomousProgressKind.Crafting or
        eAutonomousProgressKind.ServiceInteraction;

    public static string GenerateFreshGoal(long botId, int recoveryCount)
    {
        string[] goals =
        [
            "Find a reachable level-appropriate XP camp",
            "Check training, then find an XP-bearing grind spot",
            "Look for a compatible nearby group, then hunt XP-bearing monsters",
            "Clear inventory needs, then travel to a reachable XP camp",
            "Reassess safe camps and choose a new XP-bearing monster target",
        ];
        int index = (int)((botId + Math.Max(0, recoveryCount)) % goals.Length);
        return goals[index];
    }

    public static void Register(GameBot bot, DateTime? nowUtc = null)
    {
        if (bot?.IsAutonomousWorldBot != true || bot.DatabaseID <= 0)
            return;

        DateTime now = nowUtc ?? DateTime.UtcNow;
        Observations.TryAdd(bot.DatabaseID, Capture(bot, now));
        GoalObservations.TryAdd(bot.DatabaseID, new GoalObservation
        {
            AssignmentId = bot.PersistentRecord?.ObjectiveAssignmentId ?? string.Empty,
            Goal = bot.PersistentRecord?.CurrentGoal ?? string.Empty,
            Target = bot.PersistentRecord?.TargetName ?? string.Empty,
            LastProgressUtc = now,
        });
        bot.PersistentRecord.LastMeaningfulProgressUtc = now.ToString("O");
        bot.MarkAutonomousStateDirty();
    }

    public static void Unregister(GameBot bot)
    {
        if (bot?.DatabaseID > 0)
        {
            Observations.TryRemove(bot.DatabaseID, out _);
            GoalObservations.TryRemove(bot.DatabaseID, out _);
            LastPositionValidation.TryRemove(bot.DatabaseID, out _);
            GroupHoldCache.TryRemove(bot.DatabaseID, out _);
        }
    }

    public static void MarkProgress(GameBot bot, eAutonomousProgressKind kind, DateTime? nowUtc = null)
    {
        if (bot?.IsAutonomousWorldBot != true || bot.DatabaseID <= 0 || bot.PersistentRecord == null)
            return;

        DateTime now = nowUtc ?? DateTime.UtcNow;
        Observation current = Capture(bot, now);
        Observation observation = Observations.GetOrAdd(bot.DatabaseID, current);
        CopyOutcomeSnapshot(observation, current);
        if (kind is eAutonomousProgressKind.Movement or eAutonomousProgressKind.StableTravel or eAutonomousProgressKind.Recovery or eAutonomousProgressKind.SiegeParticipation)
        {
            CopyPositionSnapshot(observation, current);
            observation.LastMovementUtc = now;
        }
        // Earned XP, realm points and coin are the same hard liveness evidence
        // that Observe honours for stationary casters, archers and pet classes.
        // Observe compares against the snapshot copied just above, so it can no
        // longer see this change; refresh the liveness clock here instead or a
        // bot killing steadily from one spot is "rescued" after fifteen minutes.
        // (Combat alone still does not count: it cannot hide a stuck bot.)
        else if (kind is eAutonomousProgressKind.Experience or eAutonomousProgressKind.RealmPoints or eAutonomousProgressKind.Money)
            observation.LastMovementUtc = now;
        if (CountsAsGoalProgress(kind) || kind is eAutonomousProgressKind.Recovery)
        {
            GoalObservation goal = GoalObservations.GetOrAdd(bot.DatabaseID, _ => new GoalObservation());
            goal.AssignmentId = bot.PersistentRecord.ObjectiveAssignmentId ?? string.Empty;
            goal.Goal = bot.PersistentRecord.CurrentGoal ?? string.Empty;
            goal.Target = bot.PersistentRecord.TargetName ?? string.Empty;
            goal.LastProgressUtc = now;
        }
        bot.PersistentRecord.LastMeaningfulProgressUtc = now.ToString("O");
        if (kind == eAutonomousProgressKind.StableTravel && !string.IsNullOrWhiteSpace(bot.StableRouteDestination))
            bot.PersistentRecord.ObjectiveProgress = $"Riding stable route to {bot.StableRouteDestination}";
        bot.MarkAutonomousStateDirty();
    }

    /// <summary>
    /// Called from the ordinary bot think loop. Returning true prevents the old
    /// FSM goal from executing again in the same tick after recovery.
    /// </summary>
    public static bool Observe(GameBot bot, DateTime? nowUtc = null)
    {
        if (bot?.IsAutonomousWorldBot != true || bot.DatabaseID <= 0 || bot.PersistentRecord == null ||
            bot.ObjectState != GameObject.eObjectState.Active)
            return false;

        DateTime now = nowUtc ?? DateTime.UtcNow;
        // The stable route is the authoritative mover. Do not mesh-snap or
        // relocate a rider while the horse is traversing route geometry.
        if (bot.IsProtectedStableMasterTravel(now))
        {
            MarkProgress(bot, eAutonomousProgressKind.StableTravel, now);
            return false;
        }

        if (AutonomousRealmRaid.Protects(bot) || AutonomousRvrEventLayer.ProtectsParticipantFromInactivity(bot, GameLoop.GameLoopTime))
        {
            // Refresh both clocks so an ended rally/battle does not immediately
            // inherit fifteen or forty-five minutes of legitimate waiting/fighting.
            MarkProgress(bot, eAutonomousProgressKind.SiegeParticipation, now);
            return false;
        }

        if (ShouldValidatePosition(bot.DatabaseID, now) && TryDescribeInvalidPosition(bot, out string invalidReason))
        {
            // Three audited route pockets retain an obsolete hillside/road Z
            // even though the same XY lies over a connected floor. Repair only
            // those bounded signatures locally; every other invalid position
            // keeps the existing safe-capital recovery rules.
            if (TryRepairKnownRouteHotspot(bot, now, invalidReason))
                return true;

            // Bridges, shelves, stairs and stable geometry can legitimately be
            // above the nearest ground polygon. A vertical-only mismatch is
            // not enough to teleport a moving bot to its capital; require the
            // ordinary fifteen-minute no-movement evidence as well. Missing
            // regions/zones/mesh remain immediate hard position failures.
            bool verticalOnly = invalidReason.StartsWith("vertical offset", StringComparison.Ordinal);
            bool movementStalled = Observations.TryGetValue(bot.DatabaseID, out Observation observed) &&
                now - observed.LastMovementUtc >= StuckThreshold;
            if (!verticalOnly || movementStalled)
                return RelocateToSafeCapital(bot, now, $"live position invalid: {invalidReason}", true);
        }

        Observation current = Capture(bot, now);
        Observation previous = Observations.GetOrAdd(bot.DatabaseID, current);

        bool withHuman = bot.IsPlayerLedGroup || bot.Group?.GetPlayersInTheGroup().Count > 0;
        GoalObservation goalObservation = GoalObservations.GetOrAdd(bot.DatabaseID, _ => new GoalObservation
        {
            AssignmentId = bot.PersistentRecord.ObjectiveAssignmentId ?? string.Empty,
            Goal = bot.PersistentRecord.CurrentGoal ?? string.Empty,
            Target = bot.PersistentRecord.TargetName ?? string.Empty,
            LastProgressUtc = now,
        });
        string currentGoal = bot.PersistentRecord.CurrentGoal ?? string.Empty;
        string currentTarget = bot.PersistentRecord.TargetName ?? string.Empty;
        string assignmentId = bot.PersistentRecord.ObjectiveAssignmentId ?? string.Empty;
        goalObservation.LastProgressUtc = GoalClockForAssignment(goalObservation.LastProgressUtc,
            goalObservation.AssignmentId, assignmentId, now);
        goalObservation.AssignmentId = assignmentId;
        goalObservation.Goal = currentGoal;
        goalObservation.Target = currentTarget;

        bool outcomeProgress = previous.Experience != current.Experience ||
                               previous.RealmPoints != current.RealmPoints ||
                               previous.MoneyCopper != current.MoneyCopper;
        bool moved = HasMeaningfulMovement(previous, current);
        CopyOutcomeSnapshot(previous, current);
        if (moved)
        {
            CopyPositionSnapshot(previous, current);
            previous.LastMovementUtc = now;
            bot.PersistentRecord.LastMeaningfulProgressUtc = now.ToString("O");
            bot.MarkAutonomousStateDirty();
        }
        if (outcomeProgress)
        {
            goalObservation.LastProgressUtc = now;
            // XP, coin, or realm points are hard liveness evidence. A caster,
            // archer, pet class, or dungeon party can legitimately keep killing
            // from one position, so do not classify that as a movement stall.
            previous.LastMovementUtc = now;
            bot.PersistentRecord.LastMeaningfulProgressUtc = now.ToString("O");
            bot.MarkAutonomousStateDirty();
        }

        // Matchmaking and group coordination have their own explicit deadlines.
        // Refresh both generic watchdog clocks during these intentional holds so
        // neither the 15-minute movement clock nor the 45-minute outcome clock
        // can race the coordinator and tear down a valid party.  Once a hold
        // ends, both clocks restart from that point instead of inheriting wait time.
        // A bot waiting for matchmaking now grinds solo meanwhile, so it gets
        // the ordinary watchdog; a genuine freeze during that grind is caught.
        if (GroupProtectsFromWatchdog(bot, now) ||
            IsGroupFormationHold(bot.PersistentRecord.Activity) ||
            AutonomousObjectiveAssignments.IsIntentionalTownIdle(bot.PersistentRecord, now) &&
            AutonomousWorldBotController.IsAtAssignedIdleTown(bot))
        {
            previous.LastMovementUtc = now;
            CopyPositionSnapshot(previous, current);
            goalObservation.LastProgressUtc = now;
            return false;
        }

        bool isRvrRoaming = AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR);
        if (ShouldRecoverGoal(goalObservation.LastProgressUtc, now, bot.IsAlive, withHuman, false, isRvrRoaming))
        {
            return RelocateToSafeCapital(bot, now,
                $"no completed goal outcome for forty-five minutes (goal: {currentGoal}; target: {currentTarget})", true,
                "AUTONOMOUS_GOAL_STALL_45M_RECOVERY");
        }

        if (!ShouldRecoverMovement(previous.LastMovementUtc, now, bot.IsAlive, withHuman, false))
            return false;

        return RelocateToSafeCapital(bot, now, "no movement for fifteen minutes", true,
            "AUTONOMOUS_STUCK_15M_RECOVERY");
    }

    // The group check takes the coordinator's global lock, which every grouped
    // actor's Pulse also holds. These clocks are 15 and 45 minutes long, so a
    // two-second-old answer is equivalent; the session samples its own shared
    // progress at most every five seconds anyway.
    private static readonly TimeSpan GroupHoldCacheDuration = TimeSpan.FromSeconds(2);
    private static readonly ConcurrentDictionary<long, (DateTime ValidUntil, bool Protects)> GroupHoldCache = new();

    private static bool GroupProtectsFromWatchdog(GameBot bot, DateTime now)
    {
        if (bot.Group == null)
        {
            GroupHoldCache.TryRemove(bot.DatabaseID, out _);
            return false;
        }
        if (GroupHoldCache.TryGetValue(bot.DatabaseID, out var cached) && now < cached.ValidUntil && now >= cached.ValidUntil - GroupHoldCacheDuration)
            return cached.Protects;
        bool protects = AutonomousBotGroupCoordinator.ProtectsFromIndividualWatchdog(bot);
        GroupHoldCache[bot.DatabaseID] = (now + GroupHoldCacheDuration, protects);
        return protects;
    }

    public static bool IsGroupFormationHold(string activity) =>
        string.Equals(activity, "Formed up at rendezvous", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(activity, "Holding group formation", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(activity, "Staging outside dungeon", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(activity, "Forming inside dungeon entrance", StringComparison.OrdinalIgnoreCase) ||
        // Siege musters wait at their rally post until the army marches; the muster has its own deadline.
        // Run 15: 23 of 64 musterers were relocated to a capital mid-muster by the 15-minute clock.
        activity?.StartsWith("Mustering at ", StringComparison.OrdinalIgnoreCase) == true;

    // Changing the displayed route/camp after a failure is not progress. Only
    // a genuinely new timed assignment gets a fresh clock, otherwise repeated
    // rejected camps could suppress the 45-minute recovery indefinitely.
    public static DateTime GoalClockForAssignment(DateTime lastProgressUtc, string previousId, string currentId, DateTime now) =>
        !string.Equals(previousId ?? string.Empty, currentId ?? string.Empty, StringComparison.Ordinal)
            ? now : lastProgressUtc;

    private static bool RelocateToSafeCapital(
        GameBot bot,
        DateTime now,
        string reason,
        bool moveLiveObject,
        string logMarker = "AUTONOMOUS_POSITION_RECOVERY")
    {
        CapitalLocation capital = SafeCapitalFor(bot.Realm);
        if (capital.RegionId == 0)
            return false;

        ushort oldRegion = bot.CurrentRegionID;
        int oldX = bot.X;
        int oldY = bot.Y;
        int oldZ = bot.Z;
        string oldGoal = bot.PersistentRecord?.CurrentGoal ?? string.Empty;
        string oldTarget = bot.PersistentRecord?.TargetName ?? string.Empty;
        string oldActivity = bot.PersistentRecord?.Activity ?? string.Empty;
        string oldDestination = bot.PersistentRecord?.TravelDestination ?? string.Empty;
        var expedition = AutonomousRealmRaid.GetView(bot.Group);

        if (moveLiveObject)
        {
            // A real unrecoverable member failure ends the locked party as one
            // operation, with an explicit cause, rather than silently removing
            // one actor and leaving seven stale group assignments behind.
            if (expedition == null) AutonomousBotGroupCoordinator.EndPvePartyBeforeIndividualRecovery(bot, reason);
            bot.TempProperties.GetProperty<GameRelic>(GameRelic.PLAYER_CARRY_RELIC_WEAK)?.DropFromCarrier(bot);
            if (expedition == null) bot.Group?.RemoveMember(bot);
            bot.CompleteStableMasterRoute();
            bot.StopAttack();
            bot.StopFollowing();
            bot.StopMovingOnPath();
            bot.StopMoving();
            // A stuck cast must not survive the recovery move (stefanrows fork, Minstrel mez).
            bot.StopCurrentSpellcast();
            if (bot.Brain is BotBrain brain)
            {
                brain.ClearAggroList();
                brain.FSM.SetCurrentState(eFSMStateType.IDLE);
            }

            if (!bot.MoveTo(capital.RegionId, capital.X, capital.Y, capital.Z, capital.Heading))
                return false;
        }
        else
        {
            bot.CurrentRegionID = capital.RegionId;
            bot.X = capital.X;
            bot.Y = capital.Y;
            bot.Z = capital.Z;
            bot.Heading = capital.Heading;
        }

        AutonomousGoalDiagnostics.End(bot, logMarker switch
        {
            "AUTONOMOUS_GOAL_STALL_45M_RECOVERY" => GoalAttemptEnd.NoProgress45m,
            "AUTONOMOUS_STUCK_15M_RECOVERY" => GoalAttemptEnd.NoMovement15m,
            _ => GoalAttemptEnd.InvalidPosition
        }, reason, (oldRegion, oldX, oldY, oldZ));
        OfflineWorldBotRecord record = bot.PersistentRecord;
        record.RecoveryCount++;
        record.CurrentCampId = string.Empty;
        record.ItineraryJson = string.Empty;
        record.Activity = "Recovered to safe capital spawn";
        record.CurrentGoal = expedition == null ? GenerateFreshGoal(bot.DatabaseID, record.RecoveryCount) : $"Rejoin {expedition.Camp.MonsterName}";
        record.TargetName = string.Empty;
        record.TravelDestination = string.Empty;
        if (expedition == null)
            AutonomousObjectiveAssignments.BeginSoloAfterCapitalRecovery(bot,
                $"Capital recovery {record.RecoveryCount}: selecting a reachable solo task");
        else
            AutonomousRealmRaid.RejoinAfterRelease(bot);
        record.ObjectiveProgress = $"Position recovery {record.RecoveryCount}: {reason}; returned to {capital.Name}; " +
            (expedition == null ? "selected a different goal" : "retained expedition membership and returning to the encounter");
        record.LastMeaningfulProgressUtc = now.ToString("O");
        Log.Warn($"{logMarker} bot=\"{ForLog(bot.Name)}\" id={bot.DatabaseID} level={bot.Level} " +
                 $"realm={GlobalConstants.RealmToName(bot.Realm)} class=\"{ForLog(bot.ClassName)}\" reason=\"{ForLog(reason)}\" " +
                 $"goal=\"{ForLog(oldGoal)}\" target=\"{ForLog(oldTarget)}\" activity=\"{ForLog(oldActivity)}\" " +
                 $"destination=\"{ForLog(oldDestination)}\" recovery={record.RecoveryCount} " +
                 $"from={oldRegion}:{oldX},{oldY},{oldZ} to={capital.RegionId}:{capital.X},{capital.Y},{capital.Z}");
        MarkProgress(bot, eAutonomousProgressKind.Recovery, now);
        if (moveLiveObject)
        {
            // Recovery changes position and goal state, not inventory. Persist
            // that state through the existing coalesced writer instead of
            // blocking the NPC service while rewriting every inventory slot.
            // Real inventory/equipment/coin and exchange mutations retain their
            // own includeInventory persistence and are untouched here.
            bot.MarkAutonomousStateDirty();
            AutonomousBotStatusPersistence.Queue(bot);
        }
        return true;
    }

    private static bool TryRepairKnownRouteHotspot(GameBot bot, DateTime now, string reason)
    {
        Region region = WorldMgr.GetRegion(bot.CurrentRegionID);
        Zone zone = region?.GetZone(bot.X, bot.Y);
        Vector3 current = new(bot.X, bot.Y, bot.Z);
        if (!AutonomousRouteHotspotRepair.TryResolveFloor(PathfindingProvider.Instance,
                zone, bot.CurrentRegionID, current, out Vector3 floor))
            return false;

        ushort regionId = bot.CurrentRegionID;
        int oldX = bot.X, oldY = bot.Y, oldZ = bot.Z;
        bot.StopMovingOnPath();
        bot.StopMoving();
        if (!bot.MoveTo(regionId, (int)Math.Round(floor.X), (int)Math.Round(floor.Y),
                (int)Math.Round(floor.Z), bot.Heading))
            return false;

        bot.TryConsumeAutonomousPathFailure(out _, out _);
        bot.ForcePathReplot();
        bot.PersistentRecord.ObjectiveProgress =
            $"Corrected a known local route surface ({reason}); continuing the same goal";
        Log.Warn($"AUTONOMOUS_LOCAL_SURFACE_RECOVERY bot=\"{ForLog(bot.Name)}\" id={bot.DatabaseID} " +
                 $"region={regionId} from={oldX},{oldY},{oldZ} to={bot.X},{bot.Y},{bot.Z} " +
                 $"reason=\"{ForLog(reason)}\"");
        MarkProgress(bot, eAutonomousProgressKind.Recovery, now);
        bot.MarkAutonomousStateDirty();
        AutonomousBotStatusPersistence.Queue(bot);
        return true;
    }

    private static bool ShouldValidatePosition(long botId, DateTime now)
    {
        DateTime previous = LastPositionValidation.GetOrAdd(botId, DateTime.MinValue);
        if (now - previous < PositionValidationInterval)
            return false;
        LastPositionValidation[botId] = now;
        return true;
    }

    private static bool TryDescribeInvalidPosition(GameBot bot, out string reason)
    {
        reason = string.Empty;
        Region region = WorldMgr.GetRegion(bot.CurrentRegionID);
        if (region == null)
        {
            reason = $"region {bot.CurrentRegionID} is unavailable";
            return true;
        }

        Zone zone = region.GetZone(bot.X, bot.Y);
        if (zone == null)
        {
            reason = $"coordinates {bot.X},{bot.Y},{bot.Z} are outside every zone";
            return true;
        }

        if (!PathfindingProvider.Instance.IsAvailable || !PathfindingProvider.Instance.HasNavmesh(zone))
            return false;

        Vector3 snapped = new(bot.X, bot.Y, bot.Z);
        if (!PathfindingProvider.Instance.TrySnapToMesh(zone, ref snapped, PositionSnapRange))
        {
            reason = $"no walkable mesh exists within {PositionSnapRange:0} units";
            return true;
        }

        float verticalDelta = Math.Abs(snapped.Z - bot.Z);
        if (verticalDelta <= MaximumGroundDelta)
            return false;

        reason = $"vertical offset from walkable ground is {verticalDelta:0} units (mesh Z {snapped.Z:0})";
        return true;
    }

    private static Observation Capture(GameBot bot, DateTime now) => new()
    {
        RegionId = bot.CurrentRegionID,
        X = bot.X,
        Y = bot.Y,
        Z = bot.Z,
        Experience = bot.Experience,
        RealmPoints = bot.AutonomousRealmPoints,
        MoneyCopper = bot.PersistentRecord?.MoneyCopper ?? 0,
        Goal = bot.PersistentRecord?.CurrentGoal ?? string.Empty,
        LastMovementUtc = now,
    };

    private static bool HasMeaningfulMovement(Observation previous, Observation current)
    {
        if (previous.RegionId != current.RegionId)
            return true;

        long dx = (long)current.X - previous.X;
        long dy = (long)current.Y - previous.Y;
        long dz = (long)current.Z - previous.Z;
        return dx * dx + dy * dy + dz * dz >= MeaningfulMovementDistance * MeaningfulMovementDistance;
    }

    private static void CopyPositionSnapshot(Observation destination, Observation source)
    {
        destination.RegionId = source.RegionId;
        destination.X = source.X;
        destination.Y = source.Y;
        destination.Z = source.Z;
    }

    private static void CopyOutcomeSnapshot(Observation destination, Observation source)
    {
        destination.Experience = source.Experience;
        destination.RealmPoints = source.RealmPoints;
        destination.MoneyCopper = source.MoneyCopper;
        destination.Goal = source.Goal;
    }

    private static string ForLog(string value) =>
        (value ?? string.Empty).Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
}

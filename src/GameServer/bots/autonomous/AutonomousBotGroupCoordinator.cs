using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using DOL.Logging;
using DOL.AI.Brain;
using DOL.Database;

namespace DOL.GS;

/// <summary>
/// Low-frequency coordination for persistent bot-created parties.  Combat and
/// movement still use the normal world actors; this class only gives those
/// actors one rendezvous, one camp and one difficulty history.
/// </summary>
public static partial class AutonomousBotGroupCoordinator
{
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    public const string MetadataPrefix = "offline-group-v1:";
    // Members must actually reach their own slot. The former 135-unit test
    // declared a pile at the rendezvous center "assembled" even though every
    // intended formation point was still inside that broad radius.
    private const int CohesionRadius = 500;
    private const int MaximumPveRecruitmentDistance = 30_000;
    // A verified broken route gets one bounded retry. Initial attendance then
    // sheds that no-show; an active locked party ends its task instead of
    // holding every survivor in a permanent regroup.
    public const long UnreachableRouteRetryMilliseconds = 60_000;
    public const long UnreachableRouteGraceMilliseconds = 2 * 60_000L;
    public const long LeaderStagingTimeoutMilliseconds = 20 * 60_000L;
    private static readonly object Sync = new();
    private static readonly Dictionary<Group, Session> Sessions = new();
    // Callers hold Sync; the membership property is evaluated just once here.
    private static bool TryGetSession(Group group, out Session session)
    {
        session = null;
        return group != null && Sessions.TryGetValue(group, out session);
    }
    private static long _nextGroupPveFormationTick;
    private static long _nextRvrFormationTick;
    private static long _lastMaintenanceTick = long.MinValue;
    private static long _maintenanceRetryTick;
    private static int _nextGroupNumber;
    private static readonly string RuntimeGroupToken = Guid.NewGuid().ToString("N")[..6];

    // eRealm.Albion and eRealm._FirstPlayerRealm share the value 1, so ToString()
    // named Albion groups "_firstplayerrealm-..." in the launcher and the logs.
    public static string SessionId(eRealm realm, int number) =>
        $"{GlobalConstants.RealmToName(realm).ToLowerInvariant()}-{RuntimeGroupToken}-{number:000}";

    public sealed record SharedCamp(string Id, string MonsterName, string ZoneName, ushort RegionId, int X, int Y, int Z, bool IsDungeon, bool IsFrontier, int TargetLevel = 0);
    public sealed record Directive(string GroupId, string Phase, string SharedGoal, string Status, eAutonomousObjectiveKind ObjectiveKind, GameBot Leader,
        Vector3 Rendezvous, SharedCamp Camp, int BotMemberCount, int AverageLevel, int WipePenalty,
        int PreferredLevelBonus, bool IsDynamic)
    {
        public ushort RendezvousRegion { get; init; }
        public string LeaderName { get; init; } = string.Empty;
        public string RendezvousName { get; init; } = string.Empty;
        public GameBot Puller { get; init; }
        public bool LeaderReadyForAssembly { get; init; }
        public bool GroupCombatActive { get; init; }
        public bool RecoveringBetweenPulls { get; init; }
    }

    public sealed class StoredMetadata
    {
        public string GroupId { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public string SharedGoal { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool HasTaskClock { get; set; }
        public bool TaskTimerPaused { get; set; }
        public long TaskRemainingMilliseconds { get; set; }
        public string TaskExpiresUtc { get; set; } = string.Empty;
        public string MeetUpDeadlineUtc { get; set; } = string.Empty;
        public string LeaderName { get; set; } = string.Empty;
        public string RendezvousName { get; set; } = string.Empty;
        public string PullerName { get; set; } = string.Empty;
        public string MemberRole { get; set; } = string.Empty;
    }

    private sealed class Session
    {
        public required Group Group { get; init; }
        // Keep one authoritative leader while the actor is alive and still in
        // this party. Group.LivingLeader can change transiently while persistent
        // bots cross a region boundary one at a time; following that transient
        // value split dungeon parties and could deadlock them on opposite sides
        // of an aggressive corridor blocker.
        public required GameBot Leader { get; set; }
        public required string Id { get; init; }
        public required Vector3 Rendezvous { get; set; }
        public required AutonomousGroupTaskClock TaskClock { get; init; }
        public string Phase { get; set; } = "Leader staging";
        // Meetup overhaul (2026-10-06): the leader plans the camp while the party forms; the party
        // meets at the safe town nearest that camp, every member travelling at once.
        public bool MeetupPlanned { get; set; }
        public SharedCamp PlannedCamp { get; set; }
        public string RendezvousName { get; set; } = string.Empty;
        public bool LeaderReadyForAssembly { get; set; }
        public bool CombatObserved { get; set; }
        public string RaidMusterEvent { get; set; }
        public bool RecoveringBetweenPulls { get; set; }
        public ushort DungeonArrivalRegion { get; set; }
        public Vector3 DungeonInteriorStagingPoint { get; set; }
        public int DungeonExteriorEdge { get; set; }
        public ushort DungeonExteriorRegion { get; set; }
        public Dictionary<long, Vector3> DungeonExteriorSlots { get; } = new();
        public long DungeonArrivalHoldUntilTick { get; set; }
        public string DungeonArrivalCompletedCampId { get; set; } = string.Empty;
        public SharedCamp Camp { get; set; }
        public Dictionary<string, long> RejectedDungeonCamps { get; } = new(StringComparer.Ordinal);
        public int WipePenalty { get; set; }
        public long? NoCampSinceTick { get; set; }
        public AutonomousGroupRecoveryState Recovery { get; } = new();
        public bool RecoveryRendezvousChosen { get; set; }
        public long NextRecoveryReadinessTick { get; set; }
        public int LockedSize { get; set; }
        public ushort RendezvousRegion { get; set; }
        public AutonomousRendezvousAttendance Attendance { get; } = new();
        public Dictionary<long, Vector3> RendezvousSlots { get; } = new();
        public HashSet<long> HeldUnreachableMembers { get; } = new();
        public Dictionary<long, long> UnreachableSince { get; } = new();
        public HashSet<long> UnreachableRetried { get; } = new();
        public Dictionary<long, long> ExpeditionRouteRetry { get; } = new();
        public Dictionary<long, int> ExpeditionRouteFailures { get; } = new();
        public bool RendezvousReselectionAttempted { get; set; }
        public long NextAttendanceTick { get; set; }
        public long LeaderStagingDeadlineTick { get; set; }
        public DateTime LeaderStagingDeadlineUtc { get; set; }
        public bool ProcessingAttendanceRemovals { get; set; }
        public int PreferredLevelBonus { get; set; }
        public Dictionary<long, BotPveGroupRole> PveRoles { get; } = new();
        /// <summary>Roles of raid members released from this party, filled first by replacements.</summary>
        public List<BotPveGroupRole> ReleasedRaidRoles { get; } = new();
        public GameBot Puller { get; set; }
        public bool Ending { get; set; }
        public long SharedWatchdogProgressTick { get; set; }
        public long SharedWatchdogSampleTick { get; set; }
        public long SharedWatchdogExperience { get; set; }
        public Vector3 SharedWatchdogPosition { get; set; }
        public ushort SharedWatchdogRegion { get; set; }
        public Dictionary<long, (int Deaths, long SinceTick)> CorpseCombatClearSince { get; } = new();
        public string PhaseBeforeCasualty { get; set; } = string.Empty;
        public eAutonomousObjectiveKind ObjectiveKind { get; init; }
        public Directive PublishedDirective;
        public GameBot[] PublishedMembers = [];
        public long PublishedAttendanceRevision = -1;
        public long? PublishedDeadlineTick;
        public long PublishedRemaining;
    }

    /// <summary>Population-wide work belongs to one service phase, not every
    /// member of every group. Own-party validity is still checked on each pulse.</summary>
    public static void PrepareCoordinatorTick()
    {
        long now = GameLoop.GameLoopTime;
        if (Interlocked.Read(ref _lastMaintenanceTick) == now || now < Interlocked.Read(ref _maintenanceRetryTick)) return;
        try
        {
            RealmEventNotices.Pulse(now);
            RealmEventControls.Pulse();
            AutonomousRealmRaid.Pulse(now);
            AutonomousRealmRaid.RecruitForcedParty(now);
            AutonomousRvrEventLayer.PulseDefense(now);
            RealmWarbandSupport.Pulse(now);
            AutonomousObjectiveAssignments.ReconcileIfDue();
            lock (Sync)
            {
                if (_lastMaintenanceTick == now) return;
                Interlocked.Exchange(ref _lastMaintenanceTick, now);
                // The population-wide sweep ran every game-loop tick (~30/s) and rebuilt every
                // party's member array each time: the largest allocator on the server (117 MB in
                // 40 s, run 8 trace). Each party still checks itself on its own pulse.
                if (now >= _nextBrokenSessionSweep)
                {
                    _nextBrokenSessionSweep = now + BrokenSessionSweepMilliseconds;
                    RemoveBrokenSessions();
                }
                FormSiegeParties();
                TryFormGroups();
            }
        }
        catch (Exception exception)
        {
            // A bad party/route used to fail one brain turn, not the server.
            // Keep that isolation after moving housekeeping to the phase owner.
            Interlocked.Exchange(ref _maintenanceRetryTick, now + 5_000);
            Log.Error("Population group maintenance failed; per-party combat remains active and maintenance will retry.", exception);
        }
    }

    public static string RvrForceId(GameBot bot)
    {
        lock (Sync)
        {
            return bot.Group != null && TryGetSession(bot.Group, out Session session) &&
                session.ObjectiveKind == eAutonomousObjectiveKind.RvR ? session.Id : $"rvr-{bot.DatabaseID}";
        }
    }

    public static Directive Pulse(GameBot bot)
    {
        if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper)
            return null;

        PrepareCoordinatorTick();
        lock (Sync)
        {
            if (bot.Group != null && TryGetSession(bot.Group, out Session currentSession))
                RemoveBrokenSession(currentSession.Group, currentSession);
            if (bot.Group == null)
            {
                ClearMetadata(bot, false);
            }

            Group group = bot.Group;
            if (group == null)
                return null;

            GameBot[] members = BotMembers(group);
            if (members.Length == 0)
                return null;

            // Objective membership is authoritative.  A rebalance can never
            // leave a mixed PvE/RvR party running under the leader's old task.
            if (!group.GetMembersInTheGroup().Any(member => member is GamePlayer))
            {
                eAutonomousObjectiveKind purpose = AutonomousObjectiveAssignments.KindFor(members[0]);
                if (members.Any(member => AutonomousObjectiveAssignments.KindFor(member) != purpose))
                {
                    group.DisbandGroup();
                    return null;
                }
            }

            // Player-led parties are visible in the launcher, but the human is
            // deliberately not serialized or included in the bot count.
            if (group.GetMembersInTheGroup().Any(member => member is GamePlayer))
            {
                string key = $"player-{RuntimeHelpers.GetHashCode(group):x8}";
                string goal = "Assist the player-led party";
                string status = members.Any(member => member.InCombat) ? "Fighting with player" : "Following player formation";
                foreach (GameBot member in members)
                    SetMetadata(member, key, "Player-led", goal, status);
                return new(key, "Player-led", goal, status, eAutonomousObjectiveKind.SoloPve, members[0], new(bot.X, bot.Y, bot.Z), null,
                    members.Length, (int)Math.Round(members.Average(member => member.Level)), 0, 0, false);
            }

            if (!TryGetSession(group, out Session session))
            {
                GameBot leader = members.FirstOrDefault(member => member == group.LivingLeader) ?? members[0];
                session = NewSession(group, leader, objectiveKind: AutonomousObjectiveAssignments.KindFor(leader));
                if (session == null)
                {
                    group.DisbandGroup();
                    foreach (GameBot member in members)
                        AutonomousObjectiveAssignments.BeginSoloAfterGroupTask(member, "No connected rendezvous is available");
                    return null;
                }
                Sessions[group] = session;
            }

            if (!UpdateSession(session, members))
                return null;
            members = BotMembers(session.Group);
            if (bot.Group != session.Group || members.Length == 0)
                return null;
            return BuildDirective(session, members);
        }
    }

    /// <summary>
    /// RvR intentionally retains independent roamer/hunter bots.  Unlike
    /// GroupPve, its whole assigned cohort is not eligible for party formation;
    /// this prevents repeated formation passes from asymptotically grouping all
    /// compatible bots in a realm.
    /// </summary>
    public static int MaximumGroupedForObjective(eAutonomousObjectiveKind objectiveKind, int rosterCount) =>
        objectiveKind == eAutonomousObjectiveKind.RvR
            ? Math.Max(0, rosterCount - MinimumSoloRvrReserve(rosterCount))
            : Math.Max(0, rosterCount);

    public static int MinimumSoloRvrReserve(int rosterCount) => rosterCount < 4
        ? Math.Max(0, rosterCount - 2)
        : Math.Max(2, (int)Math.Ceiling(rosterCount * 0.35d));

    public static int AvailableGroupSlotsForObjective(eAutonomousObjectiveKind objectiveKind, int realmRosterCount, int realmGroupedCount) =>
        Math.Max(0, MaximumGroupedForObjective(objectiveKind, realmRosterCount) - realmGroupedCount);

    public static bool ShouldContinueFormationSearch(eAutonomousObjectiveKind objectiveKind, int candidateRealmSlots) =>
        objectiveKind == eAutonomousObjectiveKind.RvR || candidateRealmSlots >= 2;

    public static void PublishCamp(GameBot bot, SharedCamp camp)
    {
        if (bot?.Group == null || camp == null)
            return;
        lock (Sync)
        {
            if (!TryGetSession(bot.Group, out Session session) ||
                IsAssemblyPhase(session.Phase) || session.Recovery.IsRegrouping)
                return;
            GameBot[] members = BotMembers(bot.Group);
            GameBot leader = ChooseLeader(session, members);
            if (leader != bot)
                return;
            AssignCamp(session, camp, members);
            WriteSessionMetadata(session, members);
        }
    }

    private static void AssignCamp(Session session, SharedCamp camp, GameBot[] members)
    {
        session.Camp = camp;
        session.DungeonArrivalRegion = 0;
        session.DungeonInteriorStagingPoint = default;
        session.DungeonArrivalHoldUntilTick = 0;
        session.DungeonArrivalCompletedCampId = string.Empty;
        session.NoCampSinceTick = null;
        session.Phase = "Traveling";
        StartTaskClock(session, members);
    }

    /// <summary>
    /// Called once by the leader of a forming PvE party with the camp it would choose (null when none).
    /// The rendezvous moves to the safe town nearest that camp when one is closer than the original
    /// town and valid for every member, and every member travels at once (no leader-first staging).
    /// Run 8: followers waited in place for the leader, then the assembled party travelled a median
    /// 102k units to its first camp, two thirds of them in another region.
    /// </summary>
    public static bool PlanMeetupNearCamp(GameBot leader, SharedCamp camp)
    {
        if (leader?.Group == null) return false;
        lock (Sync)
        {
            if (!TryGetSession(leader.Group, out Session session) || session.MeetupPlanned ||
                session.ObjectiveKind != eAutonomousObjectiveKind.GroupPve || session.Phase != "Leader staging" ||
                AutonomousRealmRaid.GetView(session.Group) != null)
                return false;
            GameBot[] members = BotMembers(leader.Group);
            if (members.Length < 2 || ChooseLeader(session, members) != leader) return false;
            session.MeetupPlanned = true;
            session.PlannedCamp = camp;
            string where = session.RendezvousName;
            if (camp != null && WorldMgr.GetRegion(camp.RegionId) is Region campRegion)
            {
                Vector3 campPoint = new(camp.X, camp.Y, camp.Z);
                float current = session.RendezvousRegion == camp.RegionId ? Vector3.Distance(session.Rendezvous, campPoint) : float.MaxValue;
                if (TryChooseTownNear(leader, members, campRegion, campPoint, out Vector3 point, out string name) &&
                    Vector3.Distance(point, campPoint) + 5_000 < current)
                {
                    Vector3 previous = session.Rendezvous;
                    string previousName = session.RendezvousName;
                    ushort previousRegion = session.RendezvousRegion;
                    session.Rendezvous = point;
                    session.RendezvousName = name;
                    session.RendezvousRegion = camp.RegionId;
                    if (TryBuildRendezvousSlots(session, members))
                        where = name;
                    else
                    {
                        session.Rendezvous = previous;
                        session.RendezvousName = previousName;
                        session.RendezvousRegion = previousRegion;
                        TryBuildRendezvousSlots(session, members);
                    }
                }
            }
            session.Phase = "Meeting up";
            session.LeaderReadyForAssembly = true;
            RebaseAttendance(session, members);
            Log.Info($"AUTONOMOUS_GROUP_MEETUP_PLANNED group={session.Id} town=\"{where}\" region={session.RendezvousRegion} " +
                     $"camp=\"{camp?.MonsterName ?? "none"}\" campRegion={camp?.RegionId ?? 0} " +
                     $"campDistance={(camp != null && camp.RegionId == session.RendezvousRegion ? (int)Vector3.Distance(session.Rendezvous, new(camp.X, camp.Y, camp.Z)) : -1)}");
            WriteSessionMetadata(session, members);
            return true;
        }
    }

    private static bool TryChooseTownNear(GameBot leader, GameBot[] members, Region region, Vector3 camp,
        out Vector3 point, out string name)
    {
        point = default;
        name = string.Empty;
        GameNPC[] towns = region.Objects.OfType<GameNPC>()
            .Where(npc => npc.ObjectState == GameObject.eObjectState.Active &&
                          npc is GameMerchant or GameTrainer or GameStableMaster &&
                          (npc.Realm == leader.Realm || npc.Realm == eRealm.None) && IsTownArea(npc) &&
                          Vector3.DistanceSquared(camp, new(npc.X, npc.Y, npc.Z)) <= 80_000L * 80_000L)
            .OrderBy(npc => Vector3.DistanceSquared(camp, new(npc.X, npc.Y, npc.Z)))
            .Take(6).ToArray();
        var nav = PathfindingProvider.Instance;
        foreach (GameNPC npc in towns)
        {
            Zone zone = region.GetZone(npc.X, npc.Y);
            if (!AutonomousRendezvousNavigation.TryChoosePoint(nav, zone, new(npc.X, npc.Y, npc.Z), out Vector3 chosen)) continue;
            bool tight = zone?.IsDungeon == true;
            bool valid = true;
            foreach (GameBot member in members)
            {
                Vector3 desired = DesiredFormationPoint(member, chosen, tight);
                if (!AutonomousRendezvousNavigation.TryResolveFormationSlot(nav, zone, chosen, desired, out Vector3 slot) ||
                    member.CurrentRegion == region && !AutonomousRendezvousNavigation.CanReachFormationSlot(nav, region,
                        member.CurrentZone, zone, new(member.X, member.Y, member.Z), slot))
                { valid = false; break; }
            }
            if (!valid) continue;
            point = chosen;
            name = TownName(npc);
            return true;
        }
        return false;
    }

    public static void ReportNoAvailableCamp(GameBot bot)
    {
        lock (Sync)
        {
            if (bot?.Group == null || !TryGetSession(bot.Group, out Session session) ||
                session.ObjectiveKind != eAutonomousObjectiveKind.GroupPve || session.Camp != null ||
                session.Phase != "Choosing group target" || ChooseLeader(session, BotMembers(session.Group)) != bot)
                return;
            session.NoCampSinceTick ??= GameLoop.GameLoopTime;
            // Only after the normal level fallback is exhausted. Do not leave
            // an assembled party waiting forever on a clock that never started.
            if (GameLoop.GameLoopTime - session.NoCampSinceTick.Value >= 120_000)
                FinishGroupTask(session, "No reachable non-grey group camp after lower-level fallbacks and two minutes of retries");
        }
    }

    /// <summary>
    /// A formed PvE party owns one target for the life of that task. Normal
    /// respawn gaps are waiting periods, not permission to send the party
    /// to a different camp or out of a dungeon.
    /// </summary>
    public static bool ShouldHoldCampThroughRespawn(Directive directive) =>
        directive?.IsDynamic == true && directive.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
        directive.Camp != null;

    public static void ReportCampTemporarilyEmpty(GameBot bot, long waitedMilliseconds)
    {
        if (bot?.Group == null)
            return;
        lock (Sync)
        {
            if (!TryGetSession(bot.Group, out Session session) || session.Camp == null ||
                session.ObjectiveKind != eAutonomousObjectiveKind.GroupPve)
                return;
            GameBot[] members = BotMembers(session.Group);
            Log.Info("AUTONOMOUS_GROUP_CAMP_WAIT " + JsonSerializer.Serialize(new
            {
                group = session.Id,
                phase = session.Phase,
                camp = session.Camp.Id,
                target = session.Camp.MonsterName,
                zone = session.Camp.ZoneName,
                waitedSeconds = Math.Max(0, waitedMilliseconds / 1000),
                members = members.Select(member => new
                {
                    name = member.Name,
                    role = session.PveRoles.TryGetValue(MemberKey(member), out BotPveGroupRole role)
                        ? BotPartyRoles.GroupRoleLabel(role) : "unknown",
                    alive = member.IsAlive,
                    region = member.CurrentRegionID,
                    x = member.X,
                    y = member.Y,
                    z = member.Z
                }).ToArray()
            }));
        }
    }

    /// <summary>
    /// Moves the shared search anchor to another verified spawn cell for the
    /// same named objective. The camp identity and task clock never change;
    /// the leader simply takes the formation to another part of that camp.
    /// </summary>
    public static bool TryRepositionEmptyCamp(GameBot bot, string campId, Vector3 point, long waitedMilliseconds)
    {
        if (bot?.Group == null)
            return false;
        lock (Sync)
        {
            if (!TryGetSession(bot.Group, out Session session) || session.Camp?.Id != campId ||
                session.ObjectiveKind != eAutonomousObjectiveKind.GroupPve)
                return false;
            GameBot[] members = BotMembers(session.Group);
            if (ChoosePuller(session, members) != bot || !HasActivePveComposition(session, members))
                return false;

            SharedCamp previous = session.Camp;
            session.Camp = previous with
            {
                X = (int)Math.Round(point.X),
                Y = (int)Math.Round(point.Y),
                Z = (int)Math.Round(point.Z)
            };
            session.Phase = "Traveling";
            Log.Info("AUTONOMOUS_GROUP_CAMP_REPOSITIONED " + JsonSerializer.Serialize(new
            {
                group = session.Id,
                camp = previous.Id,
                target = previous.MonsterName,
                zone = previous.ZoneName,
                waitedSeconds = Math.Max(0, waitedMilliseconds / 1000),
                from = new { previous.X, previous.Y, previous.Z },
                to = new { session.Camp.X, session.Camp.Y, session.Camp.Z }
            }));
            WriteSessionMetadata(session, members);
            return true;
        }
    }

    public static HashSet<string> RejectedDungeonCamps(GameBot bot)
    {
        lock (Sync)
        {
            if (bot?.Group == null || !TryGetSession(bot.Group, out Session session))
                return new(StringComparer.Ordinal);
            foreach (string id in session.RejectedDungeonCamps.Where(pair => pair.Value <= GameLoop.GameLoopTime).Select(pair => pair.Key).ToArray())
                session.RejectedDungeonCamps.Remove(id);
            return new(session.RejectedDungeonCamps.Keys, StringComparer.Ordinal);
        }
    }

    public static void RejectDungeonCamp(GameBot bot, string campId)
    {
        RejectUnreachableCamp(bot, campId);
    }

    public static void RejectUnreachableCamp(GameBot bot, string campId, string reason = "")
    {
        if (AutonomousRealmRaid.GetView(bot?.Group) != null) return;
        lock (Sync)
        {
            if (bot?.Group == null || !TryGetSession(bot.Group, out Session session) || session.Camp?.Id != campId)
                return;
            SharedCamp rejected = session.Camp;
            Log.Warn("AUTONOMOUS_GROUP_CAMP_REJECTED " + JsonSerializer.Serialize(new
            {
                group = session.Id,
                bot = bot.Name,
                camp = rejected.Id,
                target = rejected.MonsterName,
                zone = rejected.ZoneName,
                reason = string.IsNullOrWhiteSpace(reason) ? "Camp route was rejected" : reason,
                region = bot.CurrentRegionID,
                x = bot.X,
                y = bot.Y,
                z = bot.Z
            }));
            session.RejectedDungeonCamps[campId] = GameLoop.GameLoopTime + 30 * 60_000;
            // Feed the shared route quarantine too: one group's rejection only lasted for that
            // group, so other groups kept drawing the same unroutable camp (2,574 rejections
            // Oct 3-5, mostly Hibernia Shrouded Isles). Three different reporters bench it.
            if (AutonomousCampRouteQuarantine.ReportFailure(campId, bot.DatabaseID))
                Log.Warn($"AUTONOMOUS_CAMP_ROUTE_QUARANTINE camp=\"{campId}\" target=\"{rejected.MonsterName}\" hours={AutonomousCampRouteQuarantine.QuarantineDuration.TotalHours:0} reason=group-camp-rejected");
            session.Camp = null;
            session.DungeonArrivalRegion = 0;
            session.DungeonInteriorStagingPoint = default;
            session.DungeonArrivalHoldUntilTick = 0;
            session.DungeonArrivalCompletedCampId = string.Empty;
            // Change the target, not the party or its already-running clock.
            if (!session.Recovery.IsRegrouping && !IsAssemblyPhase(session.Phase))
                session.Phase = "Choosing group target";
            WriteSessionMetadata(session, BotMembers(session.Group));
        }
    }

    public static void MarkGrinding(GameBot bot)
    {
        if (bot?.Group == null)
            return;
        lock (Sync)
        {
            if (!TryGetSession(bot.Group, out Session session) || session.Camp == null || session.Recovery.IsRegrouping)
                return;
            session.Phase = "Grinding";
            WriteSessionMetadata(session, BotMembers(bot.Group));
        }
    }

    public static bool IsCohesive(Directive directive)
    {
        if (AutonomousRealmRaid.GetView(directive?.Leader?.Group) != null) return true;
        if (directive?.IsDynamic != true || directive.Leader == null)
            return true;
        long now = GameLoop.GameLoopTime;
        return BotMembers(directive.Leader.Group).Where(member => member.IsAlive)
            .All(member => member.CurrentRegionID == directive.Leader.CurrentRegionID &&
                (member.GetDistanceTo(directive.Leader) is var distance && distance <= CohesionRadius ||
                 AutonomousGroupMemberStall.Release(member, directive.Leader, distance, CohesionRadius, now)));
    }

    /// <summary>How far the farthest living member trails the leader; MaxValue when one is in another region.</summary>
    public static float FarthestMemberDistance(Directive directive)
    {
        GameBot leader = directive?.Leader;
        if (leader == null) return 0;
        float farthest = 0;
        long now = GameLoop.GameLoopTime;
        foreach (GameBot member in BotMembers(leader.Group))
        {
            if (!member.IsAlive || member == leader) continue;
            if (member.CurrentRegionID != leader.CurrentRegionID) return float.MaxValue;
            float distance = member.GetDistanceTo(leader);
            // A member stuck for 45 seconds no longer holds the group (see AutonomousGroupMemberStall).
            if (AutonomousGroupMemberStall.Release(member, leader, distance, CohesionRadius, now)) continue;
            farthest = Math.Max(farthest, distance);
        }
        return farthest;
    }

    public static bool IsRecovering(GameBot bot)
    {
        if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup || bot.Group == null)
            return false;
        lock (Sync)
            return TryGetSession(bot.Group, out Session session) && session.Recovery.IsRegrouping;
    }

    public static bool IsInitialMeetup(GameBot bot)
    {
        if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup || bot.Group == null)
            return false;
        lock (Sync)
            return TryGetSession(bot.Group, out Session session) && IsAssemblyPhase(session.Phase);
    }

    /// <summary>
    /// The group coordinator, rather than the individual watchdog, owns every
    /// intentional stationary phase.  Active travel and ordinary grinding are
    /// deliberately excluded so genuine route stalls remain recoverable.
    /// </summary>
    public static bool ProtectsFromIndividualWatchdog(GameBot bot)
    {
        if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper ||
            bot.IsPlayerLedGroup)
            return false;
        // Solo actors can never hold a session. Answer without the global
        // coordinator lock, which every grouped actor's Pulse also contends for.
        if (bot.Group == null)
            return false;
        lock (Sync)
        {
            // Membership can change between the brain thread and coordinator.
            // Capture once: a second property read could become null mid-check.
            Group group = bot.Group;
            if (group == null || !Sessions.TryGetValue(group, out Session session))
                return false;
            // Support members need not earn individual XP/coin or move while
            // their intact party advances. Observe the shared outcome at most
            // once per five seconds; never let a distant stranded member borrow
            // this protection, and never extend a genuinely idle party forever.
            GameBot[] party = BotMembers(group);
            if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
                session.Phase is "Traveling" or "Grinding" && HasActivePveComposition(session, party))
            {
                long now = GameLoop.GameLoopTime;
                GameBot anchor = ChooseLeader(session, party);
                if (anchor != null && now >= session.SharedWatchdogSampleTick)
                {
                    long experience = party.Sum(member => member.PersistentRecord?.Experience ?? 0L);
                    Vector3 position = new(anchor.X, anchor.Y, anchor.Z);
                    if (session.SharedWatchdogProgressTick == 0 || experience != session.SharedWatchdogExperience ||
                        anchor.CurrentRegionID != session.SharedWatchdogRegion ||
                        Vector3.DistanceSquared(position, session.SharedWatchdogPosition) >= 96 * 96)
                    {
                        session.SharedWatchdogProgressTick = now;
                        session.SharedWatchdogPosition = position;
                        session.SharedWatchdogRegion = anchor.CurrentRegionID;
                    }
                    session.SharedWatchdogExperience = experience;
                    session.SharedWatchdogSampleTick = now + 5_000;
                }
                if (anchor != null && HasRecentSharedWatchdogProgress(now, session.SharedWatchdogProgressTick,
                    bot.CurrentRegionID == anchor.CurrentRegionID, bot.GetDistanceTo(anchor)))
                    return true;
            }
            // A waiting leader is not the stranded member. The shared task
            // deadline and the missing member's watchdog still terminate a
            // genuinely failed party; do not teleport its anchor away first.
            if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
                session.Phase is "Traveling" or "Grinding" && bot == ChooseLeader(session, BotMembers(group)) &&
                bot.PersistentRecord?.Activity == "Waiting for group members")
                return true;
            if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
                bot.CurrentRegionID == session.DungeonExteriorRegion &&
                bot.PersistentRecord?.Activity == "Staged outside dungeon" &&
                session.DungeonExteriorSlots.TryGetValue(MemberKey(bot), out Vector3 stagedSlot) &&
                AutonomousRendezvousAttendance.IsAtSlot(new(bot.X, bot.Y, bot.Z), stagedSlot))
                return true;
            if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
                session.Phase == "Traveling" &&
                bot.PersistentRecord?.Activity == "Waiting for group leader after crossing")
                return true;
            bool combatActive = BotMembers(group).Any(member => member.InCombat || member.IsAttacking ||
                (member.Brain as BotBrain)?.HasAggro == true);
            return IsProtectedWatchdogPhase(session.Phase, session.Recovery.IsRegrouping,
                session.RecoveringBetweenPulls, combatActive);
        }
    }

    public static bool IsProtectedWatchdogPhase(string phase, bool regrouping,
        bool recoveringBetweenPulls, bool combatActive) =>
        IsAssemblyPhase(phase) || regrouping || recoveringBetweenPulls || combatActive ||
        phase is "Choosing group target" or "Waiting for resurrection";

    public static bool HasRecentSharedWatchdogProgress(long now, long progressTick, bool sameRegion, int distance) =>
        sameRegion && distance <= CohesionRadius && progressTick > 0 && now >= progressTick &&
        now - progressTick < 15 * 60_000L;

    public static void EndPvePartyBeforeIndividualRecovery(GameBot bot, string reason)
    {
        if (bot?.IsAutonomousWorldBot != true || bot.IsPlayerLedGroup || bot.Group == null) return;
        if (AutonomousRealmRaid.GetView(bot.Group) != null) return;
        lock (Sync)
        {
            if (TryGetSession(bot.Group, out Session session) && !session.Ending &&
                session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve)
                FinishGroupTask(session, $"Verified individual recovery for {bot.Name}: {reason}");
        }
    }

    public static bool CanCrossDungeonEntrance(GameBot bot, Directive directive, DbZonePoint crossing,
        out string reason)
    {
        reason = string.Empty;
        if (AutonomousRealmRaid.GetView(bot?.Group) != null) return true;
        if (bot?.Group == null || directive?.IsDynamic != true || directive.Camp?.IsDungeon != true ||
            crossing == null || crossing.TargetRegion != directive.Camp.RegionId ||
            crossing.SourceRegion == crossing.TargetRegion)
            return true;

        lock (Sync)
        {
            if (!TryGetSession(bot.Group, out Session session) || session.Id != directive.GroupId)
                return false;
            GameBot[] members = BotMembers(bot.Group).Where(member => member.IsAlive).ToArray();
            var transit = members.Select(member => new AutonomousDungeonPolicy.GroupTransitMember(
                member.CurrentRegionID, new(member.X, member.Y, member.Z), member.IsOnStableMasterRoute)).ToArray();
            bool ready = AutonomousDungeonPolicy.GroupReadyForDungeonEntrance(crossing.SourceRegion,
                crossing.TargetRegion, new(crossing.SourceX, crossing.SourceY, crossing.SourceZ), transit);
            if (!ready)
            {
                int staged = transit.Count(member => member.Region == crossing.SourceRegion &&
                    Vector2.DistanceSquared(new(member.Position.X, member.Position.Y),
                        new(crossing.SourceX, crossing.SourceY)) <=
                    AutonomousDungeonPolicy.DungeonEntranceStagingRadius *
                    AutonomousDungeonPolicy.DungeonEntranceStagingRadius);
                reason = $"Waiting outside for the complete party ({staged}/{members.Length} staged)";
                return false;
            }

            if (session.DungeonArrivalRegion != crossing.TargetRegion)
            {
                session.DungeonArrivalRegion = crossing.TargetRegion;
                session.DungeonInteriorStagingPoint = new(crossing.TargetX, crossing.TargetY, crossing.TargetZ);
                session.DungeonArrivalHoldUntilTick = 0;
                Log.Info($"AUTONOMOUS_GROUP_DUNGEON_ENTRY_STARTED group={session.Id} dungeon={crossing.TargetRegion} " +
                         $"members={members.Length} source={crossing.SourceRegion}:{crossing.SourceX},{crossing.SourceY},{crossing.SourceZ}");
            }
            return true;
        }
    }

    public static bool TryGetDungeonExteriorStaging(GameBot bot, Directive directive, DbZonePoint crossing,
        out Vector3 staging)
    {
        staging = default;
        if (AutonomousRealmRaid.GetView(bot?.Group) != null) return false;
        if (bot?.Group == null || directive?.IsDynamic != true || directive.Camp?.IsDungeon != true ||
            crossing == null || crossing.SourceRegion == crossing.TargetRegion ||
            crossing.TargetRegion != directive.Camp.RegionId || bot == directive.Leader)
            return false;
        lock (Sync)
        {
            if (!TryGetSession(bot.Group, out Session session) || session.Id != directive.GroupId)
                return false;
            GameBot[] members = BotMembers(bot.Group).Where(member => member.IsAlive)
                .OrderBy(MemberKey).ToArray();
            if (members.Any(member => member.CurrentRegionID == crossing.TargetRegion))
                return false;
            if (session.DungeonExteriorEdge != crossing.Id || session.DungeonExteriorRegion != crossing.SourceRegion)
            {
                session.DungeonExteriorEdge = crossing.Id;
                session.DungeonExteriorRegion = crossing.SourceRegion;
                session.DungeonExteriorSlots.Clear();
            }
            if (session.DungeonExteriorSlots.TryGetValue(MemberKey(bot), out staging))
                return true;
            int index = Array.IndexOf(members, bot);
            Region sourceRegion = WorldMgr.GetRegion(crossing.SourceRegion);
            Zone zone = sourceRegion?.GetZone(crossing.SourceX, crossing.SourceY);
            if (index < 0 || zone == null)
                return false;
            Vector3 rawCenter = new(crossing.SourceX, crossing.SourceY, crossing.SourceZ);
            if (!AutonomousRendezvousNavigation.TryChooseFixedPoint(PathfindingProvider.Instance,
                    zone, rawCenter, out Vector3 center))
                return false;
            double angle = Math.PI * 2d * index / Math.Max(1, members.Length);
            int radius = 230 + (Math.Abs(crossing.Id) % 4) * 25;
            Vector3 desired = new(center.X + (float)(Math.Cos(angle) * radius),
                center.Y + (float)(Math.Sin(angle) * radius), center.Z);
            if (!AutonomousRendezvousNavigation.TryResolveFormationSlot(PathfindingProvider.Instance,
                    zone, center, desired, out staging))
                return false;
            session.DungeonExteriorSlots[MemberKey(bot)] = staging;
            return true;
        }
    }

    public static bool ShouldHoldDungeonArrival(GameBot bot, Directive directive, out string reason)
    {
        reason = string.Empty;
        if (AutonomousRealmRaid.GetView(bot?.Group) != null) return false;
        if (bot?.Group == null || directive?.IsDynamic != true || directive.Camp?.IsDungeon != true ||
            bot.CurrentRegionID != directive.Camp.RegionId || directive.GroupCombatActive)
            return false;

        lock (Sync)
        {
            if (!TryGetSession(bot.Group, out Session session) || session.Id != directive.GroupId)
                return false;
            if (string.Equals(session.DungeonArrivalCompletedCampId, directive.Camp.Id, StringComparison.Ordinal))
                return false;
            GameBot[] members = BotMembers(bot.Group).Where(member => member.IsAlive).ToArray();
            int inside = members.Count(member => member.CurrentRegionID == directive.Camp.RegionId);
            if (inside < members.Length)
            {
                reason = $"Holding at the inside arrival point while the party loads ({inside}/{members.Length} inside)";
                return true;
            }

            Vector3 staging = session.DungeonInteriorStagingPoint;
            if (staging != default)
            {
                var transit = members.Select(member => new AutonomousDungeonPolicy.GroupTransitMember(
                    member.CurrentRegionID, new(member.X, member.Y, member.Z), member.IsOnStableMasterRoute)).ToArray();
                if (!AutonomousDungeonPolicy.GroupReadyAtInteriorStaging(
                        directive.Camp.RegionId, staging, transit))
                {
                    int staged = transit.Count(member => member.Region == directive.Camp.RegionId &&
                        Math.Abs(member.Position.Z - staging.Z) <= 500 &&
                        Vector2.DistanceSquared(new(member.Position.X, member.Position.Y),
                            new(staging.X, staging.Y)) <= 350 * 350);
                    reason = $"Gathering at the protected inside staging point ({staged}/{members.Length} ready)";
                    return true;
                }
            }

            if (session.DungeonArrivalRegion != directive.Camp.RegionId)
                session.DungeonArrivalRegion = directive.Camp.RegionId;
            session.DungeonArrivalHoldUntilTick = session.DungeonArrivalHoldUntilTick == 0
                ? GameLoop.GameLoopTime + 5_000
                : session.DungeonArrivalHoldUntilTick;
            bool upkeepSettling = members.Any(member => member.IsCasting) ||
                                  GameLoop.GameLoopTime < session.DungeonArrivalHoldUntilTick;
            bool recovered = MembersFullyRecovered(members);
            if (upkeepSettling || !recovered)
            {
                reason = upkeepSettling
                    ? "Party is rebuffing and settling pets before the first pull"
                    : "Party is restoring HP, power, and endurance before the first pull";
                return true;
            }

            session.DungeonArrivalRegion = 0;
            session.DungeonInteriorStagingPoint = default;
            session.DungeonArrivalHoldUntilTick = 0;
            session.DungeonArrivalCompletedCampId = directive.Camp.Id;
            Log.Info($"AUTONOMOUS_GROUP_DUNGEON_READY group={session.Id} dungeon={directive.Camp.RegionId} members={members.Length}");
            return false;
        }
    }

    public static bool TryGetDungeonInteriorStaging(GameBot bot, Directive directive, out Vector3 staging)
    {
        staging = default;
        if (bot?.Group == null || directive?.IsDynamic != true || directive.Camp?.IsDungeon != true)
            return false;
        lock (Sync)
        {
            return TryGetSession(bot.Group, out Session session) &&
                   session.Id == directive.GroupId &&
                   session.DungeonArrivalRegion == directive.Camp.RegionId &&
                   (staging = session.DungeonInteriorStagingPoint) != default;
        }
    }

    public static bool IsCompletingDungeonEntry(GameBot bot, Directive directive)
    {
        if (bot?.Group == null || directive?.IsDynamic != true || directive.Camp?.IsDungeon != true ||
            bot.CurrentRegionID == directive.Camp.RegionId)
            return false;
        lock (Sync)
            return TryGetSession(bot.Group, out Session session) && session.Id == directive.GroupId &&
                session.DungeonArrivalRegion == directive.Camp.RegionId &&
                BotMembers(bot.Group).Any(member => member.CurrentRegionID == directive.Camp.RegionId);
    }

    public static void MarkCombatObserved(Group group)
    {
        if (group == null) return;
        lock (Sync)
        {
            if (!Sessions.TryGetValue(group, out Session session)) return;
            session.CombatObserved = true;
            session.RecoveringBetweenPulls = false;
        }
    }

    /// <summary>
    /// A non-puller can be the first party member to see a mandatory dungeon
    /// corridor blocker (normally the leader at the front of the formation).
    /// Give that real NPC to the locked PvE puller so the party does not wait
    /// forever with the tank following a stationary leader.  Combat, damage,
    /// aggro, death and loot continue through the normal BotBrain.
    /// </summary>
    public static bool TryHandoffDungeonBlocker(GameBot reporter, GameNPC blocker, out GameBot puller)
    {
        puller = null;
        if (reporter?.Group == null || blocker?.IsAlive != true ||
            reporter.IsAutonomousWorldBot != true || reporter.IsTemporaryGroupHelper)
            return false;

        lock (Sync)
        {
            if (!TryGetSession(reporter.Group, out Session session))
                return false;

            GameBot[] members = BotMembers(session.Group);
            puller = ChoosePuller(session, members);
            if (puller == null || puller.Brain is not BotBrain ||
                !AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve,
                    session.Phase, members.Length,
                    HasActivePveComposition(session, members),
                    puller != reporter, puller.IsAlive,
                    puller.CurrentRegionID == reporter.CurrentRegionID,
                    puller.IsOnStableMasterRoute))
            {
                puller = null;
                return false;
            }
        }

        // This retains every normal between-pull gate: the current locked
        // roster, no casualty, full resources, tight formation, and tank puller.
        if (!CanInitiateNewPull(puller, corridorBlocker: true) || puller.Brain is not BotBrain brain)
        {
            puller = null;
            return false;
        }

        bool savageMeleePull = SavageBotCombatPolicy.MustMeleePull(
            (eCharacterClass)puller.CharacterClass.ID);
        if (savageMeleePull && puller.TargetObject == blocker &&
            brain.GetBaseAggroAmount(blocker) > 0 &&
            brain.FSM.GetCurrentState()?.StateType == eFSMStateType.AGGRO)
            return true;
        // An actual fight has priority over a follower's corridor report.
        // Never replace a Savage's current enemy with a new elective pull.
        if (savageMeleePull && (brain.HasAggro || puller.InCombat || puller.IsAttacking))
        {
            puller = null;
            return false;
        }
        if (savageMeleePull &&
            AutonomousWorldBotController.IsSavageMeleePullHandoffCoolingDown(puller, blocker))
        {
            puller = null;
            return false;
        }
        if (AutonomousDefensivePull.TryBegin(puller, blocker)) return true;
        puller.TargetObject = blocker;
        brain.AddToAggroList(blocker, Math.Max(25, blocker.EffectiveLevel * 10));
        brain.CommitDungeonPull(blocker);
        brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
        if (savageMeleePull)
        {
            AutonomousWorldBotController.QueueSavageMeleePullHandoff(puller, blocker);
            brain.NextThinkTick = GameLoop.GameLoopTime;
        }
        MarkCombatObserved(puller.Group);
        return true;
    }

    public static bool IsAssemblyPhase(string phase) =>
        phase is "Leader staging" or "Meeting up";

    // A casualty regroup must travel to the rendezvous before the party can
    // recover together. Holding for full resources here strands survivors in
    // the dungeon after their released group members return to town.
    public static bool ShouldPauseForGroupPullRecovery(string phase, bool groupCombatActive,
        bool recoveringBetweenPulls) =>
        phase != "Regrouping" && !IsAssemblyPhase(phase) &&
        (groupCombatActive || recoveringBetweenPulls);

    public static string PhaseAfterCasualty(string previousPhase, bool regrouping) =>
        regrouping ? "Regrouping" : previousPhase is "Traveling" or "Grinding" or "Choosing group target"
            ? previousPhase : "Traveling";

    public static bool HasLeaderStagingTimedOut(string phase, long deadlineTick, long nowTick) =>
        phase == "Leader staging" && deadlineTick > 0 && nowTick >= deadlineTick;

    public static int PreferredPullerIndex(IReadOnlyList<BotPartyRole> roles, int leaderIndex)
    {
        if (roles == null || roles.Count == 0)
            return -1;
        if (leaderIndex >= 0 && leaderIndex < roles.Count && roles[leaderIndex] == BotPartyRole.Tank)
            return leaderIndex;
        for (int index = 0; index < roles.Count; index++)
            if (roles[index] == BotPartyRole.Tank)
                return index;
        return Math.Clamp(leaderIndex, 0, roles.Count - 1);
    }

    public static int PreferredPveTankSlot(IReadOnlyList<int> tankLevels, bool levelFiftyGroup,
        Random random = null)
    {
        if (tankLevels == null || tankLevels.Count == 0)
            return -1;
        int highestLevel = levelFiftyGroup ? int.MinValue : tankLevels.Max();
        int[] eligible = Enumerable.Range(0, tankLevels.Count)
            .Where(index => levelFiftyGroup || tankLevels[index] == highestLevel)
            .ToArray();
        return eligible[(random ?? Random.Shared).Next(eligible.Length)];
    }

    /// <summary>At most eight actors; catches a death even between coordinator pulses.
    /// Proactive pulls use 90/80/80 readiness, but an active post-combat
    /// recovery episode still runs to full. Attacked-by-enemy responses are
    /// never gated here.</summary>
    public static bool CanInitiateNewPull(GameBot bot, bool corridorBlocker = false)
    {
        if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup || bot.Group == null)
            return true;
        lock (Sync)
        {
            Group group = bot.Group;
            if (group == null || !Sessions.TryGetValue(group, out Session session))
                return false;
            // Waiting expedition parties may defend themselves, but cannot
            // initiate a fresh pull ahead of the staging/landing order.
            if (AutonomousRealmRaid.GetTravelView(bot) is { } expedition)
            {
                if (expedition.Hold && !(corridorBlocker && expedition.Crossing)) return false;
                // Only locally present living members take part in puller
                // selection. A casualty or reinforcement across the world is
                // not a veto on every surviving party's combat.
                var expeditionPuller = ChoosePuller(session, BotMembers(group));
                return bot == expeditionPuller && bot.HealthPercent >= 70;
            }
            GameBot[] members = BotMembers(group);
            GameBot leader = ChooseLeader(session, members);
            GameBot puller = ChoosePuller(session, members);
            if (leader == null) return false;
            bool combatActor = AutonomousRvrStaging.UsesIndependentCombatActors(session.ObjectiveKind) || bot == puller;
            return !IsAssemblyPhase(session.Phase) && !session.Recovery.IsRegrouping && combatActor &&
                !session.Recovery.HasCasualty(RecoveryMembers(session, members, false)) &&
                !session.RecoveringBetweenPulls && MembersReadyForPull(members) &&
                (session.ObjectiveKind != eAutonomousObjectiveKind.GroupPve ||
                    HasActivePveComposition(session, members)) &&
                members.Length >= 2 && members.All(member => !member.IsOnStableMasterRoute &&
                    member.CurrentRegionID == leader.CurrentRegionID && member.GetDistanceTo(leader) <=
                        PullCohesionRadius(corridorBlocker, bot.CurrentZone?.IsDungeon == true));
        }
    }

    // A corridor monster can separate followers from their leader by more than
    // the normal 500-unit formation radius. Let the designated tank clear that
    // obstruction; otherwise nobody may cross it to satisfy the pull gate.
    // Ordinary camp pulls, full resources, locked roles and casualty gates stay intact.
    public static int PullCohesionRadius(bool corridorBlocker, bool inDungeon) =>
        corridorBlocker && inDungeon ? 1100 : CohesionRadius;

    private static AutonomousGroupRecoveryState.Member[] RecoveryMembers(Session session, GameBot[] members, bool readiness) =>
        members.Select(member => new AutonomousGroupRecoveryState.Member(
            member.DatabaseID > 0 ? member.DatabaseID : member.ObjectID,
            member.PersistentRecord?.DeathCount ?? 0, member.IsAlive, member.IsReturningAfterRelease,
            member.IsOnStableMasterRoute, !readiness || AtRendezvous(session, member),
            // Friendly buffs/songs may keep pulsing while formed up; they must
            // not keep an otherwise ready group in recovery forever.
            member.InCombat || member.IsAttacking || (member.Brain as BotBrain)?.HasAggro == true,
            AutonomousGroupRecoveryState.ResourcesReady(member.HealthPercent, member.ManaPercent,
                member.EndurancePercent, member.MaxMana > 0))).ToArray();

    private static bool MembersFullyRecovered(GameBot[] members) => members.Length >= 2 &&
        members.All(member => AutonomousRestPolicy.IsFullyRecovered(member.HealthPercent,
            member.ManaPercent, member.EndurancePercent, member.MaxMana > 0));

    // Full recovery is still required after a fight. A new pull outside that
    // recovery episode uses the same safe 90/80/80 threshold as regrouping:
    // passive songs, buffs and normal travel can leave a member at 99%, while
    // the puller's hold branch does not start a rest just for that last point.
    private static bool MembersReadyForPull(GameBot[] members) => members.Length >= 2 &&
        members.All(member => ReadyForGroupPull(member.HealthPercent, member.ManaPercent,
            member.EndurancePercent, member.MaxMana > 0));

    public static bool ReadyForGroupPull(int health, int power, int endurance, bool usesPower) =>
        AutonomousGroupRecoveryState.ResourcesReady(health, power, endurance, usesPower);

    public static bool ShouldRecoverAfterGroupCombat(bool alreadyRecovering,
        int health, int power, int endurance, bool usesPower) =>
        alreadyRecovering
            ? health < 100 || usesPower && power < 100 || endurance < 100
            : !ReadyForGroupPull(health, power, endurance, usesPower);

    public static Vector3 FormationPoint(GameBot bot, Vector3 center, bool tight)
    {
        Vector3 desired = DesiredFormationPoint(bot, center, tight);
        Zone zone = bot?.CurrentRegion?.GetZone((int)center.X, (int)center.Y);
        return zone != null && PathfindingProvider.Instance.IsAvailable
            ? AutonomousNavigationSurface.MoveAlongGround(PathfindingProvider.Instance,
                  zone, center, desired) ?? center
            : desired;
    }

    private static long MemberKey(GameBot bot) => bot.DatabaseID > 0 ? bot.DatabaseID : bot.ObjectID;

    private static bool TryBuildRendezvousSlots(Session session, GameBot[] members)
    {
        Region region = WorldMgr.GetRegion(session.RendezvousRegion);
        Zone zone = region?.GetZone((int)session.Rendezvous.X, (int)session.Rendezvous.Y);
        if (zone == null)
            return false;
        bool tight = zone.IsDungeon;
        var resolved = new Dictionary<long, Vector3>();
        foreach (GameBot member in members)
        {
            Vector3 desired = DesiredFormationPoint(member, session.Rendezvous, tight);
            if (!AutonomousRendezvousNavigation.TryResolveFormationSlot(PathfindingProvider.Instance,
                    zone, session.Rendezvous, desired, out Vector3 slot))
                return false;
            resolved[MemberKey(member)] = slot;
        }
        session.RendezvousSlots.Clear();
        foreach ((long key, Vector3 value) in resolved)
            session.RendezvousSlots[key] = value;
        return true;
    }

    public static Vector3 RendezvousFormationPoint(GameBot bot, string groupId, Vector3 center, bool tight)
    {
        if (bot?.Group != null)
        {
            lock (Sync)
            {
                if (TryGetSession(bot.Group, out Session session) && session.Id == groupId &&
                    session.RendezvousSlots.TryGetValue(MemberKey(bot), out Vector3 slot))
                    return slot;
            }
        }
        return FormationPoint(bot, center, tight);
    }

    public static bool IsRendezvousRouteHeld(GameBot bot, string groupId)
    {
        if (bot?.Group == null)
            return false;
        lock (Sync)
        {
            if (!TryGetSession(bot.Group, out Session session) || session.Id != groupId) return false;
            long key = MemberKey(bot);
            if (AtRendezvous(session, bot))
            {
                session.HeldUnreachableMembers.Remove(key);
                session.UnreachableSince.Remove(key);
                session.UnreachableRetried.Remove(key);
                return false;
            }
            if (session.RaidMusterEvent != null && session.ExpeditionRouteRetry.TryGetValue(key, out long retry) &&
                GameLoop.GameLoopTime >= retry)
            {
                session.ExpeditionRouteRetry.Remove(key);
                session.HeldUnreachableMembers.Remove(key);
            }
            else if (session.RaidMusterEvent == null &&
                     session.UnreachableSince.TryGetValue(key, out long firstFailure) &&
                     session.HeldUnreachableMembers.Contains(key) &&
                     GameLoop.GameLoopTime >= firstFailure + UnreachableRouteRetryMilliseconds &&
                     session.UnreachableRetried.Add(key))
            {
                // Release exactly one ordinary retry from the current floor.
                // Keep firstFailure so repeated failures cannot restart the
                // shortened attendance or regroup grace window.
                session.HeldUnreachableMembers.Remove(key);
            }
            return session.HeldUnreachableMembers.Contains(key);
        }
    }

    private static Vector3 DesiredFormationPoint(GameBot bot, Vector3 center, bool tight)
    {
        GameBot[] members = BotMembers(bot?.Group)
            .OrderBy(member => member.DatabaseID > 0 ? member.DatabaseID : member.ObjectID)
            .ToArray();
        int index = Array.IndexOf(members, bot);
        if (index < 0 || members.Length < 2)
        {
            AutonomousFormation.Offset fallback = AutonomousFormation.For(bot?.Name, tight);
            double fallbackRadians = fallback.AngleDegrees * Math.PI / 180d;
            return new(center.X + (float)(Math.Cos(fallbackRadians) * fallback.Distance),
                center.Y + (float)(Math.Sin(fallbackRadians) * fallback.Distance), center.Z);
        }

        // An even ring gives every 2-8 member group a distinct deterministic
        // slot. It avoids hash collisions and remains stable across AI turns,
        // so waiting members do not stand in a stack or continuously reshuffle.
        int radius = tight ? 48 + members.Length * 2 : 64 + members.Length * 3;
        double angle = (Math.PI * 2d * index / members.Length) +
                       (RuntimeHelpers.GetHashCode(bot.Group) & 31) * (Math.PI / 180d);
        return new(center.X + (float)(Math.Cos(angle) * radius),
            center.Y + (float)(Math.Sin(angle) * radius), center.Z);
    }

    /// <summary>Stops reissuing an impossible route. One distinct coordinator
    /// reselection and one bounded retry are allowed; only a repeat failure
    /// shortens that member's ordinary fifteen-minute attendance window.</summary>
    public static bool ReportUnreachableRendezvous(GameBot bot, string groupId, string reason)
    {
        if (bot?.Group == null || string.IsNullOrWhiteSpace(groupId))
            return false;
        lock (Sync)
        {
            Group group = bot.Group;
            if (!Sessions.TryGetValue(group, out Session session) || session.Id != groupId ||
                !IsAssemblyPhase(session.Phase) && session.Phase != "Regrouping")
                return false;

            if (session.RaidMusterEvent != null)
            {
                // Never relocate an event party to an unrelated town while
                // its expedition is counting attendance at a fixed service hub.
                long memberKey = MemberKey(bot);
                if (session.HeldUnreachableMembers.Add(memberKey))
                    Log.Warn($"REALM_RAID_HUB_ROUTE_FAILED event={session.RaidMusterEvent} group={session.Id} bot={bot.Name} region={bot.CurrentRegionID} position={bot.X},{bot.Y},{bot.Z} reason={reason}");
                int failures = session.ExpeditionRouteFailures[memberKey] = session.ExpeditionRouteFailures.GetValueOrDefault(memberKey) + 1;
                // A member stranded on a disconnected patch of navmesh retried the same
                // route every minute forever (Yrdis, Tuscaran rally, 2026-10-04: 59 times
                // in 80 minutes) and its party never counted at the hub. After a few
                // failed attempts it leaves the party (no teleport) and the other
                // members go on; the raid recruits normally around the gap.
                if (RealmRaidHubEscape.ShouldEscape(failures))
                {
                    session.ExpeditionRouteFailures.Remove(memberKey);
                    session.ExpeditionRouteRetry.Remove(memberKey);
                    session.HeldUnreachableMembers.Remove(memberKey);
                    if (session.PveRoles.Remove(memberKey, out BotPveGroupRole lostRole))
                        session.ReleasedRaidRoles.Add(lostRole);
                    AutonomousRealmRaid.ReleasePartyMember(group, bot);
                    session.ProcessingAttendanceRemovals = true;
                    try
                    {
                        group.RemoveMember(bot, retainSingleRemainingMember: true);
                        AutonomousObjectiveAssignments.BeginSoloAfterGroupTask(bot, "Released from the raid party: no connected route to the hub");
                    }
                    finally { session.ProcessingAttendanceRemovals = false; }
                    session.LockedSize = BotMembers(group).Length;
                    Log.Warn($"REALM_RAID_HUB_ROUTE_RELEASED event={session.RaidMusterEvent} group={session.Id} bot={bot.Name} region={bot.CurrentRegionID} position={bot.X},{bot.Y},{bot.Z} failures={failures}");
                    return true;
                }
                session.ExpeditionRouteRetry[memberKey] = GameLoop.GameLoopTime + 60_000;
                return true;
            }
            if (!session.RendezvousReselectionAttempted)
            {
                session.RendezvousReselectionAttempted = true;
                GameBot leader = ChooseLeader(session, BotMembers(group));
                // The old retry chose from the leader's town again, commonly
                // shifting the same blocked point by a few units. First look
                // for a reachable town near the reporter, then near the leader.
                // TryChooseRendezvous validates all same-region party members.
                // A reporter may be stranded on a raised polygon while the
                // leader's original town is also disconnected. The remaining
                // PvE members provide a few more real, validated town anchors;
                // this runs only after a route failure, never in the AI tick.
                IEnumerable<GameBot> anchors = session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve
                    ? new[] { bot, leader }.Concat(BotMembers(group).Where(member => member.IsAlive))
                    : new[] { bot, leader };
                foreach (GameBot anchor in anchors.Where(candidate => candidate != null).Distinct())
                {
                    if (!TryChooseRendezvous(anchor, session.ObjectiveKind, out Vector3 replacement,
                            out string replacementName, out ushort replacementRegion) ||
                        !IsMaterialRendezvousChange(session.RendezvousRegion, session.Rendezvous,
                            replacementRegion, replacement))
                        continue;
                    Vector3 previous = session.Rendezvous;
                    string previousName = session.RendezvousName;
                    ushort previousRegion = session.RendezvousRegion;
                    session.Rendezvous = replacement;
                    session.RendezvousName = replacementName;
                    session.RendezvousRegion = replacementRegion;
                    if (TryBuildRendezvousSlots(session, BotMembers(group)))
                    {
                        if (session.Phase == "Meeting up")
                            RebaseAttendance(session, BotMembers(group));
                        else
                            session.Attendance.Reset();
                        session.HeldUnreachableMembers.Clear();
                        session.UnreachableSince.Clear();
                        session.UnreachableRetried.Clear();
                        Log.Warn($"AUTONOMOUS_GROUP_RENDEZVOUS_RESELECTED group={session.Id} bot=\"{bot.Name}\" " +
                                 $"from={(int)previous.X},{(int)previous.Y},{(int)previous.Z} " +
                                 $"to={(int)replacement.X},{(int)replacement.Y},{(int)replacement.Z} reason=\"{reason}\"");
                        return true;
                    }
                    session.Rendezvous = previous;
                    session.RendezvousName = previousName;
                    session.RendezvousRegion = previousRegion;
                    TryBuildRendezvousSlots(session, BotMembers(group));
                }
            }

            long key = MemberKey(bot);
            if (!session.HeldUnreachableMembers.Add(key))
                return true;
            long now = GameLoop.GameLoopTime;
            session.UnreachableSince.TryAdd(key, now);
            if (session.UnreachableRetried.Contains(key))
            {
                if (session.Phase == "Meeting up")
                    session.Attendance.LimitDeadline(key, now, UnreachableRouteGraceMilliseconds);
                else if (session.Phase == "Leader staging" && bot == session.Leader &&
                         session.LeaderStagingDeadlineTick > 0)
                {
                    long limited = Math.Min(session.LeaderStagingDeadlineTick,
                        now + UnreachableRouteGraceMilliseconds);
                    if (limited < session.LeaderStagingDeadlineTick)
                    {
                        session.LeaderStagingDeadlineTick = limited;
                        session.LeaderStagingDeadlineUtc = DateTime.UtcNow.AddMilliseconds(limited - now);
                    }
                }
            }
            Log.Warn($"AUTONOMOUS_GROUP_UNREACHABLE_RENDEZVOUS group={session.Id} bot=\"{bot.Name}\" " +
                     $"id={bot.DatabaseID} region={bot.CurrentRegionID} position={bot.X},{bot.Y},{bot.Z} " +
                     $"rendezvous={session.RendezvousRegion}:{(int)session.Rendezvous.X},{(int)session.Rendezvous.Y},{(int)session.Rendezvous.Z} " +
                     $"reason=\"{reason}\"");
            return true;
        }
    }

    public static bool IsMaterialRendezvousChange(ushort previousRegion, Vector3 previous,
        ushort candidateRegion, Vector3 candidate) =>
        previousRegion != candidateRegion ||
        Vector2.DistanceSquared(new(previous.X, previous.Y), new(candidate.X, candidate.Y)) >= 128 * 128 ||
        MathF.Abs(previous.Z - candidate.Z) >= 160;

    public static void OnMemberRemoved(Group group, GameLiving living)
    {
        if (living is not GameBot bot || !bot.IsAutonomousWorldBot || bot.IsTemporaryGroupHelper)
            return;
        lock (Sync)
        {
            ClearMetadata(bot, true);
            if (group != null && Sessions.TryGetValue(group, out Session session))
            {
                if (!session.ProcessingAttendanceRemovals && !session.Ending &&
                    session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve)
                {
                    FinishGroupTask(session, $"{bot.Name} left the locked PvE party");
                    return;
                }
                if (!session.ProcessingAttendanceRemovals && BotMembers(group).Length < 2)
                    FinishGroupTask(session, "A member left and fewer than two active bots remain");
                else if (!session.ProcessingAttendanceRemovals)
                {
                    GameBot[] remaining = BotMembers(group);
                    GameBot previousLeader = session.Leader;
                    GameBot replacement = ChooseLeader(session, remaining);
                    if (previousLeader == bot && replacement != null && IsAssemblyPhase(session.Phase))
                    {
                        // The old meetup was anchored to a leader who no longer
                        // belongs to the party. Elect an actual member and give
                        // that member a fresh, reachable staging contract.
                        if (!TryChooseRendezvous(replacement, session.ObjectiveKind, out Vector3 point,
                                out string name, out ushort region))
                        {
                            FinishGroupTask(session, "The group leader left and no replacement rendezvous is reachable");
                        }
                        else
                        {
                            session.Rendezvous = point;
                            session.RendezvousName = name;
                            session.RendezvousRegion = region;
                            session.Phase = "Leader staging";
                            session.LeaderReadyForAssembly = false;
                            session.LockedSize = remaining.Length;
                            session.Camp = null;
                            session.Attendance.Reset();
                            session.LeaderStagingDeadlineTick = GameLoop.GameLoopTime + LeaderStagingTimeoutMilliseconds;
                            session.LeaderStagingDeadlineUtc = DateTime.UtcNow.AddMilliseconds(LeaderStagingTimeoutMilliseconds);
                            if (TryBuildRendezvousSlots(session, remaining))
                                WriteSessionMetadata(session, remaining);
                            else
                                FinishGroupTask(session, "The replacement leader has no valid formation staging point");
                        }
                    }
                    else if (Sessions.ContainsKey(group))
                    {
                        // Publish replacement leader/puller membership now;
                        // do not leave the launcher stale until another member's
                        // low-frequency AI pulse happens to run.
                        session.LockedSize = remaining.Length;
                        WriteSessionMetadata(session, remaining);
                    }
                }
                AutonomousObjectiveAssignments.BeginSoloAfterGroupTask(bot,
                    "Left autonomous party; choosing independent work");
            }
        }
    }


    public static void OnDisbanding(Group group)
    {
        if (group == null)
            return;
        AutonomousDefensivePull.Cancel(group);
        lock (Sync)
        {
            AutonomousRealmRaid.RemoveParty(group);
            if (Sessions.TryGetValue(group, out Session ending) && ending.ObjectiveKind == eAutonomousObjectiveKind.RvR)
                AutonomousRvrEventLayer.RemoveForce(ending.Id);
            foreach (GameBot bot in AllBotMembers(group))
                ClearMetadata(bot, true);
            Sessions.Remove(group);
        }
    }

    private static void TryFormGroups()
    {
        TryFormGroups(eAutonomousObjectiveKind.GroupPve);
        TryFormGroups(eAutonomousObjectiveKind.RvR);
    }

    private static void TryFormGroups(eAutonomousObjectiveKind objectiveKind)
    {
        ref long nextFormationTick = ref objectiveKind == eAutonomousObjectiveKind.RvR
            ? ref _nextRvrFormationTick
            : ref _nextGroupPveFormationTick;
        if (GameLoop.GameLoopTime < nextFormationTick)
            return;
        nextFormationTick = GameLoop.GameLoopTime + 15_000;

        // One existing low-frequency pass expires whole groups, including a
        // warband whose own members are currently all on horses or in combat.
        foreach (Session expired in Sessions.Values.Where(session => session.ObjectiveKind == objectiveKind &&
                     session.TaskClock.HasExpired(GameLoop.GameLoopTime) &&
                     AutonomousRealmRaid.GetView(session.Group) == null &&
                     !(session.ObjectiveKind == eAutonomousObjectiveKind.RvR &&
                       AutonomousRvrEventLayer.IsForceCommitted(session.Id, GameLoop.GameLoopTime))).ToArray())
            FinishGroupTask(expired, AutonomousRealmRaid.TryConsumeRelease(expired.Group, out string releaseReason)
                ? releaseReason : "Shared group task expired");

        GameBot[] activeRoster = AutonomousBotRegistry.Snapshot()
            .Where(bot => !bot.IsTemporaryGroupHelper && AutonomousObjectiveAssignments.Is(bot, objectiveKind))
            .ToArray();
        int groupedCount = activeRoster.Count(bot => bot.Group != null);
        // Allocation has already capped the GroupPve cohort at its population
        // target. Applying the legacy 40% cap again here would reduce it to 16%
        // of the full roster.
        int maximumGrouped = MaximumGroupedForObjective(objectiveKind, activeRoster.Length);
        int availableGroupSlots = maximumGrouped - groupedCount;
        if (objectiveKind != eAutonomousObjectiveKind.RvR && availableGroupSlots < 2)
            return;
        Dictionary<eRealm, int> initiallyGroupedByRealm = activeRoster
            .Where(bot => bot.Group != null)
            .GroupBy(bot => bot.Realm)
            .ToDictionary(group => group.Key, group => group.Count());

        GameBot[] available = activeRoster
            .Where(bot => bot.IsAlive && !bot.IsTemporaryGroupHelper && !bot.IsPlayerLedGroup && bot.Group == null && bot.CurrentRegion != null &&
                          !AutonomousRealmRaid.IsReserved(bot) &&
                          // siege recruits are merged into full parties by FormSiegeParties, not random warbands
                          !(objectiveKind == eAutonomousObjectiveKind.RvR &&
                            AutonomousRvrEventLayer.IsForceCommitted($"rvr-{bot.DatabaseID}", GameLoop.GameLoopTime)) &&
                          (objectiveKind != eAutonomousObjectiveKind.GroupPve ||
                           CanRecruitPveMember(bot.IsOnStableMasterRoute, bot.IsReturningAfterRelease,
                               bot.InCombat, bot.IsAttacking, (bot.Brain as BotBrain)?.HasAggro == true, 0)) &&
                          AutonomousObjectiveAssignments.Is(bot, objectiveKind))
            .OrderBy(_ => Random.Shared.Next())
            .ToArray();
        var claimed = new HashSet<GameBot>();
        int rendezvousChecks = 0;
        // RvR stages at the realm's fixed border keep, so the leader is the
        // bot closest to it. PvE meets around its leader and keeps the
        // shuffled draw below.
        IEnumerable<GameBot> leaders = available;
        if (objectiveKind == eAutonomousObjectiveKind.RvR)
        {
            var crossings = new Dictionary<(eRealm Realm, ushort Region), DbZonePoint>();
            leaders = AutonomousRvrStaging.ClosestToStagingFirst(available, bot => bot.Realm, bot =>
                AutonomousRvrStaging.TryGetNearestBorderKeep(bot, out AutonomousRvrStaging.BorderKeep keep, crossings)
                    ? AutonomousWorldBotController.EstimateTravelMinutes(bot, keep.RegionId,
                        (int)keep.Position.X, (int)keep.Position.Y, crossings)
                    : double.MaxValue);
        }

        foreach (GameBot leader in leaders)
        {
            // Bad geometry must not turn one formation pass into thousands of
            // native queries. Continue with more candidates on the next pass.
            // (Raised from 4 checks / 35% leader draw: only 248 groups formed in
            // an eight-hour run while 3,640 bots timed out waiting.)
            if (rendezvousChecks >= 6) break;
            if (claimed.Contains(leader) ||
                objectiveKind != eAutonomousObjectiveKind.RvR && Random.Shared.NextDouble() >= 0.6)
                continue;
            // RvR reserves are realm-local: Albion grouping cannot consume the
            // independent Midgard/Hibernia roamer reserve, and vice versa.
            int leaderSlots = objectiveKind == eAutonomousObjectiveKind.RvR
                ? AvailableGroupSlotsForObjective(objectiveKind,
                    activeRoster.Count(candidate => candidate.Realm == leader.Realm),
                    initiallyGroupedByRealm.GetValueOrDefault(leader.Realm))
                : availableGroupSlots - claimed.Count;
            int largestAllowed = Math.Min(8, leaderSlots - (objectiveKind == eAutonomousObjectiveKind.RvR
                ? claimed.Count(candidate => candidate.Realm == leader.Realm)
                : 0));
            // PvE prefers a full eight, but a region short on healers or tanks
            // may start a smaller viable party (see PveRosterTemplates).
            int minimumRequired = objectiveKind == eAutonomousObjectiveKind.GroupPve ? MinimumPveFormationSize : 2;
            if (largestAllowed < minimumRequired)
            {
                // One realm's full reserve must not prevent a later realm from
                // creating its own compatible RvR roaming party.
                if (ShouldContinueFormationSearch(objectiveKind, largestAllowed))
                    continue;
                break;
            }
            // A one means this actor remains an independent roamer; two
            // through eight create an actual warband. The realm-local reserve
            // below still guarantees that grouping pressure cannot consume
            // every solo RvR character.
            int rolledSize = objectiveKind == eAutonomousObjectiveKind.RvR
                ? AutonomousRvrStaging.RollWarbandSize(largestAllowed, Random.Shared.NextDouble())
                : 8;
            if (rolledSize == 1)
                continue;
            GameBot[] compatiblePool = available
                .Where(candidate => candidate != leader && !claimed.Contains(candidate) && candidate.Group == null &&
                                     AutonomousObjectiveAssignments.Is(candidate, objectiveKind) &&
                                     candidate.Realm == leader.Realm && candidate.CurrentRegionID == leader.CurrentRegionID &&
                                     LevelsCompatible(leader.Level, candidate.Level) &&
                                     (objectiveKind != eAutonomousObjectiveKind.GroupPve ||
                                      CanRecruitPveMember(candidate.IsOnStableMasterRoute,
                                          candidate.IsReturningAfterRelease, candidate.InCombat,
                                          candidate.IsAttacking, (candidate.Brain as BotBrain)?.HasAggro == true,
                                          candidate.GetDistanceTo(leader))))
                .OrderBy(candidate => Math.Abs(candidate.Level - leader.Level))
                .ThenBy(candidate => candidate.GetDistanceTo(leader))
                .ToArray();
            Dictionary<long, BotPveGroupRole> pveRoles = null;
            GameBot[] compatible;
            if (objectiveKind == eAutonomousObjectiveKind.GroupPve)
            {
                if (!TryBuildPveRoster(leader, compatiblePool, out compatible, out pveRoles,
                        MinimumPveFormationSize, largestAllowed))
                    continue;
                rolledSize = compatible.Length + 1;
            }
            else
            {
                compatible = compatiblePool.Take(rolledSize - 1).ToArray();
                if (compatible.Length != rolledSize - 1)
                    continue;
            }

            var group = new Group(leader);
            GroupMgr.AddGroup(group);
            if (!group.AddMember(leader))
            {
                GroupMgr.RemoveGroup(group);
                continue;
            }
            claimed.Add(leader);
            foreach (GameBot candidate in compatible)
            {
                if (group.AddMember(candidate))
                    claimed.Add(candidate);
            }
            if (group.MemberCount < 2)
            {
                group.DisbandGroup();
                continue;
            }

            rendezvousChecks++;
            Session session = NewSession(group, leader, rolledSize, objectiveKind);
            if (session == null)
            {
                GameBot[] unmatched = BotMembers(group);
                group.DisbandGroup();
                foreach (GameBot member in unmatched)
                    AutonomousObjectiveAssignments.BeginSoloAfterGroupTask(member, "No connected rendezvous is available");
                continue;
            }
            if (pveRoles != null)
                foreach ((long key, BotPveGroupRole role) in pveRoles)
                    session.PveRoles[key] = role;
            if (objectiveKind == eAutonomousObjectiveKind.GroupPve)
                session.Puller = ChooseLockedPvePuller(session, BotMembers(group));
            Sessions[group] = session;
            GameBot[] formedMembers = BotMembers(group);
            WriteSessionMetadata(session, formedMembers);
            Log.Info($"AUTONOMOUS_GROUP_FORMED group={session.Id} realm={GlobalConstants.RealmToName(leader.Realm)} " +
                     $"size={formedMembers.Length} lockedSize={session.LockedSize} objective={objectiveKind} " +
                     $"members=\"{string.Join(",", formedMembers.Select(member => member.Name))}\" " +
                     $"rendezvous={session.RendezvousRegion}:{(int)session.Rendezvous.X},{(int)session.Rendezvous.Y},{(int)session.Rendezvous.Z}");
            if (claimed.Count >= 15)
                break;
        }
    }

    private static Session NewSession(Group group, GameBot leader, int lockedSize = 0, eAutonomousObjectiveKind objectiveKind = eAutonomousObjectiveKind.GroupPve, SharedCamp raidStaging = null)
    {
        Vector3 center;
        string rendezvousName;
        ushort rendezvousRegion;
        if (raidStaging != null)
        {
            center = new(raidStaging.X, raidStaging.Y, raidStaging.Z);
            rendezvousName = raidStaging.ZoneName + " raid staging";
            rendezvousRegion = raidStaging.RegionId;
        }
        else if (!TryChooseRendezvous(leader, objectiveKind, out center, out rendezvousName, out rendezvousRegion)) return null;
        var session = new Session
        {
            Group = group,
            Leader = leader,
            Id = SessionId(leader.Realm, ++_nextGroupNumber),
            Rendezvous = center,
            RendezvousName = rendezvousName,
            TaskClock = new AutonomousGroupTaskClock(objectiveKind),
            LockedSize = lockedSize > 0 ? lockedSize : BotMembers(group).Length,
            PreferredLevelBonus = RollPreferredLevelBonus(lockedSize > 0 ? lockedSize : BotMembers(group).Length),
            ObjectiveKind = objectiveKind,
            RendezvousRegion = rendezvousRegion,
            LeaderStagingDeadlineTick = GameLoop.GameLoopTime + LeaderStagingTimeoutMilliseconds,
            LeaderStagingDeadlineUtc = DateTime.UtcNow.AddMilliseconds(LeaderStagingTimeoutMilliseconds),
        };
        if (!TryBuildRendezvousSlots(session, BotMembers(group)))
            return null;
        if (objectiveKind == eAutonomousObjectiveKind.GroupPve && session.PveRoles.Count == 0)
        {
            GameBot[] exact = BotMembers(group);
            if (!(raidStaging != null ? TryAssignExpeditionRoles(exact, leader, out Dictionary<long, BotPveGroupRole> roles) :
                TryAssignExactPveRoles(exact, leader, out roles)))
                return null;
            foreach ((long key, BotPveGroupRole role) in roles)
                session.PveRoles[key] = role;
            session.Puller = ChooseLockedPvePuller(session, exact);
        }
        session.Recovery.Observe(RecoveryMembers(session, BotMembers(group), false), false);
        if (raidStaging != null)
        {
            session.Phase = "Meeting up";
            session.LeaderReadyForAssembly = true;
        }
        return session;
    }

    private static bool UpdateSession(Session session, GameBot[] members)
    {
        if (AutonomousRealmRaid.TryConsumeRelease(session.Group, out string raidReason))
        {
            FinishGroupTask(session, raidReason);
            return false;
        }
        var raidView = AutonomousRealmRaid.GetView(session.Group);
        if (raidView != null && !IsAssemblyPhase(session.Phase)) session.Camp = raidView.Camp;
        if (session.ObjectiveKind == eAutonomousObjectiveKind.RvR &&
            AutonomousRvrEventLayer.TryConsumeRelease(session.Id, GameLoop.GameLoopTime, out string eventReason))
        {
            FinishGroupTask(session, eventReason);
            return false;
        }
        if (session.TaskClock.HasExpired(GameLoop.GameLoopTime) &&
            raidView == null &&
            !(session.ObjectiveKind == eAutonomousObjectiveKind.RvR &&
              AutonomousRvrEventLayer.IsForceCommitted(session.Id, GameLoop.GameLoopTime)))
        {
            FinishGroupTask(session, "Shared group task expired");
            return false;
        }
        if (raidView == null && session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve &&
            !HasViablePveComposition(session, members))
        {
            FinishGroupTask(session, "The PvE party no longer has a viable tank, healer, and attacker roster");
            return false;
        }
        if (members.Length < 2)
        {
            FinishGroupTask(session, "Not enough active members remain for a group");
            return false;
        }

        GameBot leader = ChooseLeader(session, members);
        if (leader == null && raidView == null)
        {
            FinishGroupTask(session, "No living group leader remains");
            return false;
        }

        // Only enrolled dragon/epic parties use simultaneous hub assembly.
        // Automatic and forced enrollment share this path. Never wait for a
        // leader to finish a continent-wide solo journey before releasing others.
        if (raidView?.Muster == true)
        {
            if (session.RaidMusterEvent != raidView.EventId)
            {
                var previous = session.Rendezvous;
                ushort previousRegion = session.RendezvousRegion;
                session.Rendezvous = new(raidView.Camp.X, raidView.Camp.Y, raidView.Camp.Z);
                session.RendezvousRegion = raidView.Camp.RegionId;
                if (!TryBuildRendezvousSlots(session, members))
                {
                    session.Rendezvous = previous;
                    session.RendezvousRegion = previousRegion;
                    FinishGroupTask(session, "Realm expedition hub formation could not be validated");
                    return false;
                }
                session.RaidMusterEvent = raidView.EventId;
                session.RendezvousName = raidView.Camp.ZoneName;
                session.HeldUnreachableMembers.Clear();
                RebaseAttendance(session, members);
                Log.Info($"REALM_RAID_HUB_ASSEMBLY group={session.Id} event={raidView.EventId} hub={session.RendezvousName} simultaneous=true");
            }
            session.Phase = "Meeting up";
            session.LeaderReadyForAssembly = true;
            session.Camp = raidView.Camp;
            // Deaths on the initial hub journey must not become a delayed wipe
            // signal after the fully reassembled party departs.
            session.Recovery.Observe(RecoveryMembers(session, members, false), false);
            WriteSessionMetadata(session, members);
            return true;
        }
        if (session.RaidMusterEvent != null)
        {
            session.RaidMusterEvent = null;
            session.HeldUnreachableMembers.Clear();
            session.Phase = "Traveling";
            session.Camp = raidView?.Camp;
            StartTaskClock(session, members);
            Log.Info($"REALM_RAID_HUB_DEPARTURE group={session.Id} event={raidView?.EventId} size={members.Length}");
        }

        if (raidView != null)
        {
            // The expedition owns its lifetime. A death, release or distant
            // member must never invoke the ordinary eight-person wipe policy.
            session.Phase = "Traveling";
            session.Camp = raidView.Camp;
            session.RecoveringBetweenPulls = false;
            session.Recovery.Observe(RecoveryMembers(session, members, false), false);
            WriteSessionMetadata(session, members);
            return true;
        }

        // Accept a leader who reached the validated slot on the final pulse
        // before evaluating the deadline. The former ordering could log a
        // staging failure while the controller already displayed "Leader
        // ready", destroying an otherwise valid eight-person party.
        if (session.Phase == "Leader staging" && AtRendezvous(session, leader))
        {
            Vector3 previous = session.Rendezvous;
            session.Rendezvous = new(leader.X, leader.Y, leader.Z);
            if (!TryBuildRendezvousSlots(session, members))
            {
                session.Rendezvous = previous;
                // Recentering is cosmetic. The leader already reached the
                // originally validated slot, so a failed rebuild around its
                // exact feet must not destroy an otherwise valid party.
                TryBuildRendezvousSlots(session, members);
            }
            session.LeaderReadyForAssembly = true;
            session.Phase = "Meeting up";
            RebaseAttendance(session, members);
            Log.Info($"AUTONOMOUS_GROUP_LEADER_READY group={session.Id} leader=\"{leader.Name}\" " +
                     $"town=\"{session.RendezvousName}\" region={session.RendezvousRegion} " +
                     $"rendezvous={(int)session.Rendezvous.X},{(int)session.Rendezvous.Y},{(int)session.Rendezvous.Z}");
        }

        if (raidView == null && HasLeaderStagingTimedOut(session.Phase, session.LeaderStagingDeadlineTick, GameLoop.GameLoopTime))
        {
            OfflineWorldBotRecord record = leader.PersistentRecord;
            Vector3 leaderSlot = session.RendezvousSlots.TryGetValue(MemberKey(leader), out Vector3 resolvedSlot)
                ? resolvedSlot
                : session.Rendezvous;
            Log.Warn("AUTONOMOUS_GROUP_LEADER_STAGING_TIMEOUT " + JsonSerializer.Serialize(new
            {
                group = session.Id, leader = leader.Name, id = leader.DatabaseID,
                level = leader.Level, className = leader.ClassName, realm = GlobalConstants.RealmToName(leader.Realm),
                ordinaryWindowSeconds = LeaderStagingTimeoutMilliseconds / 1000,
                shortenedAfterRepeatedRouteFailure = session.UnreachableRetried.Contains(MemberKey(leader)),
                region = leader.CurrentRegionID, x = leader.X, y = leader.Y, z = leader.Z,
                rendezvousTown = session.RendezvousName, rendezvousRegion = session.RendezvousRegion,
                rendezvousX = session.Rendezvous.X, rendezvousY = session.Rendezvous.Y,
                rendezvousZ = session.Rendezvous.Z,
                rendezvousSlotX = leaderSlot.X, rendezvousSlotY = leaderSlot.Y, rendezvousSlotZ = leaderSlot.Z,
                distanceToCenter = (int)Math.Ceiling(Vector3.Distance(new(leader.X, leader.Y, leader.Z), session.Rendezvous)),
                distanceToSlot = (int)Math.Ceiling(Vector3.Distance(new(leader.X, leader.Y, leader.Z), leaderSlot)),
                moving = leader.IsMoving, riding = leader.IsOnStableMasterRoute,
                activity = record?.Activity, goal = record?.CurrentGoal,
                destination = record?.TravelDestination, routeStatus = record?.ObjectiveProgress,
                members = members.Select(member => member.Name).ToArray()
            }));
            FinishGroupTask(session,
                $"Leader {leader.Name} failed to reach {session.RendezvousName} within the 20-minute staging window");
            return false;
        }

        // Only fights near the leader hold the group (see AutonomousGroupCombat).
        GameBot combatLeader = session.Leader != null && members.Contains(session.Leader) ? session.Leader : null;
        bool groupCombatActive = AutonomousGroupCombat.AnyHoldsGroup(members, combatLeader, GameLoop.GameLoopTime);
        if (groupCombatActive)
        {
            session.CombatObserved = true;
            session.RecoveringBetweenPulls = false;
        }
        else if (session.CombatObserved && !session.Recovery.IsRegrouping)
        {
            // Near-full resources after a trivial defense do not require an
            // exact-100 rest that no controller branch would initiate. Once a
            // real low-resource recovery begins, keep the existing full-rest
            // contract before the next elective pull.
            session.RecoveringBetweenPulls = members.Any(member => ShouldRecoverAfterGroupCombat(
                session.RecoveringBetweenPulls, member.HealthPercent, member.ManaPercent,
                member.EndurancePercent, member.MaxMana > 0));
            if (!session.RecoveringBetweenPulls)
                session.CombatObserved = false;
        }

        if (session.Phase == "Meeting up" && !(session.ObjectiveKind == eAutonomousObjectiveKind.RvR &&
            AutonomousRvrEventLayer.IsBattleForce(session.Id, GameLoop.GameLoopTime)))
        {
            if (raidView == null) ExpelRendezvousNoShows(session, members);
            members = BotMembers(session.Group);
            if (!Sessions.ContainsKey(session.Group))
                return false;
        }

        if (members.Length != session.LockedSize)
        {
            if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve)
            {
                FinishGroupTask(session, "The locked PvE party roster changed");
                return false;
            }
            session.LockedSize = members.Length;
            session.Camp = null;
            session.PreferredLevelBonus = RollPreferredLevelBonus(members.Length) - session.WipePenalty;
            if (!IsAssemblyPhase(session.Phase) && !session.Recovery.IsRegrouping)
                session.Phase = "Choosing group target";
        }

        // An active siege replaces the normal wipe rendezvous: each revived
        // member returns directly to the event while its warband stays locked.
        if (session.ObjectiveKind == eAutonomousObjectiveKind.RvR &&
            AutonomousRvrEventLayer.IsBattleForce(session.Id, GameLoop.GameLoopTime))
        {
            session.RecoveringBetweenPulls = false;
            session.Phase = "Siege in progress";
            WriteSessionMetadata(session, members);
            return true;
        }
        if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve && session.TaskClock.HasStarted)
        {
            bool casualty = members.Any(member => !member.IsAlive);
            if (casualty)
            {
                if (session.Phase != "Waiting for resurrection")
                {
                    session.PhaseBeforeCasualty = session.Phase;
                    session.Phase = "Waiting for resurrection";
                    Log.Info($"AUTONOMOUS_GROUP_RESURRECTION_WAIT group={session.Id} " +
                             $"dead=\"{string.Join(",", members.Where(member => !member.IsAlive).Select(member => member.Name))}\"");
                }
                session.RecoveringBetweenPulls = true;
                foreach (GameBot member in members.Where(member => member.IsAlive))
                    session.CorpseCombatClearSince.Remove(MemberKey(member));
                WriteSessionMetadata(session, members);
                return true;
            }
            if (session.Phase == "Waiting for resurrection")
            {
                // A nearby resurrection already put the whole party back in
                // formation. Record the death once, then recover resources at
                // the current camp instead of sending everybody back to town.
                bool together = RevivedMembersCanReformLocally(members, leader);
                bool localRecovery = session.Recovery.TryResumeLocally(
                    RecoveryMembers(session, members, false), together);
                session.Phase = PhaseAfterCasualty(session.PhaseBeforeCasualty,
                    session.Recovery.IsRegrouping);
                session.PhaseBeforeCasualty = string.Empty;
                session.CorpseCombatClearSince.Clear();
                session.RecoveringBetweenPulls = !MembersReadyForPull(members);
                Log.Info($"AUTONOMOUS_GROUP_CASUALTY_CLEARED group={session.Id} localRecovery={localRecovery} " +
                    $"returning={members.Count(member => member.IsReturningAfterRelease)}");
            }
        }
        if (session.Recovery.Observe(RecoveryMembers(session, members, false), session.TaskClock.HasStarted))
        {
            session.WipePenalty = session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve
                ? AutonomousGroupTargetPolicy.PenaltyAfterWipe((int)Math.Round(members.Average(member => member.Level)),
                    members.Length, session.WipePenalty, session.Camp?.TargetLevel ?? 0)
                : Math.Min(2, session.WipePenalty + 1);
            session.PreferredLevelBonus = RollPreferredLevelBonus(members.Length) - session.WipePenalty;
            session.Camp = null;
            session.DungeonArrivalRegion = 0;
            session.DungeonInteriorStagingPoint = default;
            session.DungeonArrivalHoldUntilTick = 0;
            session.Phase = "Regrouping";
            session.LeaderReadyForAssembly = false;
            session.HeldUnreachableMembers.Clear();
            session.UnreachableSince.Clear();
            session.UnreachableRetried.Clear();
            session.RendezvousReselectionAttempted = false;
            if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve)
                session.RecoveryRendezvousChosen = false;
            ChooseRecoveryRendezvous(session, members[0].Realm);
            Log.Info($"AUTONOMOUS_GROUP_REGROUP_STARTED group={session.Id} objective={session.ObjectiveKind} " +
                $"members=\"{string.Join(",", members.Select(member => $"{member.Name}:L{member.Level}:deaths={member.PersistentRecord?.DeathCount}:alive={member.IsAlive}"))}\" " +
                $"rendezvous={session.RendezvousRegion}:{session.Rendezvous.X},{session.Rendezvous.Y},{session.Rendezvous.Z} " +
                $"remainingSeconds={session.TaskClock.RemainingMilliseconds(GameLoop.GameLoopTime) / 1000}");
        }
        // Readiness/mesh checks happen at most once per second per PARTY, not
        // once for every member's AI turn. Casualty detection above stays immediate.
        if (session.Recovery.IsRegrouping && session.RaidMusterEvent == null)
        {
            GameBot[] unreachable = members.Where(member =>
                session.UnreachableSince.TryGetValue(MemberKey(member), out long failedAt) &&
                session.HeldUnreachableMembers.Contains(MemberKey(member)) &&
                GameLoop.GameLoopTime - failedAt >=
                    UnreachableRouteRetryMilliseconds + UnreachableRouteGraceMilliseconds &&
                !AtRendezvous(session, member)).ToArray();
            if (unreachable.Length > 0)
            {
                if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve)
                {
                    // A single disconnected member should not disband a
                    // recoverable party. The existing roster-reduction path
                    // still requires a tank, healer, and attacker and ends the
                    // task if that viable core has been lost.
                    ReducePvePartyAfterNoShows(session, members, unreachable, GameLoop.GameLoopTime);
                    if (session.Ending) return false;
                    members = BotMembers(session.Group);
                }
                else
                {
                    FinishGroupTask(session,
                        "A regroup member has no connected route to the safe rendezvous after a bounded retry");
                    return false;
                }
            }
        }
        if (session.Recovery.IsRegrouping && GameLoop.GameLoopTime >= session.NextRecoveryReadinessTick)
        {
            session.NextRecoveryReadinessTick = GameLoop.GameLoopTime + 1_000;
            leader = ChooseLeader(session, members);
            if (!session.LeaderReadyForAssembly && leader != null && AtRendezvous(session, leader))
            {
                Vector3 previous = session.Rendezvous;
                session.Rendezvous = new(leader.X, leader.Y, leader.Z);
                if (TryBuildRendezvousSlots(session, members))
                {
                    session.LeaderReadyForAssembly = true;
                    Log.Info($"AUTONOMOUS_GROUP_REGROUP_LEADER_READY group={session.Id} leader=\"{leader.Name}\" " +
                             $"region={session.RendezvousRegion} rendezvous={(int)session.Rendezvous.X},{(int)session.Rendezvous.Y},{(int)session.Rendezvous.Z}");
                }
                else
                {
                    session.Rendezvous = previous;
                    TryBuildRendezvousSlots(session, members);
                    session.LeaderReadyForAssembly = true;
                    Log.Info($"AUTONOMOUS_GROUP_REGROUP_LEADER_READY group={session.Id} leader=\"{leader.Name}\" " +
                             $"region={session.RendezvousRegion} rendezvous={(int)session.Rendezvous.X},{(int)session.Rendezvous.Y},{(int)session.Rendezvous.Z}");
                }
            }
            if (session.LeaderReadyForAssembly &&
                session.Recovery.TryComplete(RecoveryMembers(session, members, true), GameLoop.GameLoopTime))
            {
                session.Phase = "Choosing group target";
                Log.Info($"AUTONOMOUS_GROUP_REGROUP_READY group={session.Id} size={members.Length} " +
                    $"remainingSeconds={session.TaskClock.RemainingMilliseconds(GameLoop.GameLoopTime) / 1000}");
            }
        }

        if (session.Phase == "Meeting up" && members.All(member => AtRendezvous(session, member)))
        {
            session.Phase = "Choosing group target";
            // The camp planned at formation: the party met at the town nearest it and goes straight there.
            if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve && session.PlannedCamp is { } planned)
            {
                session.PlannedCamp = null;
                AssignCamp(session, planned, members);
            }
            // PvE starts when the leader publishes the actual outbound camp.
            // RvR heads out through its director as soon as assembly completes.
            if (session.ObjectiveKind == eAutonomousObjectiveKind.RvR)
                StartTaskClock(session, members);
            Log.Info($"AUTONOMOUS_GROUP_ASSEMBLED group={session.Id} realm={members[0].Realm} size={members.Length} " +
                     $"members=\"{string.Join(",", members.Select(member => member.Name))}\" " +
                     $"region={members[0].CurrentRegionID} rendezvous={(int)session.Rendezvous.X},{(int)session.Rendezvous.Y},{(int)session.Rendezvous.Z}");
        }

        WriteSessionMetadata(session, members);
        return true;
    }

    private static bool RevivedMembersCanReformLocally(GameBot[] members, GameBot leader)
    {
        if (leader?.CurrentRegion == null || members == null || members.Length < 2)
            return false;
        IPathfindingMgr nav = PathfindingProvider.Instance;
        Zone destinationZone = leader.CurrentZone;
        if (destinationZone == null || !nav.IsAvailable || !nav.HasNavmesh(destinationZone) ||
            !AutonomousNavigationSurface.TryFloor(nav, destinationZone,
                new(leader.X, leader.Y, leader.Z), out Vector3 destination))
            return false;
        foreach (GameBot member in members)
        {
            if (member == leader)
                continue;
            bool sameRegion = member.CurrentRegionID == leader.CurrentRegionID;
            bool sameZone = member.CurrentZone == destinationZone;
            int distance = member.GetDistanceTo(leader);
            if (!sameRegion || !sameZone || distance > 1_800 ||
                !AutonomousNavigationSurface.TryFloor(nav, destinationZone,
                    new(member.X, member.Y, member.Z), out Vector3 start))
                return false;
            // Proximity is not connectivity: two Koalinth rooms may be only a
            // few units apart across a wall. Both complete corridors must be
            // proved on the same floor so one-way drops also cannot masquerade
            // as a locally reunited party. Missing mesh proof means regroup.
            bool connectedBothWays = HasExactLocalCorridor(nav, destinationZone, start, destination) &&
                HasExactLocalCorridor(nav, destinationZone, destination, start);
            if (!CanCountLocalRevival(distance, sameRegion, sameZone, true, connectedBothWays))
                return false;
        }
        return true;
    }

    public static bool CanCountLocalRevival(int distance, bool sameRegion, bool sameZone,
        bool hasNavmeshProof, bool connectedBothWays) =>
        distance >= 0 && distance <= 1_800 && sameRegion && sameZone &&
        hasNavmeshProof && connectedBothWays;

    private static bool HasExactLocalCorridor(IPathfindingMgr nav, Zone zone, Vector3 start, Vector3 end)
    {
        // The ordinary route helper deliberately accepts a partial path that
        // ends within 48 units. That is useful for movement but could mistake
        // a thin wall between neighboring dungeon rooms for a local reunion.
        // Require Detour's actual complete path in both directions here.
        Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[256];
        try
        {
            PathfindingResult result = nav.GetPathStraight(zone, start, end, nav.DefaultFilters, nodes);
            return IsCompleteLocalReunionSegment(result, nodes, start, end);
        }
        catch
        {
            // Mesh failure cannot be treated as evidence of local reunion.
            return false;
        }
    }

    public static bool IsCompleteLocalReunionSegment(PathfindingResult result,
        ReadOnlySpan<WrappedPathfindingNode> nodes, Vector3 start, Vector3 end) =>
        result.Status == PathfindingStatus.PathFound && result.NodeCount > 0 &&
        result.NodeCount <= nodes.Length &&
        Vector3.DistanceSquared(nodes[0].Position, start) <= 48 * 48 &&
        Vector3.DistanceSquared(nodes[result.NodeCount - 1].Position, end) <= 48 * 48;

    private static bool AtRendezvous(Session session, GameBot member) =>
        // Physical attendance is authoritative. IsReturningAfterRelease can
        // remain set for a pulse after a living bot has already reached its
        // assigned slot; rejecting that real arrival produced leader timeouts
        // with distanceToSlot=1 and activity="Leader ready".
        member.IsAlive && !member.IsOnStableMasterRoute &&
        member.CurrentRegionID == session.RendezvousRegion &&
        AutonomousRendezvousAttendance.IsAtSlot(new(member.X, member.Y, member.Z),
            session.RendezvousSlots.TryGetValue(MemberKey(member), out Vector3 slot)
                ? slot
                : FormationPoint(member, session.Rendezvous,
                    RendezvousIsTight(session)), RendezvousIsTight(session));

    private static bool RendezvousIsTight(Session session)
    {
        Region region = WorldMgr.GetRegion(session.RendezvousRegion);
        Zone zone = region?.GetZone((int)session.Rendezvous.X, (int)session.Rendezvous.Y);
        return region?.IsDungeon == true || zone?.IsDungeon == true;
    }

    private static void StartTaskClock(Session session, GameBot[] members)
    {
        if (!session.TaskClock.Start(GameLoop.GameLoopTime, DateTime.UtcNow))
            return;
        Log.Info($"AUTONOMOUS_GROUP_TASK_STARTED group={session.Id} objective={session.ObjectiveKind} " +
                 $"size={members.Length} remainingSeconds={session.TaskClock.RemainingMilliseconds(GameLoop.GameLoopTime) / 1000} " +
                 $"expiresUtc={session.TaskClock.ExpiresUtc:O}");
    }

    private static void ExpelRendezvousNoShows(Session session, GameBot[] members)
    {
        long now = GameLoop.GameLoopTime;
        if (now < session.NextAttendanceTick)
            return;
        session.NextAttendanceTick = now + 5_000;
        // Capture attendance before removals shift group indexes/formation slots.
        GameBot[] missing = members.Where(member =>
            session.Attendance.Observe(member.DatabaseID, now, AtRendezvous(session, member))).ToArray();
        if (missing.Length == 0)
            return;
        if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve)
        {
            foreach (GameBot member in missing)
                LogNoShow(session, member, now);
            ReducePvePartyAfterNoShows(session, members, missing, now);
            return;
        }
        GameBot leader = ChooseLeader(session, members);
        bool everyInvitedMemberMissed = leader != null &&
            members.Any(member => member != leader) &&
            members.Where(member => member != leader).All(member => missing.Contains(member));
        session.ProcessingAttendanceRemovals = true;
        try
        {
            foreach (GameBot member in missing)
            {
                LogNoShow(session, member, now);
                session.Group.RemoveMember(member, retainSingleRemainingMember: true);
                AutonomousObjectiveAssignments.BeginSoloAfterGroupTask(member, "Choosing independent work after rendezvous timeout");
            }
        }
        finally
        {
            session.ProcessingAttendanceRemovals = false;
        }
        GameBot[] remaining = BotMembers(session.Group);
        session.LockedSize = remaining.Length; // Do not backfill and restart the same wait.
        session.Camp = null;
        session.PreferredLevelBonus = RollPreferredLevelBonus(remaining.Length) - session.WipePenalty;
        foreach (GameBot member in missing)
        {
            session.HeldUnreachableMembers.Remove(MemberKey(member));
            session.UnreachableSince.Remove(MemberKey(member));
            session.UnreachableRetried.Remove(MemberKey(member));
        }
        if (leader != null && missing.Contains(leader) && remaining.Length >= 2)
        {
            GameBot replacement = ChooseLeader(session, remaining);
            if (replacement == null || !TryChooseRendezvous(replacement, session.ObjectiveKind, out Vector3 point,
                    out string name, out ushort region))
            {
                FinishGroupTask(session, "The meetup leader left and no replacement rendezvous is reachable");
                return;
            }

            session.Rendezvous = point;
            session.RendezvousName = name;
            session.RendezvousRegion = region;
            session.Phase = "Leader staging";
            session.LeaderReadyForAssembly = false;
            session.Attendance.Reset();
            session.LeaderStagingDeadlineTick = now + LeaderStagingTimeoutMilliseconds;
            session.LeaderStagingDeadlineUtc = DateTime.UtcNow.AddMilliseconds(LeaderStagingTimeoutMilliseconds);
            if (!TryBuildRendezvousSlots(session, remaining))
            {
                FinishGroupTask(session, "The replacement meetup leader has no valid formation staging point");
                return;
            }
            Log.Info($"AUTONOMOUS_GROUP_LEADER_REPLACED group={session.Id} old=\"{leader.Name}\" " +
                     $"leader=\"{replacement.Name}\" town=\"{session.RendezvousName}\" members={remaining.Length}");
            WriteSessionMetadata(session, remaining);
            return;
        }
        if (everyInvitedMemberMissed)
        {
            Log.Warn($"AUTONOMOUS_GROUP_MEETUP_FAILED group={session.Id} leader=\"{leader.Name}\" " +
                     $"town=\"{session.RendezvousName}\" invited={members.Length - 1} " +
                     $"reason=\"Every invited member missed the meetup deadline\"");
            FinishGroupTask(session,
                $"Every invited member missed the meetup deadline with leader {leader.Name} in {session.RendezvousName}");
            return;
        }
        if (remaining.Length < 2)
        {
            session.Group.DisbandGroup();
            foreach (GameBot member in remaining)
                AutonomousObjectiveAssignments.BeginSoloAfterGroupTask(member, "Choosing independent work after rendezvous timeout");
            return;
        }
        // Remaining members finish settling into their new slots, then the
        // existing leader selector chooses a fresh camp for this actual size.
        if (TryBuildRendezvousSlots(session, remaining))
            RebaseAttendance(session, remaining, preserveDeadlines: true);
        WriteSessionMetadata(session, remaining);
    }

    private static void RebaseAttendance(Session session, GameBot[] members, bool preserveDeadlines = false)
    {
        long now = GameLoop.GameLoopTime;
        if (preserveDeadlines)
            session.Attendance.RebaseAfterRosterReduction(members.Select(MemberKey), now);
        else
            session.Attendance.Rebase(members.Select(MemberKey), now);
        foreach (GameBot member in members)
            session.Attendance.Observe(MemberKey(member), now, AtRendezvous(session, member));
    }

    /// <summary>
    /// PvE parties may continue after a route failure removes one or more
    /// meetup no-shows.  The initial roster is still formed with eight bots;
    /// this predicate only governs safe continuation after that point.
    /// Realm-event and RvR parties do not use this rule.
    /// </summary>
    private static bool HasViablePveComposition(Session session, GameBot[] members)
    {
        if (session == null || members == null || members.Length < 3 ||
            members.Any(member => !session.PveRoles.ContainsKey(MemberKey(member))))
            return false;

        int tanks = members.Count(member => session.PveRoles[MemberKey(member)] == BotPveGroupRole.Tank);
        int healers = members.Count(member => session.PveRoles[MemberKey(member)] == BotPveGroupRole.Healer);
        int attackers = members.Count(member => session.PveRoles[MemberKey(member)] == BotPveGroupRole.Attacker);
        return tanks > 0 && healers > 0 && attackers > 0;
    }

    private static void ReducePvePartyAfterNoShows(Session session, GameBot[] members,
        GameBot[] missing, long now)
    {
        if (session == null || members == null || missing == null || missing.Length == 0)
            return;

        session.ProcessingAttendanceRemovals = true;
        try
        {
            foreach (GameBot member in missing)
            {
                long key = MemberKey(member);
                session.PveRoles.Remove(key);
                session.HeldUnreachableMembers.Remove(key);
                session.UnreachableSince.Remove(key);
                session.UnreachableRetried.Remove(key);
                session.Group.RemoveMember(member, retainSingleRemainingMember: true);
                AutonomousObjectiveAssignments.BeginSoloAfterGroupTask(member,
                    "Could not reach the PvE rendezvous; choosing independent work");
            }
        }
        finally
        {
            session.ProcessingAttendanceRemovals = false;
        }

        GameBot[] remaining = BotMembers(session.Group);
        session.LockedSize = remaining.Length;
        session.Camp = null;
        session.PreferredLevelBonus = RollPreferredLevelBonus(remaining.Length) - session.WipePenalty;
        if (!HasViablePveComposition(session, remaining))
        {
            FinishGroupTask(session,
                $"PvE meetup lost {string.Join(", ", missing.Select(member => member.Name))} and no viable core roster remained");
            return;
        }

        GameBot leader = ChooseLeader(session, remaining);
        if (leader == null)
        {
            FinishGroupTask(session, "No living PvE leader remained after unreachable members were released");
            return;
        }

        if (missing.Contains(session.Leader))
        {
            session.Leader = leader;
            if (!TryChooseRendezvous(leader, session.ObjectiveKind, out Vector3 point,
                    out string name, out ushort region))
            {
                FinishGroupTask(session, "No validated replacement PvE rendezvous was reachable");
                return;
            }
            session.Rendezvous = point;
            session.RendezvousName = name;
            session.RendezvousRegion = region;
            session.Phase = "Leader staging";
            session.LeaderReadyForAssembly = false;
            session.RendezvousReselectionAttempted = false;
            session.Attendance.Reset();
            session.LeaderStagingDeadlineTick = now + LeaderStagingTimeoutMilliseconds;
            session.LeaderStagingDeadlineUtc = DateTime.UtcNow.AddMilliseconds(LeaderStagingTimeoutMilliseconds);
        }

        if (!TryBuildRendezvousSlots(session, remaining))
        {
            FinishGroupTask(session, "The reduced PvE party had no valid formation slots");
            return;
        }

        RebaseAttendance(session, remaining, preserveDeadlines: true);
        Log.Warn($"AUTONOMOUS_GROUP_PARTIAL_ROSTER group={session.Id} " +
                 $"removed=\"{string.Join(",", missing.Select(member => member.Name))}\" " +
                 $"remaining={remaining.Length} reason=unreachable-rendezvous action=continue");
        WriteSessionMetadata(session, remaining);
    }

    private static void FinishGroupTask(Session session, string reason)
    {
        if (session == null || session.Ending)
            return;
        session.Ending = true;
        GameBot[] members = AllBotMembers(session.Group);
        if (session.ObjectiveKind == eAutonomousObjectiveKind.RvR)
            AutonomousRvrEventLayer.RemoveForce(session.Id);
        Log.Info($"AUTONOMOUS_GROUP_TASK_ENDED group={session.Id} objective={session.ObjectiveKind} " +
                 $"size={members.Length} members=\"{string.Join(",", members.Select(member => member.Name))}\" reason=\"{reason}\"");
        Log.Info("AUTONOMOUS_GROUP_OUTCOME " + JsonSerializer.Serialize(new
        {
            group = session.Id,
            objective = session.ObjectiveKind.ToString(),
            phase = session.Phase,
            reason,
            taskStarted = session.TaskClock.HasStarted,
            taskRemainingSeconds = session.TaskClock.RemainingMilliseconds(GameLoop.GameLoopTime) / 1000,
            camp = session.Camp?.Id ?? string.Empty,
            target = session.Camp?.MonsterName ?? string.Empty,
            zone = session.Camp?.ZoneName ?? string.Empty,
            members = members.Select(member => new
            {
                name = member.Name,
                id = member.DatabaseID,
                role = session.PveRoles.TryGetValue(MemberKey(member), out BotPveGroupRole role)
                    ? BotPartyRoles.GroupRoleLabel(role) : string.Empty,
                alive = member.IsAlive,
                region = member.CurrentRegionID,
                x = member.X,
                y = member.Y,
                z = member.Z,
                activity = member.PersistentRecord?.Activity ?? string.Empty,
                progress = member.PersistentRecord?.ObjectiveProgress ?? string.Empty,
                deaths = member.PersistentRecord?.DeathCount ?? 0,
                recoveryCount = member.PersistentRecord?.RecoveryCount ?? 0
            }).ToArray()
        }));
        session.Group.DisbandGroup();
        foreach (GameBot member in members)
        {
            ClearMetadata(member, true);
            if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve && session.TaskClock.HasExpired(GameLoop.GameLoopTime))
                AutonomousObjectiveAssignments.MarkPveTaskCompleted(member);
            AutonomousObjectiveAssignments.BeginSoloAfterGroupTask(member, reason + "; choosing independent work");
        }
    }

    private static bool TryChooseRendezvous(GameBot leader, eAutonomousObjectiveKind objectiveKind, out Vector3 point,
        out string rendezvousName, out ushort rendezvousRegion)
    {
        point = default;
        rendezvousName = string.Empty;
        rendezvousRegion = leader?.CurrentRegionID ?? 0;
        if (leader?.CurrentRegion == null)
            return false;
        Vector3 current = new(leader.X, leader.Y, leader.Z);
        GameBot[] members = BotMembers(leader.Group);
        bool TryPoint(Region targetRegion, Vector3 anchor, out Vector3 chosen, bool fixedCenter = false)
        {
            var nav = PathfindingProvider.Instance;
            Zone zone = targetRegion?.GetZone((int)anchor.X, (int)anchor.Y);
            bool valid = fixedCenter
                ? AutonomousRendezvousNavigation.TryChooseFixedPoint(nav, zone, anchor, out chosen)
                : AutonomousRendezvousNavigation.TryChoosePoint(nav, zone, anchor, out chosen);
            if (!valid) return false;
            // Local exits on two polygons do not imply a path between them.
            // Test once while forming the party, not every AI turn. A roof or
            // disconnected platform must not trap eight bots in an impossible meetup.
            bool tight = zone?.IsDungeon == true;
            foreach (GameBot member in members)
            {
                Vector3 desired = DesiredFormationPoint(member, chosen, tight);
                if (!AutonomousRendezvousNavigation.TryResolveFormationSlot(nav, zone, chosen, desired, out Vector3 slot))
                    return false;
                // Cross-region members are validated by the authoritative
                // zone-point graph while travelling. Members already in this
                // region must prove a connected zone itinerary to their slot.
                if (member.CurrentRegion == targetRegion &&
                    !AutonomousRendezvousNavigation.CanReachFormationSlot(nav, targetRegion,
                        member.CurrentZone, zone, new(member.X, member.Y, member.Z), slot))
                    return false;
            }
            return true;
        }

        if (objectiveKind == eAutonomousObjectiveKind.RvR &&
            AutonomousRvrStaging.TryGetNearestBorderKeep(leader, out AutonomousRvrStaging.BorderKeep borderKeep))
        {
            Region borderRegion = WorldMgr.GetRegion(borderKeep.RegionId);
            foreach (Vector3 anchor in AutonomousRvrStaging.CandidateAnchors(borderKeep, leader.DatabaseID))
            {
                if (!TryPoint(borderRegion, anchor, out point))
                    continue;
                rendezvousName = borderKeep.Name;
                rendezvousRegion = borderKeep.RegionId;
                return true;
            }
            // Never silently turn a failed RvR border-keep projection into a
            // random PvE-town meetup. Formation will retry on a later pass.
            return false;
        }

        // Never anchor a new Jordheim party to a leader waiting on the bank's
        // shelf. Keep the same floor/local-exit/member-corridor validation.
        if (leader.Realm == eRealm.Midgard && leader.CurrentRegionID == 101 &&
            TryPoint(leader.CurrentRegion, AutonomousRendezvousNavigation.JordheimMeetingPoint, out point, fixedCenter: true))
        {
            rendezvousName = "Jordheim";
            return true;
        }

        if (IsTownArea(leader) && TryPoint(leader.CurrentRegion, current, out point))
        {
            rendezvousName = TownName(leader);
            return true;
        }

        GameNPC[] safeTownNpcs = leader.CurrentRegion?.Objects.OfType<GameNPC>()
            .Where(npc => npc.ObjectState == GameObject.eObjectState.Active &&
                          npc is GameMerchant or GameTrainer or GameStableMaster &&
                          (npc.Realm == leader.Realm || npc.Realm == eRealm.None) && IsTownArea(npc))
            .Where(npc => Vector3.DistanceSquared(current, new(npc.X, npc.Y, npc.Z)) <= 30_000L * 30_000L)
            .OrderBy(npc => Vector3.DistanceSquared(current, new(npc.X, npc.Y, npc.Z)))
            .Take(4).ToArray() ?? [];
        foreach (GameNPC npc in safeTownNpcs)
        {
            if (!TryPoint(leader.CurrentRegion, new(npc.X, npc.Y, npc.Z), out point)) continue;
            rendezvousName = TownName(npc);
            return true;
        }

        // A capital is preferred when the group is already in its capital
        // region; otherwise avoid turning every local party into a cross-world
        // capital commute just to assemble.
        AutonomousStuckWatchdog.CapitalLocation capital = AutonomousStuckWatchdog.SafeCapitalFor(leader.Realm);
        if (capital.RegionId == leader.CurrentRegionID && TryPoint(leader.CurrentRegion,
                new(capital.X, capital.Y, capital.Z), out point))
        {
            rendezvousName = leader.CurrentRegion.Description;
            return true;
        }

        // Never form a party around an arbitrary wilderness point. A future
        // matchmaking pass can elect a leader near a real, level-local town.
        return false;
    }

    public enum PveCorpseDisposition { NotManaged, HoldForResurrection, ReleaseAndDisband, ReleaseAndRejoin }

    public static PveCorpseDisposition PveCorpseRecovery(GameBot deadBot)
    {
        Group corpseGroup = deadBot?.Group;
        if (corpseGroup == null || deadBot.IsAlive)
            return PveCorpseDisposition.NotManaged;
        if (AutonomousRealmRaid.GetView(corpseGroup) != null)
            return AutonomousRealmRaid.CorpseRecovery(deadBot);
        lock (Sync)
        {
            if (deadBot.Group != corpseGroup || !Sessions.TryGetValue(corpseGroup, out Session session) ||
                session.ObjectiveKind != eAutonomousObjectiveKind.GroupPve ||
                !session.TaskClock.HasStarted || IsAssemblyPhase(session.Phase))
                return PveCorpseDisposition.NotManaged;
            GameBot[] members = BotMembers(session.Group);
            // A nearby sister party in a realm raid can also defend or
            // resurrect this corpse. Combat elsewhere in the region must not
            // extend the corpse wait.
            GameBot[] support = AutonomousRealmRaid.GetView(corpseGroup) == null ? members :
                AutonomousRealmRaid.SupportMembers(deadBot).OfType<GameBot>().ToArray();
            GameBot[] rescuers = support.Where(member => member != deadBot &&
                member.ObjectState == GameObject.eObjectState.Active &&
                member.CurrentRegionID == deadBot.CurrentRegionID &&
                member.IsWithinRadius(deadBot, WorldMgr.VISIBILITY_DISTANCE)).ToArray();
            bool combat = rescuers.Any(member => member.IsAlive && (member.InCombat || member.IsAttacking ||
                (member.Brain as BotBrain)?.HasAggro == true));
            long corpseKey = MemberKey(deadBot);
            if (combat)
            {
                session.CorpseCombatClearSince.Remove(corpseKey);
                return PveCorpseDisposition.HoldForResurrection;
            }
            int deaths = deadBot.PersistentRecord?.DeathCount ?? 0;
            if (!session.CorpseCombatClearSince.TryGetValue(corpseKey, out var wait) || wait.Deaths != deaths)
            {
                wait = (deaths, GameLoop.GameLoopTime);
                session.CorpseCombatClearSince[corpseKey] = wait;
            }
            bool potentialResurrection = rescuers.Any(member =>
                member.IsAlive && member.ResurrectionSpell != null);
            long waitMilliseconds = potentialResurrection ? 60_000 : 20_000;
            if (GameLoop.GameLoopTime - wait.SinceTick < waitMilliseconds)
                return PveCorpseDisposition.HoldForResurrection;
            long deadline = wait.SinceTick + waitMilliseconds;
            if (rescuers.Any(member => member.IsAlive && member.IsCasting &&
                    member.castingComponent.SpellHandler is { } cast && cast.Target == deadBot &&
                    cast.Spell.SpellType == eSpellType.Resurrect &&
                    BotGroupSupport.CanFinishResurrectionBeforeRelease(cast.CastStartTick, deadline,
                        GameLoop.GameLoopTime, cast.Spell.CastTime)))
                return PveCorpseDisposition.HoldForResurrection;
            string roleName = session.PveRoles.TryGetValue(MemberKey(deadBot), out BotPveGroupRole role)
                ? BotPartyRoles.GroupRoleLabel(role) : "unknown";
            Log.Warn($"AUTONOMOUS_GROUP_RESURRECTION_TIMEOUT group={session.Id} bot=\"{deadBot.Name}\" " +
                     $"role=\"{roleName}\" combatClearSeconds={waitMilliseconds / 1000} action=release-and-rejoin " +
                     $"corpse={deadBot.CurrentRegionID}:{deadBot.X},{deadBot.Y},{deadBot.Z} " +
                     $"resurrectors=\"{string.Join(";", members.Where(m => m.ResurrectionSpell != null).Select(m =>
                         $"{m.Name}:alive={m.IsAlive}:region={m.CurrentRegionID}:distance={m.GetDistanceTo(deadBot)}:mana={m.ManaPercent}:casting={m.IsCasting}:interrupted={m.IsBeingInterruptedByOther}"))}\"");
            // A missed resurrection is a member-recovery problem, not a party
            // wipe.  The dead bot releases to bind and uses its normal
            // return-to-party route while the surviving PvE roster continues.
            session.CorpseCombatClearSince.Remove(corpseKey);
            return PveCorpseDisposition.ReleaseAndRejoin;
        }
    }

    public static bool IsNamedRendezvousArea(AbstractArea area) =>
        area != null && area is not Area.BindArea &&
        !string.Equals(area.Description?.Trim(), "bind point", StringComparison.OrdinalIgnoreCase) &&
        AutonomousWorldBotController.IsIdleTownArea(area);

    private static bool IsTownArea(GameObject obj) => obj?.CurrentRegion?.IsCapitalCity == true ||
        NamedTownArea(obj) != null;

    private static string TownName(GameObject obj) => obj?.CurrentRegion?.IsCapitalCity == true
        ? obj.CurrentRegion.Description
        : NamedTownArea(obj)?.Description ?? obj?.CurrentZone?.Description ?? "reachable town";

    // CurrentAreas dereferences CurrentZone without a guard. A bot or town NPC
    // can lose its zone during a cross-region handoff on a different AI turn;
    // checking the zone snapshot avoids taking down group coordination.
    private static AbstractArea NamedTownArea(GameObject obj)
    {
        Zone zone = obj?.CurrentZone;
        return zone?.GetAreasOfSpot(obj.X, obj.Y, obj.Z)?
            .OfType<AbstractArea>().FirstOrDefault(IsNamedRendezvousArea);
    }

    private static void ChooseRecoveryRendezvous(Session session, eRealm realm)
    {
        if (session.RecoveryRendezvousChosen) return;
        session.RecoveryRendezvousChosen = true;
        // RvR regrouping returns to the same validated safe border keep used
        // for initial assembly. It must never drift to a random town or bind.
        if (session.ObjectiveKind == eAutonomousObjectiveKind.RvR)
            return;
        // A casualty after a long camp trip should not send the whole party
        // back across the world to its first meetup. Prefer a validated safe
        // town near the largest living cluster; keep the original town as a
        // fallback when the present region has no reachable safe rendezvous.
        GameBot[] members = BotMembers(session.Group);
        GameBot anchor = members.Where(member => member.IsAlive)
            .OrderByDescending(member => members.Count(other => other.IsAlive &&
                other.CurrentRegionID == member.CurrentRegionID &&
                other.GetDistanceTo(member) <= CohesionRadius))
            .ThenBy(member => member.DatabaseID)
            .FirstOrDefault();
        if (anchor != null && TryChooseRendezvous(anchor, session.ObjectiveKind,
                out Vector3 nearby, out string nearbyName, out ushort nearbyRegion))
        {
            Vector3 previous = session.Rendezvous;
            string previousName = session.RendezvousName;
            ushort previousNearbyRegion = session.RendezvousRegion;
            session.Rendezvous = nearby;
            session.RendezvousName = nearbyName;
            session.RendezvousRegion = nearbyRegion;
            if (TryBuildRendezvousSlots(session, members))
                return;
            session.Rendezvous = previous;
            session.RendezvousName = previousName;
            session.RendezvousRegion = previousNearbyRegion;
            TryBuildRendezvousSlots(session, members);
        }

        Region region = WorldMgr.GetRegion(session.RendezvousRegion);
        if (region?.IsCapitalCity == true) return;
        Vector3 original = session.Rendezvous;
        GameNPC town = region?.Objects.OfType<GameNPC>()
            .Where(npc => npc.ObjectState == GameObject.eObjectState.Active &&
                npc is not GameBot && (npc.Realm == realm || npc.Realm == eRealm.None) && IsSafeArea(npc))
            .OrderBy(npc => Vector3.DistanceSquared(original, new(npc.X, npc.Y, npc.Z)))
            .FirstOrDefault();
        if (town != null && AutonomousRendezvousNavigation.TryChoosePoint(PathfindingProvider.Instance,
                town.CurrentZone, new(town.X, town.Y, town.Z), out Vector3 connected))
        {
            Vector3 previous = session.Rendezvous;
            session.Rendezvous = connected;
            if (TryBuildRendezvousSlots(session, BotMembers(session.Group)))
                return;
            session.Rendezvous = previous;
            TryBuildRendezvousSlots(session, BotMembers(session.Group));
        }
        AutonomousStuckWatchdog.CapitalLocation capital = AutonomousStuckWatchdog.SafeCapitalFor(realm);
        ushort previousRegion = session.RendezvousRegion;
        Vector3 previousPoint = session.Rendezvous;
        session.RendezvousRegion = capital.RegionId;
        session.Rendezvous = new(capital.X, capital.Y, capital.Z);
        if (!TryBuildRendezvousSlots(session, BotMembers(session.Group)))
        {
            session.RendezvousRegion = previousRegion;
            session.Rendezvous = previousPoint;
            TryBuildRendezvousSlots(session, BotMembers(session.Group));
        }
    }

    private static bool IsSafeArea(GameObject obj) => obj?.CurrentZone?.GetAreasOfSpot(obj)?
        .OfType<AbstractArea>()
        .Any(area => area.IsSafeArea || area is Area.BindArea) == true;

    private static Directive BuildDirective(Session session, GameBot[] members)
    {
        GameBot leader = ChooseLeader(session, members);
        GameBot puller = session.ObjectiveKind == eAutonomousObjectiveKind.RvR
            ? null
            : ChoosePuller(session, members);
        bool groupCombatActive = AutonomousGroupCombat.AnyHoldsGroup(members, leader, GameLoop.GameLoopTime);
        string goal = session.ObjectiveKind == eAutonomousObjectiveKind.RvR
            ? "Assemble, then roam frontier keeps, relic routes, and enemy realm forces"
            : session.Camp == null
            ? $"Assemble, then choose a shared level {(int)Math.Round(members.Average(member => member.Level)) + session.PreferredLevelBonus} target"
            : $"Group grind {session.Camp.MonsterName} (L{session.Camp.TargetLevel}) in {session.Camp.ZoneName}";
        string status = session.Phase switch
        {
            "Leader staging" => $"Leader {leader?.Name} is traveling to {session.RendezvousName}; members wait until the town position is established",
            "Meeting up" => $"Meeting leader {leader?.Name} in {session.RendezvousName} and forming the locked {session.LockedSize}-bot party",
            "Regrouping" => session.LeaderReadyForAssembly
                ? $"Re-forming around leader {leader?.Name}; shared task timer is still running"
                : $"Leader {leader?.Name} is establishing the regroup point; shared task timer is still running",
            "Choosing group target" => "Formation ready; leader is choosing group-difficulty content",
            "Traveling" => $"Traveling together to {session.Camp?.MonsterName}",
            "Grinding" => $"Grinding {session.Camp?.MonsterName} as a party",
            _ => session.Phase,
        };
        if (session.TaskClock.DeadlineTick.HasValue && AutonomousRealmRaid.GetView(session.Group) == null)
            status += $"; shared task ends {session.TaskClock.ExpiresUtc:HH:mm} UTC";
        return new(session.Id, session.Phase, goal, status, session.ObjectiveKind, leader, session.Rendezvous, session.Camp,
            members.Length, (int)Math.Round(members.Average(member => member.Level)), session.WipePenalty,
            session.PreferredLevelBonus, true)
        {
            RendezvousRegion = session.RendezvousRegion,
            LeaderName = leader?.Name ?? string.Empty,
            RendezvousName = session.RendezvousName,
            Puller = puller,
            LeaderReadyForAssembly = session.LeaderReadyForAssembly,
            GroupCombatActive = groupCombatActive,
            RecoveringBetweenPulls = session.RecoveringBetweenPulls
        };
    }

    /// <summary>
    /// Group camps are +3 to +bonus over the party level. A full eight keeps
    /// the +3..+10 roll; smaller parties keep the +3 floor with a lower top.
    /// </summary>
    public static int RollPreferredLevelBonus(int groupSize, Random random = null)
    {
        random ??= Random.Shared;
        int highest = groupSize >= 8 ? 10 : groupSize == 7 ? 8 : 6;
        return random.Next(3, highest + 1);
    }

    public const int MinimumPveFormationSize = 5;

    private static readonly BotPveGroupRole[] RequiredPveSlots =
    [
        BotPveGroupRole.Tank, BotPveGroupRole.Tank,
        BotPveGroupRole.Healer, BotPveGroupRole.Healer,
        BotPveGroupRole.Buffer,
        BotPveGroupRole.Attacker, BotPveGroupRole.Attacker, BotPveGroupRole.Attacker
    ];

    private const BotPveGroupRole T = BotPveGroupRole.Tank, H = BotPveGroupRole.Healer,
        B = BotPveGroupRole.Buffer, A = BotPveGroupRole.Attacker;

    /// <summary>
    /// Largest and most complete first, so the full eight is always tried before
    /// any smaller party. Every template keeps at least one tank, one healer and
    /// one attacker, which is what CanUsePveCombatRoster requires.
    /// </summary>
    private static readonly BotPveGroupRole[][] PveRosterTemplates =
    [
        RequiredPveSlots,
        [T, T, H, H, A, A, A], [T, T, H, B, A, A, A], [T, H, H, B, A, A, A],
        [T, T, H, H, A, A], [T, H, H, B, A, A], [T, T, H, B, A, A], [T, H, B, A, A, A], [T, T, H, A, A, A],
        [T, H, H, A, A], [T, T, H, A, A], [T, H, B, A, A], [T, H, A, A, A]
    ];

    public static bool LevelsCompatible(int first, int second) =>
        first >= 50 || second >= 50 ? first >= 50 && second >= 50 : Math.Abs(first - second) <= 5;

    public static bool CanRecruitPveMember(bool riding, bool returning, bool inCombat,
        bool attacking, bool hasAggro, int distanceFromLeader) =>
        !riding && !returning && !inCombat && !attacking && !hasAggro &&
        distanceFromLeader >= 0 && distanceFromLeader <= MaximumPveRecruitmentDistance;

    private static bool TryBuildPveRoster(GameBot leader, GameBot[] candidates, out GameBot[] selected,
        out Dictionary<long, BotPveGroupRole> roles, int minimumSize = 8, int maximumSize = 8)
    {
        selected = [];
        roles = null;
        GameBot[] pool = candidates.Where(candidate => candidate?.CharacterClass != null)
            .OrderBy(_ => Random.Shared.Next()).ToArray();
        eCharacterClass[] poolClasses = pool.Select(candidate => (eCharacterClass)candidate.CharacterClass.ID).ToArray();
        if (!TryMatchPveRoster((eCharacterClass)leader.CharacterClass.ID, poolClasses, minimumSize, maximumSize,
                out BotPveGroupRole[] template, out int[] owners))
            return false;
        GameBot[] slotted = owners.Select(owner => owner < 0 ? leader : pool[owner]).ToArray();
        selected = slotted.Where(owner => owner != leader).ToArray();
        roles = new Dictionary<long, BotPveGroupRole>();
        for (int index = 0; index < slotted.Length; index++)
            roles[MemberKey(slotted[index])] = template[index];
        return true;
    }

    /// <summary>
    /// Fills the first template (largest first) whose size is allowed. owners
    /// holds a pool index per slot, or -1 for the leader.
    /// </summary>
    private static bool TryMatchPveRoster(eCharacterClass leader, eCharacterClass[] pool, int minimumSize,
        int maximumSize, out BotPveGroupRole[] template, out int[] owners)
    {
        foreach (BotPveGroupRole[] candidateTemplate in PveRosterTemplates)
        {
            if (candidateTemplate.Length < minimumSize || candidateTemplate.Length > maximumSize)
                continue;
            template = candidateTemplate;
            foreach (int leaderSlot in Enumerable.Range(0, template.Length)
                         .Where(index => BotPartyRoles.CanFill(leader, candidateTemplate[index]))
                         .OrderBy(_ => Random.Shared.Next()))
            {
                owners = Enumerable.Repeat(int.MinValue, template.Length).ToArray();
                owners[leaderSlot] = -1;
                for (int candidate = 0; candidate < pool.Length; candidate++)
                    TryMatchPveSlot(candidate, pool, template, owners, new bool[template.Length], leaderSlot);
                if (owners.All(owner => owner != int.MinValue))
                    return true;
            }
        }
        template = null;
        owners = null;
        return false;
    }

    private static bool TryAssignExactPveRoles(GameBot[] members, GameBot leader,
        out Dictionary<long, BotPveGroupRole> roles)
    {
        roles = null;
        if (members.Length < MinimumPveFormationSize || members.Length > 8 || leader == null)
            return false;
        return TryBuildPveRoster(leader, members.Where(member => member != leader).ToArray(), out GameBot[] selected,
                   out roles, members.Length, members.Length) &&
               selected.Length == members.Length - 1 && selected.All(members.Contains);
    }

    private static bool TryMatchPveSlot(int candidate, eCharacterClass[] pool, BotPveGroupRole[] template,
        int[] owners, bool[] visited, int reservedLeaderSlot)
    {
        for (int slot = 0; slot < template.Length; slot++)
        {
            if (slot == reservedLeaderSlot || visited[slot] || !BotPartyRoles.CanFill(pool[candidate], template[slot]))
                continue;
            visited[slot] = true;
            if (owners[slot] == int.MinValue ||
                TryMatchPveSlot(owners[slot], pool, template, owners, visited, reservedLeaderSlot))
            {
                owners[slot] = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>Party size PvE formation would build from these classes (0 when none fits).</summary>
    public static int PveFormationSize(eCharacterClass leader, params eCharacterClass[] candidates) =>
        TryMatchPveRoster(leader, candidates, MinimumPveFormationSize, 8, out BotPveGroupRole[] template, out _)
            ? template.Length : 0;

    private static bool HasActivePveComposition(Session session, GameBot[] members) =>
        session != null && members != null &&
        CanUsePveCombatRoster(session.LockedSize, members.Length,
            members.Count(member => session.PveRoles.TryGetValue(MemberKey(member), out BotPveGroupRole role) && role == BotPveGroupRole.Tank),
            members.Count(member => session.PveRoles.TryGetValue(MemberKey(member), out BotPveGroupRole role) && role == BotPveGroupRole.Healer),
            members.Count(member => session.PveRoles.TryGetValue(MemberKey(member), out BotPveGroupRole role) && role == BotPveGroupRole.Attacker)) &&
        members.All(member => session.PveRoles.ContainsKey(MemberKey(member)));

    public static bool CanUsePveCombatRoster(int lockedSize, int activeSize,
        int tanks, int healers, int attackers) =>
        activeSize >= 3 && activeSize <= 8 && activeSize == lockedSize &&
        tanks > 0 && healers > 0 && attackers > 0;

    private static GameBot ChooseLockedPvePuller(Session session, GameBot[] members)
    {
        if (AutonomousDefensivePull.UsesDefensivePull(true, members.Length, members.All(member => member.Level == 50)))
        {
            GameBot ranged = members.Where(AutonomousDefensivePull.HasRangedPull)
                .OrderBy(member => session.PveRoles.TryGetValue(MemberKey(member), out BotPveGroupRole role)
                    ? role == BotPveGroupRole.Tank ? 0 : role == BotPveGroupRole.Healer ? 2 : 1 : 3)
                .ThenBy(MemberKey).FirstOrDefault();
            if (ranged != null) return ranged;
        }
        GameBot[] tanks = members.Where(member => session.PveRoles.TryGetValue(MemberKey(member), out BotPveGroupRole role) &&
                                                  role == BotPveGroupRole.Tank)
            .OrderBy(MemberKey)
            .ToArray();
        bool levelFiftyGroup = members.Length > 0 && members.All(member => member.Level == 50);
        int slot = PreferredPveTankSlot(tanks.Select(member => (int)member.Level).ToArray(), levelFiftyGroup);
        return slot >= 0 ? tanks[slot] : null;
    }

    public static bool UsesDefensivePullGroup(Group group)
    {
        lock (Sync)
            return TryGetSession(group, out Session session) &&
                AutonomousDefensivePull.UsesDefensivePull(session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve,
                    group.MemberCount, BotMembers(group).All(member => member.Level == 50));
    }

    private static void LogNoShow(Session session, GameBot member, long now)
    {
        OfflineWorldBotRecord record = member.PersistentRecord;
        Log.Warn("AUTONOMOUS_GROUP_NO_SHOW " + JsonSerializer.Serialize(new
        {
            group = session.Id, bot = member.Name, id = member.DatabaseID, level = member.Level,
            className = member.ClassName, realm = GlobalConstants.RealmToName(member.Realm),
            assignedRole = session.PveRoles.TryGetValue(MemberKey(member), out BotPveGroupRole role)
                ? BotPartyRoles.GroupRoleLabel(role) : string.Empty,
            waitedSeconds = session.Attendance.WaitedMilliseconds(member.DatabaseID, now) / 1000,
            region = member.CurrentRegionID, x = member.X, y = member.Y, z = member.Z,
            rendezvousRegion = session.RendezvousRegion, rendezvousX = session.Rendezvous.X,
            rendezvousY = session.Rendezvous.Y, rendezvousZ = session.Rendezvous.Z,
            activity = record?.Activity, goal = record?.CurrentGoal, destination = record?.TravelDestination,
            routeStatus = record?.ObjectiveProgress, riding = member.IsOnStableMasterRoute,
            horseDestination = member.StableRouteDestination, deaths = record?.DeathCount,
            assignment = record?.ObjectiveAssignmentId, camp = record?.CurrentCampId,
            lastProgressUtc = record?.LastMeaningfulProgressUtc
        }));
    }

    public static int MaximumAutonomousGrouped(int activePopulation) =>
        (int)Math.Floor(Math.Max(0, activePopulation) * 0.40);

    public static long RollGrindingLifetimeMilliseconds(Random random = null)
    {
        random ??= Random.Shared;
        return new AutonomousGroupTaskClock(eAutonomousObjectiveKind.GroupPve, random).DurationMilliseconds;
    }

    private static void WriteSessionMetadata(Session session, GameBot[] members)
    {
        Directive directive = BuildDirective(session, members);
        // A member's AI pulse must not serialize/save all eight members again
        // just because one second elapsed. Only real phase/arrival/clock changes
        // invalidate this small per-session cache.
        if (directive == session.PublishedDirective &&
            session.PublishedAttendanceRevision == session.Attendance.Revision &&
            session.PublishedDeadlineTick == session.TaskClock.DeadlineTick &&
            session.PublishedRemaining == session.TaskClock.PausedRemainingMilliseconds &&
            session.PublishedMembers.SequenceEqual(members))
            return;
        session.PublishedDirective = directive;
        session.PublishedMembers = members;
        session.PublishedAttendanceRevision = session.Attendance.Revision;
        session.PublishedDeadlineTick = session.TaskClock.DeadlineTick;
        session.PublishedRemaining = session.TaskClock.PausedRemainingMilliseconds;
        foreach (GameBot member in members)
        {
            if (member.PersistentRecord == null) continue;
            string expiry = session.TaskClock.IsPaused ? string.Empty : session.TaskClock.ExpiresUtc.ToString("O");
            member.PersistentRecord.ObjectiveExpiresUtc = expiry;
            // While running, serialize the fixed deadline, NOT the changing remaining
            // seconds. Paused remaining time and attendance only change on transitions.
            SetMetadata(member, directive.GroupId, directive.Phase, directive.SharedGoal, directive.Status,
                new StoredMetadata
                {
                    HasTaskClock = true,
                    TaskTimerPaused = session.TaskClock.IsPaused,
                    TaskRemainingMilliseconds = session.TaskClock.IsPaused ? session.TaskClock.PausedRemainingMilliseconds : 0,
                    TaskExpiresUtc = expiry,
                    MeetUpDeadlineUtc = session.Phase == "Leader staging"
                        ? session.LeaderStagingDeadlineUtc.ToString("O")
                        : session.Phase == "Meeting up"
                            ? session.Attendance.DeadlineUtc(member.DatabaseID)?.ToString("O") ?? string.Empty
                            : string.Empty,
                    LeaderName = directive.LeaderName,
                    RendezvousName = directive.RendezvousName,
                    PullerName = directive.Puller?.Name ?? string.Empty,
                    MemberRole = session.PveRoles.TryGetValue(MemberKey(member), out BotPveGroupRole role)
                        ? BotPartyRoles.GroupRoleLabel(role)
                        : string.Empty
                });
        }
    }

    private static void SetMetadata(GameBot bot, string id, string phase, string goal, string status, StoredMetadata metadata = null)
    {
        if (bot?.PersistentRecord == null)
            return;
        metadata ??= new StoredMetadata();
        metadata.GroupId = id;
        metadata.Phase = phase;
        metadata.SharedGoal = goal;
        metadata.Status = status;
        string value = MetadataPrefix + JsonSerializer.Serialize(metadata);
        if (bot.PersistentRecord.ItineraryJson == value)
            return;
        bot.PersistentRecord.ItineraryJson = value;
        bot.MarkAutonomousStateDirty();
        AutonomousBotStatusPersistence.QueueGroupMetadata(bot);
    }

    private static void ClearMetadata(GameBot bot, bool save)
    {
        if (bot?.PersistentRecord == null || !bot.PersistentRecord.ItineraryJson.StartsWith(MetadataPrefix, StringComparison.Ordinal))
            return;
        bot.PersistentRecord.ItineraryJson = string.Empty;
        bot.MarkAutonomousStateDirty();
        if (save)
            AutonomousBotStatusPersistence.QueueGroupMetadata(bot);
    }

    private static GameBot[] BotMembers(Group group) => group?.GetMembersInTheGroup()
        .OfType<GameBot>()
        .Where(bot => bot.IsAutonomousWorldBot && !bot.IsTemporaryGroupHelper && bot.ObjectState == GameObject.eObjectState.Active)
        .ToArray() ?? [];

    private static GameBot[] AllBotMembers(Group group) => group?.GetMembersInTheGroup()
        .OfType<GameBot>()
        .Where(bot => bot.IsAutonomousWorldBot && !bot.IsTemporaryGroupHelper)
        .ToArray() ?? [];

    private static GameBot ChooseLeader(Group group, GameBot[] members) =>
        members.FirstOrDefault(member => member == group?.LivingLeader && member.IsAlive) ??
        members.FirstOrDefault(member => member.IsAlive) ?? members.FirstOrDefault();

    private static GameBot ChooseLeader(Session session, GameBot[] members)
    {
        if (session?.Leader != null && session.Leader.IsAlive && session.Leader.Group == session.Group &&
            members.Contains(session.Leader))
            return session.Leader;

        GameBot replacement = ChooseLeader(session?.Group, members);
        if (session != null)
            session.Leader = replacement;
        return replacement;
    }

    private static GameBot ChoosePuller(Session session, GameBot[] members)
    {
        if (AutonomousRealmRaid.GetView(session.Group) != null && session.Camp is { } camp)
        {
            var available = members.Where(b => b.IsAlive && !b.IsOnStableMasterRoute && b.CurrentRegionID == camp.RegionId).ToArray();
            var anchor = available.OrderBy(b => Vector3.DistanceSquared(new(b.X,b.Y,b.Z),new(camp.X,camp.Y,camp.Z))).FirstOrDefault();
            return available.Where(b => anchor != null && b.GetDistanceTo(anchor) <= 1100)
                .OrderBy(b => AutonomousDefensivePull.HasRangedPull(b) ? 0 : 1).ThenBy(MemberKey).FirstOrDefault();
        }
        if (session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve)
        {
            if (session.Puller?.IsAlive == true && members.Contains(session.Puller))
                return session.Puller;
            session.Puller = ChooseLockedPvePuller(session, members);
            return session.Puller;
        }
        GameBot leader = ChooseLeader(session, members);
        GameBot[] ordered = members.Where(member => member.IsAlive)
            .OrderBy(member => member == leader ? 0 : 1)
            .ThenBy(MemberKey)
            .ToArray();
        int leaderIndex = Array.IndexOf(ordered, leader);
        int pullerIndex = PreferredPullerIndex(ordered.Select(member =>
            BotPartyRoles.For((eCharacterClass)member.CharacterClass.ID)).ToArray(), leaderIndex);
        return pullerIndex >= 0 ? ordered[pullerIndex] : leader;
    }

    private static long _nextBrokenSessionSweep;
    public const int BrokenSessionSweepMilliseconds = 500;

    private static void RemoveBrokenSessions()
    {
        foreach ((Group group, Session session) in Sessions.ToArray())
            RemoveBrokenSession(group, session);
    }

    private static void RemoveBrokenSession(Group group, Session session)
    {
        if (AutonomousRealmRaid.TryConsumeRelease(group, out string releaseReason))
        {
            FinishGroupTask(session, releaseReason);
            return;
        }
        GameBot[] members = BotMembers(group);
        bool requiredSize = AutonomousRealmRaid.GetView(group) != null || session.ObjectiveKind != eAutonomousObjectiveKind.GroupPve ||
                            HasViablePveComposition(session, members);
        if (members.Length >= 2 && requiredSize && members.All(member => member.Group == group) &&
            !group.GetMembersInTheGroup().Any(member => member is GamePlayer))
            return;
        FinishGroupTask(session, session.ObjectiveKind == eAutonomousObjectiveKind.GroupPve && !requiredSize
            ? "The PvE party lost its viable tank, healer, or attacker core"
            : members.Length < 2
            ? "Fewer than two active members remain; dissolving the orphaned party"
            : "The group roster is no longer valid");
    }
}

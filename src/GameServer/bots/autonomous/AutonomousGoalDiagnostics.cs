using System;
using System.Text.Json;
using System.Threading;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS
{
    public static class AutonomousDiagnosticsProperties
    {
        [ServerProperty("autonomous_diagnostics", "bot_goal_attempt_tracking", "Silent event-driven PvE camp attempt summaries. Diagnostic only; never changes goals or movement.", true)]
        public static bool GoalAttempts = true;

        [ServerProperty("autonomous_diagnostics", "bot_equipment_upgrade_tracking", "One log record after a persistent bot successfully equips an upgrade.", true)]
        public static bool EquipmentUpgrades = true;

        [ServerProperty("autonomous_diagnostics", "bot_training_tracking", "One log record after successful persistent bot trainer training.", true)]
        public static bool Training = true;
    }

    public enum GoalAttemptEnd
    {
        Reassigned, GroupChanged, ServiceDetour, Logout, Expired,
        EmptyCamp, RouteFailure, Defeated, NoMovement15m, NoProgress45m, InvalidPosition, Error
    }

    /// <summary>
    /// One small state object per active camp attempt. No world scans, pathfinding,
    /// database writes, timers, or per-kill log messages. Measurements never feed
    /// back into decision logic. Offline analysis computes cross-bot failure rates.
    /// </summary>
    public sealed class AutonomousGoalAttempt
    {
        private readonly object _gate = new();
        private bool _closed;
        public string AttemptId { get; } = Guid.NewGuid().ToString("N");
        public string CampId { get; init; }
        public string Target { get; init; }
        public string AssignmentId { get; init; }
        public string GroupId { get; init; }
        public long BotId { get; init; }
        public string BotName { get; init; }
        public string ClassName { get; init; }
        public int Level { get; init; }
        public int Realm { get; init; }
        public int GroupSize { get; init; }
        public double GroupAverageLevel { get; init; }
        public string GroupClasses { get; init; }
        public int Region { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public int Z { get; init; }
        public string Zone { get; init; }
        public int StartRegion { get; init; }
        public int StartX { get; init; }
        public int StartY { get; init; }
        public int StartZ { get; init; }
        public DateTime StartedUtc { get; init; }
        public DateTime ExpiresUtc { get; init; }
        public DateTime? ArrivedUtc { get; private set; }
        public int Kills { get; private set; }
        public int TargetKills { get; private set; }
        public long Experience { get; private set; }
        public long TargetExperience { get; private set; }
        public int Deaths { get; private set; }
        public int RecoveryAttempts { get; private set; }
        public string LastPathStatus { get; private set; } = string.Empty;

        public bool MatchesTarget(string name, int region, int x, int y, int z)
        {
            // Location matters: a kill of the same species on the other side of
            // the region must not hide a broken route to this specific camp.
            long dx = (long)x - X, dy = (long)y - Y;
            return region == Region && string.Equals(name, Target, StringComparison.OrdinalIgnoreCase) &&
                   dx * dx + dy * dy <= 2600L * 2600 && Math.Abs((long)z - Z) <= 512;
        }

        public void Arrive(DateTime utcNow)
        {
            if (ArrivedUtc.HasValue || _closed) return;
            lock (_gate)
            {
                if (!_closed) ArrivedUtc ??= utcNow;
            }
        }

        public void Reward(bool assignedTarget, long experience)
        {
            lock (_gate)
            {
                if (_closed) return;
                Kills++;
                Experience += Math.Max(0, experience);
                if (assignedTarget)
                {
                    TargetKills++;
                    TargetExperience += Math.Max(0, experience);
                }
            }
        }

        public void Died()
        {
            lock (_gate) { if (!_closed) Deaths++; }
        }

        public void RouteFailed(string pathStatus)
        {
            lock (_gate)
            {
                if (_closed) return;
                RecoveryAttempts++;
                LastPathStatus = pathStatus;
            }
        }

        public static string Classify(GoalAttemptEnd reason, int targetKills)
        {
            if (targetKills > 0) return "productive";
            return reason is GoalAttemptEnd.Reassigned or GoalAttemptEnd.GroupChanged or
                GoalAttemptEnd.ServiceDetour or GoalAttemptEnd.Logout ? "interrupted" : "failed";
        }

        public object Finish(GoalAttemptEnd reason, string detail, DateTime utcNow, int region, int x, int y, int z)
        {
            lock (_gate)
            {
                if (_closed) return null;
                _closed = true;
                if (reason == GoalAttemptEnd.Reassigned && ExpiresUtc != default && utcNow >= ExpiresUtc)
                    reason = GoalAttemptEnd.Expired;
                return new
                {
                    schema = 1, attemptId = AttemptId, campId = CampId, target = Target,
                    assignmentId = AssignmentId, groupId = GroupId, botId = BotId, bot = BotName,
                    className = ClassName, level = Level, realm = Realm, groupSize = GroupSize,
                    groupAverageLevel = GroupAverageLevel, groupClasses = GroupClasses,
                    goalRegion = Region, goalX = X, goalY = Y, goalZ = Z, zone = Zone,
                    startRegion = StartRegion, startX = StartX, startY = StartY, startZ = StartZ,
                    endRegion = region, endX = x, endY = y, endZ = z,
                    startedUtc = StartedUtc, endedUtc = utcNow, arrivedUtc = ArrivedUtc,
                    elapsedSeconds = Math.Max(0, (utcNow - StartedUtc).TotalSeconds),
                    kills = Kills, targetKills = TargetKills, xp = Experience, targetXp = TargetExperience,
                    deaths = Deaths, recoveryAttempts = RecoveryAttempts,
                    terminalRouteFailures = reason == GoalAttemptEnd.RouteFailure ? 1 : 0, lastPathStatus = LastPathStatus,
                    reason = reason.ToString(), outcome = Classify(reason, TargetKills), detail
                };
            }
        }
    }

    public static class AutonomousGoalDiagnostics
    {
        private static readonly Logger Log = LoggerManager.Create(typeof(AutonomousGoalDiagnostics));

        public static void Begin(GameBot bot, string campId, string target, string zone, int region,
            int x, int y, int z, string groupId, int groupSize, double groupAverageLevel, string groupClasses)
        {
            try
            {
            if (!AutonomousDiagnosticsProperties.GoalAttempts || bot?.IsAutonomousWorldBot != true ||
                bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup) return;
            if (bot.GoalDiagnosticAttempt?.CampId == campId) return;
            End(bot, GoalAttemptEnd.Reassigned, "Camp changed");
            DateTime.TryParse(bot.PersistentRecord?.ObjectiveExpiresUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime expiry);
            bot.GoalDiagnosticAttempt = new AutonomousGoalAttempt
            {
                CampId = campId, Target = target, Zone = zone, Region = region, X = x, Y = y, Z = z,
                AssignmentId = bot.PersistentRecord?.ObjectiveAssignmentId, GroupId = groupId,
                BotId = bot.DatabaseID, BotName = bot.Name, ClassName = bot.ClassName, Level = bot.Level,
                Realm = (int)bot.Realm, GroupSize = groupSize, GroupAverageLevel = groupAverageLevel,
                GroupClasses = groupClasses, StartRegion = bot.CurrentRegionID, StartX = bot.X,
                StartY = bot.Y, StartZ = bot.Z, StartedUtc = DateTime.UtcNow, ExpiresUtc = expiry
            };
            }
            catch { }
        }

        public static void Reward(GameBot bot, GameNPC killedNpc, long actualExperience)
        {
            try
            {
            AutonomousGoalAttempt attempt = bot?.GoalDiagnosticAttempt;
            if (!AutonomousDiagnosticsProperties.GoalAttempts || attempt == null || killedNpc == null) return;
            bool matches = attempt.MatchesTarget(killedNpc.Name, killedNpc.CurrentRegionID,
                killedNpc.X, killedNpc.Y, killedNpc.Z) || attempt.MatchesTarget(killedNpc.Name,
                killedNpc.CurrentRegionID, killedNpc.SpawnPoint.X, killedNpc.SpawnPoint.Y, killedNpc.SpawnPoint.Z);
            attempt.Reward(matches, actualExperience);
            }
            catch { }
        }

        public static void End(GameBot bot, GoalAttemptEnd reason, string detail,
            (int Region, int X, int Y, int Z)? failurePosition = null)
        {
            if (bot == null) return;
            AutonomousGoalAttempt attempt = Interlocked.Exchange(ref bot.GoalDiagnosticAttempt, null);
            if (attempt == null || !AutonomousDiagnosticsProperties.GoalAttempts) return;
            // A logging problem must never prevent movement, recovery or logout.
            try
            {
                var position = failurePosition ?? (bot.CurrentRegionID, bot.X, bot.Y, bot.Z);
                object summary = attempt.Finish(reason, detail, DateTime.UtcNow,
                    position.Item1, position.Item2, position.Item3, position.Item4);
                if (summary != null) Log.Info("AUTONOMOUS_GOAL_ATTEMPT " + JsonSerializer.Serialize(summary));
            }
            catch { }
        }
    }
}

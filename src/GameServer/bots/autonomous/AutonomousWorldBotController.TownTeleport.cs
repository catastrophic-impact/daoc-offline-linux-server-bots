using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>
    /// Town teleporter travel (see AutonomousTownTeleporters). Solo bots and members still travelling to
    /// a meetup plan for themselves; an assembled party (PvE, RvR warband or raid party) is planned by its
    /// leader, gathers at the teleporter and is sent together. Stable-master routes remain the alternative
    /// whenever they are faster, and after arrival the bot plans again (a horse from the arrival town is
    /// still taken when it helps).
    /// </summary>
    public sealed partial class AutonomousWorldBotController
    {
        private AutonomousTownTeleporters.Plan _townPort;
        private ushort _townPortGoalRegion;
        private Vector3 _townPortGoal;
        private long _townPortStartedTick, _townPortChannelUntil, _townPortGatherSinceTick, _nextTownPortPlanTick, _townPortActiveTick;
        // A plan the bot stopped working on (resting after a death, a fight, shopping) is dropped
        // quietly; only time spent actually trying counts toward the five-minute failure (run 14:
        // 152 of 165 failures were bots that respawned, planned, then rested past the deadline).
        private const long TownPortIdleDropMilliseconds = 30_000;
        private readonly Dictionary<GameNPC, long> _failedTownPorters = new();
        private const long TownPortGiveUpMilliseconds = 5 * 60_000;
        private const long TownPortGatherMilliseconds = 120_000;
        private const int TownPortPartyRadius = 600;

        /// <summary>
        /// True when this turn went to teleporter travel (planning succeeded, walking, gathering,
        /// channelling or the jump itself). <paramref name="walkSeconds"/> is the on-foot estimate;
        /// a stable route found for the same goal must be slower for the teleporter to win.
        /// </summary>
        private bool TryTownTeleport(GameBot bot, ushort goalRegion, Vector3 goal, double walkSeconds)
        {
            if (!AutonomousTownTeleporters.IsEnabled || bot.CurrentRegion == null || !bot.IsAlive ||
                bot.IsOnStableMasterRoute || bot.CurrentZone?.IsDungeon == true || GameRelic.IsPlayerCarryingRelic(bot) ||
                bot.Brain is not BotBrain brain || brain.HasAggro || bot.InCombat || bot.IsAttacking)
            {
                if (_townPort != null && bot.InCombat) _townPortChannelUntil = 0;
                return false;
            }
            bool assembledParty = _groupDirective?.IsDynamic == true &&
                                  !AutonomousBotGroupCoordinator.IsAssemblyPhase(_groupDirective.Phase);
            if (assembledParty && _groupDirective.Leader != bot) return false;
            long now = GameLoop.GameLoopTime;

            if (_townPort != null && now - _townPortActiveTick > TownPortIdleDropMilliseconds)
                ClearTownPort();
            if (_townPort != null && (_townPort.Porter.ObjectState != GameObject.eObjectState.Active ||
                                      _townPort.Porter.CurrentRegion != bot.CurrentRegion || _townPortGoalRegion != goalRegion ||
                                      Vector3.DistanceSquared(_townPortGoal, goal) > 2_000f * 2_000f))
                ClearTownPort();
            if (_townPort == null)
            {
                if (now < _nextTownPortPlanTick) return false;
                _nextTownPortPlanTick = now + 20_000 + bot.ObjectID % 5_000;
                foreach (GameNPC expired in _failedTownPorters.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                    _failedTownPorters.Remove(expired);
                AutonomousTownTeleporters.Plan plan = AutonomousTownTeleporters.TryPlan(bot, goalRegion, goal, walkSeconds, _failedTownPorters);
                if (plan == null) return false;
                // A horse that beats the teleporter trip keeps its place (same goal, same region only).
                if (goalRegion == bot.CurrentRegionID &&
                    AutonomousStableRoutePlanner.FindBest(bot, goal) is { } horse && horse.EstimatedSeconds <= plan.Seconds)
                    return false;
                _townPort = plan;
                _townPortGoalRegion = goalRegion;
                _townPortGoal = goal;
                _townPortStartedTick = now;
                _townPortChannelUntil = 0;
                _townPortGatherSinceTick = 0;
                Log.Info($"AUTONOMOUS_TOWN_TELEPORT_PLANNED bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} " +
                         $"group={(assembledParty ? _groupDirective.GroupId : "solo")} porter=\"{plan.Porter.Name}\" " +
                         $"destination=\"{plan.Destination.TeleportID}\" seconds={plan.Seconds:0} walkSeconds={walkSeconds:0}");
            }

            AutonomousTownTeleporters.Plan port = _townPort;
            _townPortActiveTick = now;
            if (now - _townPortStartedTick > TownPortGiveUpMilliseconds)
            {
                FailTownPort(bot, "no teleport within five minutes");
                return false;
            }
            if (!bot.IsWithinRadius(port.Porter, AutonomousTownTeleporters.ArrivalRadius))
            {
                // stop a short way out, never on the NPC (players must be able to click the teleporter)
                Vector3 target = AutonomousTownTeleporters.StandOff(port.Porter, bot);
                if (!IssuePath(bot, target))
                {
                    FailTownPort(bot, "no connected route to the teleporter");
                    return false;
                }
                SetStatus(bot, $"Walking to {port.Porter.Name}", GoalText(),
                    $"Taking the town teleporter to {port.Destination.TeleportID}; faster than walking or riding there",
                    _camp?.MonsterName ?? string.Empty, port.Porter.Name);
                return true;
            }

            // Everyone goes together: the assembled party gathers on the teleporter first.
            GameBot[] party = assembledParty
                ? bot.Group.GetMembersInTheGroup().OfType<GameBot>().Where(member => member.IsAlive).ToArray()
                : [bot];
            GameBot[] present = party.Where(member => member.CurrentRegion == bot.CurrentRegion &&
                                                      member.IsWithinRadius(port.Porter, TownPortPartyRadius)).ToArray();
            if (present.Length < party.Length)
            {
                if (_townPortGatherSinceTick == 0) _townPortGatherSinceTick = now;
                if (now - _townPortGatherSinceTick < TownPortGatherMilliseconds)
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                    SetStatus(bot, $"Gathering at {port.Porter.Name}", GoalText(),
                        $"{present.Length}/{party.Length} of the party at the teleporter to {port.Destination.TeleportID}",
                        _camp?.MonsterName ?? string.Empty, port.Porter.Name);
                    return true;
                }
            }
            if (present.Any(member => member.InCombat || (member.Brain as BotBrain)?.HasAggro == true))
            {
                _townPortChannelUntil = 0;
                return false;
            }
            if (_townPortChannelUntil == 0)
            {
                _townPortChannelUntil = now + AutonomousTownTeleporters.ChannelMilliseconds;
                bot.StopMovingOnPath();
                bot.StopMoving();
            }
            if (now < _townPortChannelUntil)
            {
                SetStatus(bot, $"Teleporting to {port.Destination.TeleportID}", GoalText(),
                    $"{port.Porter.Name} is channelling the teleport", _camp?.MonsterName ?? string.Empty, port.Porter.Name);
                return true;
            }

            int moved = 0;
            for (int slot = 0; slot < present.Length; slot++)
            {
                GameBot member = present[slot];
                Vector3 arrival = AutonomousTownTeleporters.Arrival(port.Destination, slot);
                member.StopMovingOnPath();
                member.StopMoving();
                if (!member.MoveTo((ushort)port.Destination.RegionID, (int)arrival.X, (int)arrival.Y, (int)arrival.Z,
                        (ushort)port.Destination.Heading))
                    continue;
                moved++;
                member.ForcePathReplot();
                AutonomousStuckWatchdog.MarkProgress(member, eAutonomousProgressKind.Movement);
                AutonomousBotStatusPersistence.Queue(member, true);
            }
            Log.Info($"AUTONOMOUS_TOWN_TELEPORT bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} " +
                     $"group={(assembledParty ? _groupDirective.GroupId : "solo")} porter=\"{port.Porter.Name}\" " +
                     $"destination=\"{port.Destination.TeleportID}\" region={port.Destination.RegionID} moved={moved}/{party.Length} " +
                     $"plannedSeconds={port.Seconds:0} baselineSeconds={port.BaselineSeconds:0} " +
                     $"tookSeconds={(now - _townPortStartedTick) / 1000}");
            ClearTownPort();
            _nextPlanTick = 0;
            ResetRouteOrderState();
            return true;
        }

        /// <summary>Cross-region form: on foot means walking to the crossing and on from its far side.</summary>
        private bool TryTownTeleportAcross(GameBot bot, DOL.Database.DbZonePoint crossing, ushort goalRegion,
            int goalX, int goalY, int goalZ)
        {
            if (!AutonomousTownTeleporters.IsEnabled) return false;
            double walk = (Distance(bot.X, bot.Y, crossing.SourceX, crossing.SourceY) +
                           (crossing.TargetRegion == goalRegion ? Distance(crossing.TargetX, crossing.TargetY, goalX, goalY) : 60_000)) /
                          (double)Math.Max(150, (int)bot.MaxSpeed);
            return TryTownTeleport(bot, goalRegion, new(goalX, goalY, goalZ), walk);
        }

        private void FailTownPort(GameBot bot, string reason)
        {
            if (_townPort != null)
            {
                _failedTownPorters[_townPort.Porter] = GameLoop.GameLoopTime + 10 * 60_000;
                Log.Info($"AUTONOMOUS_TOWN_TELEPORT_FAILED bot={bot.Name} id={bot.DatabaseID} porter=\"{_townPort.Porter.Name}\" " +
                         $"destination=\"{_townPort.Destination.TeleportID}\" reason=\"{reason}\"");
            }
            ClearTownPort();
        }

        private void ClearTownPort()
        {
            _townPort = null;
            _townPortChannelUntil = 0;
            _townPortGatherSinceTick = 0;
        }
    }
}

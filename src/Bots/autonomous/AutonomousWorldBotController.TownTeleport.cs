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
        // Five minutes, or twice the planned trip when that is longer, as long as the bot keeps closing on the
        // teleporter; ninety seconds without getting closer ends it sooner (run 2026-10-13: 343 of 414 failures were
        // bots still walking in from a porter planned 250-300 seconds away).
        private const long TownPortGiveUpMilliseconds = 5 * 60_000;
        private const long TownPortNoProgressMilliseconds = 90_000;
        private Vector2 _townPortLastSpot;
        private long _townPortProgressTick;
        // Closest the bot has come to the porter, and when it last got 500 units closer.
        private float _townPortBestDistance = float.MaxValue;
        private long _townPortCloserTick;
        // Failure diagnostics (known issues #4, 1 in 30 trips): distance when planned and turns spent on the trip. 2026-10-16:
        // 513 of 566 failures ended far from every teleporter of that name while still walking, so something else was
        // steering the bot; the turn count says whether the trip was driven every turn.
        private float _townPortStartDistance;
        private int _townPortTurns;
        private Vector3 _townPortLastTarget;
        /// <summary>
        /// The give-up time stretches while the bot keeps closing in (500 units nearer within two minutes), up to
        /// <see cref="TownPortClosingLimitMilliseconds"/>. 2026-10-15: of 2,240 failures, 1,460 were bots still walking
        /// toward a porter more than 2,000 units away when the five minutes ran out.
        /// </summary>
        public const long TownPortClosingWindowMilliseconds = 2 * 60_000;
        public const long TownPortClosingLimitMilliseconds = 15 * 60_000;
        private const int TownPortStandOffTries = 4; // route searches per turn when the bot's own side has no route
        private const long TownPortGatherMilliseconds = 120_000;
        private const int TownPortPartyRadius = 600;
        /// <summary>
        /// A bot this much farther from the teleporter than the whole priced walk to it is being steered elsewhere (on
        /// the route it can never be farther than the route is long): the plan is dropped without blaming the
        /// teleporter. 2026-10-17: 162 of 175 failures had walked tens of thousands of units away (to their party's
        /// meetup, near their goal) while the plan, and the 10-minute ban of that teleporter it ends in, stayed.
        /// </summary>
        public const float TownPortSteeredAwayDistance = 4_000f;

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
                _townPortLastSpot = new(bot.X, bot.Y);
                _townPortProgressTick = now;
                _townPortBestDistance = float.MaxValue;
                _townPortCloserTick = now;
                _townPortStartDistance = (float)bot.GetDistanceTo(plan.Porter);
                _townPortTurns = 0;
                Log.Info($"AUTONOMOUS_TOWN_TELEPORT_PLANNED bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} " +
                         $"group={(assembledParty ? _groupDirective.GroupId : "solo")} porter=\"{plan.Porter.Name}\" " +
                         $"destination=\"{plan.Destination.TeleportID}\" seconds={plan.Seconds:0} walkSeconds={walkSeconds:0}");
            }

            AutonomousTownTeleporters.Plan port = _townPort;
            _townPortActiveTick = now;
            _townPortTurns++;
            // Progress is walking at all: a path may lead away from the teleporter first (out of a building, around a
            // wall), so distance to it is no measure (run 4: 1,099 walking bots gave up as "no progress").
            if (Vector2.DistanceSquared(new(bot.X, bot.Y), _townPortLastSpot) >= 64 * 64)
            {
                _townPortLastSpot = new(bot.X, bot.Y);
                _townPortProgressTick = now;
            }
            long allowed = Math.Max(TownPortGiveUpMilliseconds, (long)(port.Seconds * 2_000));
            if (bot.CurrentRegion == port.Porter.CurrentRegion)
            {
                float distance = (float)bot.GetDistanceTo(port.Porter);
                if (distance > Math.Max(port.Walk, Math.Min(_townPortStartDistance, _townPortBestDistance)) + TownPortSteeredAwayDistance &&
                    _townPortChannelUntil == 0 && _townPortGatherSinceTick == 0)
                {
                    var mover = bot.movementComponent as NpcMovementComponent;
                    Log.Info($"AUTONOMOUS_TOWN_TELEPORT_DROPPED bot={bot.Name} id={bot.DatabaseID} porter=\"{port.Porter.Name}\" " +
                             $"destination=\"{port.Destination.TeleportID}\" reason=\"steered away from the teleporter\" " +
                             $"region={bot.CurrentRegionID} at={bot.X},{bot.Y},{bot.Z} toPorter={(int)distance} " +
                             $"startDistance={(int)_townPortStartDistance} walk={(int)port.Walk} turns={_townPortTurns} seconds={(now - _townPortStartedTick) / 1000} " +
                             $"follow=\"{mover?.FollowTarget?.Name}\" moverDest={(mover == null ? "-" : $"{mover.Destination.X:0},{mover.Destination.Y:0}")} " +
                             $"lastTarget={(int)_townPortLastTarget.X},{(int)_townPortLastTarget.Y} group={_groupDirective?.GroupId ?? "-"} " +
                             $"phase={_groupDirective?.Phase.ToString() ?? "-"} activity=\"{bot.PersistentRecord?.Activity}\"");
                    ClearTownPort();
                    _nextTownPortPlanTick = now + 60_000;
                    return false;
                }
                if (distance < _townPortBestDistance - 500) { _townPortBestDistance = distance; _townPortCloserTick = now; }
                if (now - _townPortCloserTick < TownPortClosingWindowMilliseconds)
                    allowed = Math.Max(allowed, Math.Min(TownPortClosingLimitMilliseconds, now - _townPortStartedTick + 1));
            }
            if (now - _townPortStartedTick > allowed)
            {
                FailTownPort(bot, $"no teleport within {allowed / 60_000} minutes");
                return false;
            }
            if (_townPortChannelUntil == 0 && _townPortGatherSinceTick == 0 && now - _townPortProgressTick > TownPortNoProgressMilliseconds &&
                !bot.IsWithinRadius(port.Porter, AutonomousTownTeleporters.ArrivalRadius))
            {
                FailTownPort(bot, "no movement toward the teleporter for ninety seconds");
                return false;
            }
            if (!bot.IsWithinRadius(port.Porter, AutonomousTownTeleporters.ArrivalRadius))
            {
                // stop a short way out, never on the NPC (players must be able to click the teleporter); when the bot's own
                // side has no route (a ledge or roof), try other sides before giving the teleporter up
                // A bot already walking somewhere (its camp) turns for the teleporter at once: an unchanged order is kept
                // while moving, and the old walk went on, away from the teleporter, until the trip failed (2026-10-17).
                bool walking = false;
                foreach (Vector3 target in AutonomousTownTeleporters.StandOffs(port.Porter, bot).Take(TownPortStandOffTries))
                    if (IssuePath(bot, target, retargetWhileMoving: true))
                    {
                        _townPortLastTarget = target;
                        AutonomousTownTeleporters.RememberStandOff(port.Porter, target);
                        walking = true;
                        break;
                    }
                if (!walking)
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
            // A horse chosen before the teleport belongs to the old side of the realm (Yarley's Farm, 2026-10-13: bots
            // landed and then gave up on Fluvon's horse at Gronyr's Farm, blocking horses for ten minutes): choose again.
            _pendingStableChoice = null;
            _nextStableCheckTick = 0;
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
                         $"destination=\"{_townPort.Destination.TeleportID}\" reason=\"{reason}\" " +
                         // Where it gave up (2026-10-14: 1,800 failures, most "no teleport within 5 minutes", with nothing
                         // to say whether the bot was far away, standing at the porter, or busy fighting on the way).
                         $"region={bot.CurrentRegionID} at={bot.X},{bot.Y},{bot.Z} toPorter={(bot.CurrentRegion == _townPort.Porter.CurrentRegion ? (int)bot.GetDistanceTo(_townPort.Porter) : -1)} " +
                         $"stillSeconds={(GameLoop.GameLoopTime - _townPortProgressTick) / 1000} " +
                         $"startDistance={(int)_townPortStartDistance} bestDistance={(_townPortBestDistance < float.MaxValue ? (int)_townPortBestDistance : -1)} " +
                         $"turns={_townPortTurns} seconds={(GameLoop.GameLoopTime - _townPortStartedTick) / 1000} plannedSeconds={_townPort.Seconds:0} " +
                         $"porterAt={_townPort.Porter.X},{_townPort.Porter.Y} lastTarget={(int)_townPortLastTarget.X},{(int)_townPortLastTarget.Y} " +
                         $"gathering={_townPortGatherSinceTick > 0} inCombat={bot.InCombat} activity=\"{bot.PersistentRecord?.Activity}\"");
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

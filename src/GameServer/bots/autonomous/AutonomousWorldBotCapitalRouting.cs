using System.Linq;
using System.Numerics;
using DOL.Database;

namespace DOL.GS
{
    public sealed partial class AutonomousWorldBotController
    {
        private bool TryPlanCapitalTransit(GameBot bot, Vector3 destination)
        {
            if (_capitalTransit != null) return false;
            ushort capitalId = AutonomousStuckWatchdog.CapitalFor(bot.Realm).RegionId;
            if (capitalId == 0 || capitalId == bot.CurrentRegionID) return false;
            DbZonePoint[] points = ZonePoints().Where(point =>
                (point.Realm == 0 || point.Realm == (ushort)bot.Realm) &&
                !IsZonePointQuarantined(bot, point) &&
                (point.SourceRegion == capitalId || point.TargetRegion == capitalId) &&
                IsRegionEdgeAccessible(bot.Realm, point.SourceRegion, point.TargetRegion) &&
                IsRegionPointAccessible(bot.Realm, point.SourceRegion, point.SourceX, point.SourceY) &&
                IsRegionPointAccessible(bot.Realm, point.TargetRegion, point.TargetX, point.TargetY)).ToArray();
            var plan = AutonomousCapitalTransit.Choose(bot.CurrentRegion, WorldMgr.GetRegion(capitalId),
                new(bot.X, bot.Y, bot.Z), destination, bot.CurrentRegionID, capitalId, points, PathfindingProvider.Instance);
            if (plan == null) return false;
            _capitalTransit = plan;
            _capitalTransitAssignment = bot.PersistentRecord?.ObjectiveAssignmentId;
            _capitalTransitGroup = _groupDirective?.GroupId;
            _capitalTransitPhase = _groupDirective?.Phase;
            if (Log.IsInfoEnabled)
                Log.Info($"AUTONOMOUS_CAPITAL_TRANSIT bot=\"{bot.Name}\" id={bot.DatabaseID} level={bot.Level} " +
                    $"realm={bot.Realm} entry={plan.Entry.Id} exit={plan.Exit.Id} goal=\"{bot.PersistentRecord?.CurrentGoal}\"");
            return true;
        }

        private bool HandleCapitalTransit(GameBot bot)
        {
            AutonomousCapitalTransit.Plan plan = _capitalTransit;
            if (plan == null) return false;
            if (_capitalTransitAssignment != bot.PersistentRecord?.ObjectiveAssignmentId ||
                _capitalTransitGroup != _groupDirective?.GroupId || _capitalTransitPhase != _groupDirective?.Phase)
            {
                ResetRouteOrderState();
                return false;
            }
            DbZonePoint leg = bot.CurrentRegionID == plan.Entry.SourceRegion ? plan.Entry :
                bot.CurrentRegionID == plan.Exit.SourceRegion ? plan.Exit : null;
            if (leg == null)
            {
                ResetRouteOrderState();
                return false;
            }
            if (TryRepairAuditedCrossingSource(bot, leg))
                return true;
            int distance = Distance(bot.X, bot.Y, leg.SourceX, leg.SourceY);
            if (!AtRegionCrossing(bot, leg, distance))
            {
                Vector3 raw = new(leg.SourceX, leg.SourceY, leg.SourceZ);
                if (!TryResolveConnectedApproach(bot, raw, ZonePointArrivalRadius, out Vector3 approach))
                {
                    QuarantineZonePoint(bot, leg);
                    AbandonCamp(bot, $"Capital gate {leg.Id} has no connected approach from the current surface");
                    return true;
                }
                if (!IssuePath(bot, approach)) return true;
                SetStatus(bot, "Traveling through capital gates", GoalText(),
                    $"Walking to real city gate {leg.Id}; {distance:N0} units remain", _camp?.MonsterName ?? string.Empty);
                return true;
            }
            if (!IsRegionEdgeAccessible(bot.Realm, leg.SourceRegion, leg.TargetRegion) ||
                !IsRegionPointAccessible(bot.Realm, leg.TargetRegion, leg.TargetX, leg.TargetY))
            {
                AbandonCamp(bot, "Capital transit gate became unavailable");
                return true;
            }
            bot.StopMovingOnPath();
            bot.StopMoving();
            if (!AutonomousZonePointArrival.TryResolve(leg, out Vector3 arrival) ||
                !bot.MoveTo(leg.TargetRegion, (int)arrival.X, (int)arrival.Y, (int)arrival.Z, leg.TargetHeading))
            {
                AbandonCamp(bot, "The server rejected a capital transit gate transfer");
                return true;
            }
            ResetRouteOrderState();
            if (leg == plan.Entry) _capitalTransit = plan;
            else if (Log.IsInfoEnabled)
                Log.Info($"AUTONOMOUS_CAPITAL_TRANSIT_ARRIVE bot=\"{bot.Name}\" id={bot.DatabaseID} level={bot.Level} " +
                    $"realm={bot.Realm} exit={leg.Id} region={bot.CurrentRegionID} position={bot.X},{bot.Y},{bot.Z}");
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Movement);
            return true;
        }

        /// <summary>
        /// Last-resort egress for an autonomous bot stranded on a disconnected
        /// interior component of its own capital. Ordinary navigation still gets
        /// both real gates first. This path is reached only after those connected
        /// approaches fail (or their per-bot quarantines hide every route), and it
        /// uses the unchanged authoritative DbZonePoint pairing rather than a
        /// synthetic destination or a guessed outdoor coordinate.
        /// </summary>
        private bool TryRecoverBlockedCapitalEgress(GameBot bot, CampDestination destination,
            DbZonePoint preferred = null)
        {
            if (bot == null || destination == null || bot.CurrentRegion?.IsCapitalCity != true)
                return false;

            AutonomousStuckWatchdog.CapitalLocation capital = AutonomousStuckWatchdog.CapitalFor(bot.Realm);
            if (capital.RegionId == 0 || bot.CurrentRegionID != capital.RegionId ||
                destination.RegionId == capital.RegionId)
                return false;

            DbZonePoint edge = preferred;
            if (!IsUsableCapitalRecoveryEdge(bot, edge))
            {
                // Deliberately ignore only this bot's temporary gate quarantine.
                // All realm, expansion, dungeon-entrance and region-access checks
                // remain in FindNextCrossing.
                edge = FindNextCrossing(bot.Realm, bot.CurrentRegionID, destination.RegionId,
                    destination.X, destination.Y);
            }
            if (!IsUsableCapitalRecoveryEdge(bot, edge))
                return false;

            ushort sourceRegion = bot.CurrentRegionID;
            int sourceX = bot.X;
            int sourceY = bot.Y;
            int sourceZ = bot.Z;
            bot.StopMovingOnPath();
            bot.StopMoving();
            if (!AutonomousZonePointArrival.TryResolve(edge, out Vector3 arrival) ||
                !bot.MoveTo(edge.TargetRegion, (int)arrival.X, (int)arrival.Y, (int)arrival.Z, edge.TargetHeading))
                return false;

            ResetRouteOrderState();
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Recovery);
            SetStatus(bot, $"Crossing toward {destination.ZoneName}", GoalText(),
                $"Recovered from a disconnected capital interior through real zone connection {edge.Id}",
                destination.MonsterName, destination.ZoneName, true);
            Log.Warn($"AUTONOMOUS_CAPITAL_EGRESS_RECOVERY bot=\"{bot.Name}\" id={bot.DatabaseID} " +
                     $"realm={bot.Realm} edge={edge.Id} from={sourceRegion}:{sourceX},{sourceY},{sourceZ} " +
                     $"to={edge.TargetRegion}:{edge.TargetX},{edge.TargetY},{edge.TargetZ} " +
                     $"goal=\"{bot.PersistentRecord?.CurrentGoal}\"");
            return true;
        }

        private static bool IsUsableCapitalRecoveryEdge(GameBot bot, DbZonePoint edge) =>
            edge != null && IsAuthoritativeZonePointEdge(edge) &&
            edge.SourceRegion == bot.CurrentRegionID && edge.TargetRegion != bot.CurrentRegionID &&
            (edge.Realm == 0 || edge.Realm == (ushort)bot.Realm) &&
            IsRegionEdgeAccessible(bot.Realm, edge.SourceRegion, edge.TargetRegion) &&
            IsRegionPointAccessible(bot.Realm, edge.TargetRegion, edge.TargetX, edge.TargetY);
    }
}

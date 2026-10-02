using System;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;

namespace DOL.GS;

public sealed partial class AutonomousWorldBotController
{
    private string _keepTravelKey;
    private long _keepTravelGeometry, _keepTravelRetry;
    private int _keepTravelFailures, _keepTravelIndex;
    private Vector3 _keepPlanningOrigin;
    private Vector3? _keepTravelLastPosition;
    private Vector3[] _keepTravelPoints;
    private RvrPlanningNavigation _keepPlanning;

    private static long KeepTravelGeometry(Region region, Zone destination, string targetId)
    {
        // A distant border door opening must not restart every defender's job.
        // Intermediate cached queries carry their own zone revision instead.
        long stamp = NavigationGeometryRevision.Read(destination);
        // Ownership can change permission to traverse a closed friendly gate.
        foreach (var keep in GameServer.KeepManager.GetKeepsOfRegion(region.ID))
        {
            stamp = unchecked(stamp * 31 + keep.KeepID * 7 + (int)keep.Realm);
            if ($"rvr-keep-{keep.KeepID}" != targetId) continue;
            foreach (var door in keep.Doors.Values)
                stamp = unchecked(stamp * 31 + (int)door.State * 7 + (door.IsAlive ? 1 : 0));
        }
        return stamp;
    }

    /// <returns>True while planning/travelling/waiting. False only at the proved
    /// endpoint, so existing combat and patrol decisions retain control there.</returns>
    private bool FollowKeepTravel(GameBot bot, CampDestination destination)
    {
        long now = GameLoop.GameLoopTime;
        Vector3 current = new(bot.X, bot.Y, bot.Z);
        string key = $"{bot.PersistentRecord?.ObjectiveAssignmentId}:{bot.CurrentRegionID}:{bot.Realm}:{destination.Id}:{destination.X}:{destination.Y}:{destination.Z}";
        long geometry = KeepTravelGeometry(bot.CurrentRegion, bot.CurrentRegion.GetZone(destination.X,destination.Y),destination.Id);
        if (_keepTravelKey != key || _keepTravelGeometry != geometry ||
            _keepTravelLastPosition.HasValue && Vector3.DistanceSquared(current, _keepTravelLastPosition.Value) > 8000 * 8000 ||
            _keepTravelPoints != null && _keepTravelIndex >= _keepTravelPoints.Length && Vector3.DistanceSquared(current, _keepTravelPoints[^1]) > 650 * 650 ||
            _keepPlanning != null && Vector3.DistanceSquared(current, _keepPlanningOrigin) > 96 * 96)
        {
            _keepTravelKey = key; _keepTravelGeometry = geometry;
            _keepTravelPoints = null; _keepPlanning = null; _keepTravelIndex = 0;
            _keepTravelRetry = 0; _keepTravelFailures = 0;
            _rvrApproachDestination = null;
            _rvrTravelWaypoint = null; _patrolDestination = null;
        }
        _keepTravelLastPosition = current;
        if (_keepTravelPoints == null)
        {
            if (now < _keepTravelRetry) return true;
            if (_keepPlanning == null)
            {
                // Cancel only the obsolete travel order. Otherwise a redirected
                // roamer moves the search origin every slice and never finishes.
                bot.StopMovingOnPath(); bot.StopMoving();
                _keepPlanningOrigin = current;
                _keepPlanning = new RvrPlanningNavigation(AutonomousKeepApproachNavigation.ForRealm(
                    PathfindingProvider.Instance, bot.CurrentRegion, bot.Realm));
            }
            _keepPlanning.BeginSlice();
            var planningWork = _keepPlanning;
            string failure = "No connected exterior route";
            try
            {
                if (TryResolveKeepTravelApproach(bot, destination, _keepPlanning, _keepPlanningOrigin, out var endpoint) &&
                    RvrKeepRoute.TryBuild(bot.CurrentRegion, _keepPlanning, bot.Realm, _keepPlanningOrigin, endpoint, out var steps))
                {
                    _keepTravelPoints = steps; _keepTravelIndex = 0;
                    _rvrApproachDestination = endpoint;
                    _keepPlanning = null; _keepTravelFailures = 0;
                }
            }
            catch (RvrPlanningNavigation.Yield)
            {
                _keepTravelRetry = now + 250;
                if (bot.Brain is BotBrain brain) brain.ThinkInterval = 250;
                SetRvrStatus(bot, "Planning keep route", destination.MonsterName,
                    "Continuing a bounded route search; combat remains responsive");
                return true;
            }
            catch (RvrPlanningNavigation.Limit) { failure = "Bounded route-work limit reached"; }
            finally
            {
                // Charge only work actually spent planning. Combat, casting,
                // transport and background AI cadence must not consume the
                // search's time allowance and discard a valid partial route.
                planningWork.EndSlice();
            }
            if (_keepTravelPoints == null)
            {
                int queries = _keepPlanning.Queries;
                _keepPlanning = null;
                _keepTravelFailures = Math.Min(4, _keepTravelFailures + 1);
                _keepTravelRetry = now + _keepTravelFailures * 30_000 + Math.Abs(bot.DatabaseID % 3000);
                bot.StopMovingOnPath(); bot.StopMoving();
                SetRvrStatus(bot, "Keep route retry", destination.MonsterName,
                    $"{failure}; retaining the siege and backing off before retrying");
                Log.Warn($"RVR_KEEP_ROUTE_FAILED bot=\"{bot.Name}\" id={bot.DatabaseID} realm={bot.Realm} " +
                    $"target=\"{destination.Id}\" region={bot.CurrentRegionID} from={current} queries={queries} " +
                    $"reason=\"{failure}\" retryMs={_keepTravelRetry-now}");
                return true;
            }
        }
        // Retain the existing attacker's optional real stable journey. The
        // horse still owns movement exclusively; replan from its real arrival.
        if (_keepTravelIndex == 0 && _rvrIntent != AutonomousRvrEventLayer.Intent.DefendEvent &&
            IsInFrontier(bot) && Vector2.Distance(new(current.X,current.Y),
                new(_keepTravelPoints[^1].X,_keepTravelPoints[^1].Y)) > 650 &&
            TryBeginFasterStableRoute(bot,_keepTravelPoints[^1],destination.ZoneName))
        {
            _keepTravelKey = null; _keepTravelPoints = null;
            return true;
        }
        while (_keepTravelIndex < _keepTravelPoints.Length &&
            Vector3.DistanceSquared(current, _keepTravelPoints[_keepTravelIndex]) <= 80 * 80)
            _keepTravelIndex++;
        if (_keepTravelIndex >= _keepTravelPoints.Length)
        {
            _rvrApproachDestination = _keepTravelPoints[^1];
            return false;
        }
        Vector3 next = _keepTravelPoints[_keepTravelIndex];
        var zone = bot.CurrentRegion.GetZone((int)next.X, (int)next.Y);
        if (zone != bot.CurrentZone && _keepTravelIndex > 0 &&
            Vector3.DistanceSquared(current, next) <= 256 * 256)
        {
            // Only the explicit inside/outside pair of an already-proved seam.
            // No teleport, wall shortcut, or fresh cross-region chord.
            if (!bot.IsMoving) bot.WalkTo(next, bot.MaxSpeed);
        }
        else if (!IssuePath(bot, next, preciseArrival: true))
        {
            _keepTravelPoints = null; _keepPlanning = null;
            _keepTravelRetry = now + 30_000;
            return true;
        }
        SetRvrStatus(bot, "Traveling to keep", destination.MonsterName,
            $"Following validated road segment {_keepTravelIndex+1}/{_keepTravelPoints.Length}");
        return true;
    }
}

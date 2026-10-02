using System;
using System.Numerics;
using DOL.AI.Brain;
using DOL.GS.Keeps;

namespace DOL.GS
{
    public sealed partial class AutonomousWorldBotController
    {
        private string _defensivePostKey;
        private long _defensivePostGeometry, _defensivePostRetry;
        private Vector3? _defensivePost;
        private Vector3 _defensivePostOrigin;
        private RvrPlanningNavigation _defensivePostPlanning;

        private bool HoldDefensiveKeepPost(GameBot bot, AbstractGameKeep keep)
        {
            if (keep.Realm != bot.Realm || bot.CurrentRegionID != keep.Region) return false;
            long now = GameLoop.GameLoopTime;
            Vector3 current = new(bot.X, bot.Y, bot.Z);
            string target = $"rvr-keep-{keep.KeepID}";
            string key = $"{bot.PersistentRecord?.ObjectiveAssignmentId}:{keep.Region}:{keep.KeepID}:{bot.Realm}";
            long geometry = KeepTravelGeometry(bot.CurrentRegion, bot.CurrentRegion.GetZone(keep.X, keep.Y), target);
            if (_defensivePostKey != key || _defensivePostGeometry != geometry ||
                _defensivePostPlanning != null && Vector3.DistanceSquared(current, _defensivePostOrigin) > 96 * 96)
            {
                _defensivePostKey = key; _defensivePostGeometry = geometry;
                _defensivePost = null; _defensivePostPlanning = null; _defensivePostRetry = 0;
            }
            if (!_defensivePost.HasValue)
            {
                if (now < _defensivePostRetry) return true;
                if (_defensivePostPlanning == null)
                {
                    bot.StopMovingOnPath(); bot.StopMoving();
                    _defensivePostOrigin = current;
                    _defensivePostPlanning = new RvrPlanningNavigation(AutonomousKeepApproachNavigation.ForRealm(
                        PathfindingProvider.Instance, bot.CurrentRegion, bot.Realm));
                }
                var work = _defensivePostPlanning;
                work.BeginSlice();
                try
                {
                    // Dedicated interior state: an exterior arrival/fallback can
                    // never be reused as a successful defensive post. Native
                    // guard floors supply posts, including relic commanders.
                    if (AutonomousRvrRally.TryDefenderPost(bot, keep,
                        (int)(unchecked((ulong)bot.DatabaseID) % 240), work, _defensivePostOrigin, out var post))
                        _defensivePost = post;
                }
                catch (RvrPlanningNavigation.Yield)
                {
                    _defensivePostRetry = now + 250;
                    if (bot.Brain is BotBrain brain) brain.ThinkInterval = 250;
                    SetRvrStatus(bot, "Planning keep interior", keep.Name, "Bounded route check; nearby attackers take priority");
                    return true;
                }
                catch (RvrPlanningNavigation.Limit) { }
                finally { work.EndSlice(); }
                _defensivePostPlanning = null;
                if (!_defensivePost.HasValue)
                {
                    _defensivePostRetry = now + 30_000 + Math.Abs(bot.DatabaseID % 3000);
                    SetRvrStatus(bot, "Defense interior route retry", keep.Name,
                        "No verified interior post yet; still scanning for attackers outside");
                    Log.Warn($"RVR_DEFENSE_POST_RETRY bot=\"{bot.Name}\" keep={keep.KeepID} from={current} queries={work.Queries}");
                    return true;
                }
            }
            if (Vector3.DistanceSquared(current, _defensivePost.Value) <= 80 * 80)
            {
                bot.StopMovingOnPath(); bot.StopMoving();
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Objective);
                SetRvrStatus(bot, "Defending keep", keep.Name, "At a verified interior post; engaging visible enemy combatants before siege engines");
                return true;
            }
            // The complete friendly-door corridor was proved above. The native
            // path follower operates each owned door along the actual path.
            if (!IssuePath(bot, _defensivePost.Value, preciseArrival: true))
            {
                _defensivePost = null;
                _defensivePostRetry = now + 30_000;
                bot.StopMovingOnPath(); bot.StopMoving();
                SetRvrStatus(bot, "Defense interior route retry", keep.Name, "Movement failed; retrying without leaving the defense event");
            }
            else SetRvrStatus(bot, "Entering friendly keep", keep.Name, "Using a verified interior route; visible attackers interrupt travel");
            return true;
        }
    }
}

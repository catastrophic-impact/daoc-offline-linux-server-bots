using System;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;
using DOL.GS.Keeps;

namespace DOL.GS;

public sealed partial class AutonomousWorldBotController
{
    private string _physicalRallyKey;
    private Vector3? _physicalRallyPost;
    private long _nextRallyProjection;

    private bool HandleSiegeRally(GameBot bot, string forceId, AutonomousRvrEventLayer.RallyOrder order)
    {
        var keep = GameServer.KeepManager.GetKeepsOfRegion(_rvrDestination.RegionId)
            .FirstOrDefault(candidate => $"rvr-keep-{candidate.KeepID}" == order.TargetId);
        if (keep == null) return true;
        // A distant responder must use the same bounded, retained keep route.
        // Do not run a fresh full-country reachability test for every rally slot.
        if (bot.CurrentRegionID == keep.Region &&
            Vector2.DistanceSquared(new(bot.X,bot.Y),new(keep.X,keep.Y)) > 11_500 * 11_500)
        {
            FollowKeepTravel(bot, _rvrDestination);
            return true;
        }
        bool operatorBot = _groupDirective?.IsDynamic != true || _groupDirective.Leader == bot;
        // Equipment procurement belongs to the bounded per-keep job pool.
        // Do not send every arriving leader (and their followers) shopping.
        bot.TempProperties.RemoveProperty("RvrSupplying");
        if (operatorBot && bot.CurrentRegionID != keep.Region && TryFrontierTransport(bot,_rvrDestination)) return true;
        GameBot leader = _groupDirective?.Leader;
        if (leader != null && leader != bot && leader.IsAlive)
        {
            if (bot.CurrentRegionID != keep.Region && TryFrontierTransport(bot,_rvrDestination)) return true;
            if (leader.CurrentRegionID != keep.Region ||
                Vector2.Distance(new(leader.X,leader.Y),new(keep.X,keep.Y)) > AutonomousRvrRally.CampRadius+2500)
                return FollowDynamicGroupLeader(bot,_groupDirective);
        }
        if (operatorBot && _groupDirective?.IsDynamic == true &&
            (bot.CurrentRegionID != keep.Region || Vector2.Distance(new(bot.X,bot.Y),new(keep.X,keep.Y)) > AutonomousRvrRally.CampRadius+2500) &&
            !AutonomousBotGroupCoordinator.IsCohesive(_groupDirective))
        {
            bot.StopMovingOnPath(); bot.StopMoving();
            SetRvrStatus(bot,"Regrouping en route to rally",keep.Name,"Waiting for nearby warband members before continuing together");
            return true;
        }
        var members = bot.Group?.GetMembersInTheGroup().OfType<GameBot>().OrderBy(member => member.DatabaseID).ToArray() ?? [bot];
        int memberIndex = Array.IndexOf(members, bot);
        if (memberIndex < 0 || memberIndex >= order.Slots.Length) return true;
        string key = $"{order.TargetId}:{forceId}:{order.Slots[memberIndex]}";
        if (_physicalRallyKey != key)
        {
            _physicalRallyKey = key;
            _physicalRallyPost = null;
            _nextRallyProjection = 0;
        }
        long now = GameLoop.GameLoopTime;
        if (!_physicalRallyPost.HasValue && now >= _nextRallyProjection)
        {
            _nextRallyProjection = now + 15_000;
            if (AutonomousRvrRally.TryPost(bot, keep, order, memberIndex, out Vector3 point))
                _physicalRallyPost = point;
        }
        if (!_physicalRallyPost.HasValue)
        {
            bot.StopMovingOnPath();
            bot.StopMoving();
            SetRvrStatus(bot, "Rally route unavailable", keep.Name,
                "No validated defensive rally post is reachable; this bot is not counted as present");
            return true;
        }
        Vector3 post = _physicalRallyPost.Value;
        bool atPost = bot.CurrentRegionID == keep.Region &&
            Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), post) <= AutonomousRvrRally.ArrivalRadius * AutonomousRvrRally.ArrivalRadius;
        // Different regions have unrelated coordinates; use a large sentinel
        // until the actual approach starts instead of rewarding a wrong map.
        AutonomousRvrEventLayer.ReportTravel(order.TargetId, forceId, bot.DatabaseID,
            bot.CurrentRegionID == keep.Region ? Vector3.Distance(new(bot.X, bot.Y, bot.Z), post) : double.PositiveInfinity,
            atPost, now, bot);
        if (atPost)
        {
            bot.StopMovingOnPath();
            bot.StopMoving();
        }
        AutonomousRvrEventLayer.ReportAttendance(order.TargetId, forceId, bot.Realm, bot.DatabaseID,
            atPost && bot.IsAlive && !bot.InCombat && (bot.Brain as BotBrain)?.HasAggro != true, now, bot, post);
        if (atPost)
        {
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Objective);
            SetRvrStatus(bot, "Holding siege rally", keep.Name,
                $"{(bot.Realm == order.Defender ? "Holding an interior defensive post" : "Holding the realm's separated defensive camp")}; " +
                $"attackers advance when their recruited force is ready after at least three minutes; no enemy attendance requirement; " +
                $"preparation deadline in {Math.Max(0, (int)Math.Ceiling(TimeSpan.FromMilliseconds(order.RemainingMilliseconds).TotalMinutes))}m " +
                $"({(keep.IsRelic ? 192 : 128)} cap per realm; actual fighting can start the battle earlier)");
            return true;
        }

        var destination = _rvrDestination with { X = (int)post.X, Y = (int)post.Y, Z = (int)post.Z };
        if (bot.CurrentRegionID != keep.Region)
            TravelRvrObjective(bot, destination);
        else if (bot.Realm == order.Defender)
            TravelToDefensivePost(bot, keep, post);
        else
        {
            // Keep the outbound warband around its leader until close to the
            // siege camp, then let each member settle into its reserved post.
            if (leader != null && leader != bot && leader.IsAlive && leader.CurrentRegionID == bot.CurrentRegionID &&
                Vector3.DistanceSquared(new(leader.X, leader.Y, leader.Z), post) > 2200 * 2200)
                FollowDynamicGroupLeader(bot, _groupDirective);
            else IssueVariedRvrPath(bot, post);
        }
        SetRvrStatus(bot, "Traveling to siege rally", keep.Name,
            bot.Realm == order.Defender ? "Entering the friendly keep to take a defensive post" :
                "Joining this realm's separate rally camp outside the keep");
        return true;
    }

    private bool TravelToDefensivePost(GameBot bot, AbstractGameKeep keep, Vector3 post)
    {
        var nav = PathfindingProvider.Instance;
        // Do not route across a whole frontier to an interior wall component.
        // Reach the connected exterior first, then use the native friendly door.
        if (Vector2.DistanceSquared(new(bot.X, bot.Y), new(keep.X, keep.Y)) > 3500 * 3500)
        {
            FollowKeepTravel(bot, _rvrDestination with { X = keep.X, Y = keep.Y, Z = keep.Z });
            return true;
        }
        Zone zone = bot.CurrentRegion.GetZone((int)post.X, (int)post.Y);
        if (bot.CurrentZone == zone && AutonomousZoneItinerary.HasCompleteCorridor(nav, zone,
                new(bot.X, bot.Y, bot.Z), post))
            return IssuePath(bot, post);
        // Closed friendly doors split mesh components. Walk to the actual
        // doorway and use the native door operation, never a wall shortcut.
        foreach (var door in keep.Doors.Values.OrderBy(bot.GetDistanceTo))
        {
            if (door.Realm != bot.Realm || Math.Abs(door.Z - bot.Z) > 500) continue;
            if (bot.IsWithinRadius(door, WorldMgr.INTERACT_DISTANCE))
            {
                if (AutonomousRvrTravel.TraverseFriendlyDoor(bot, post)) return true;
                continue;
            }
            if (door.CurrentZone == bot.CurrentZone && AutonomousZoneItinerary.HasCompleteCorridor(nav, bot.CurrentZone,
                    new(bot.X, bot.Y, bot.Z), new(door.X, door.Y, door.Z)))
                return IssuePath(bot, new(door.X, door.Y, door.Z));
        }
        // A local floor is not proof of a usable interior route. Hold outside
        // and retry instead of issuing a known-disconnected wall/rampart goal.
        _rvrApproachDestination=null;
        bot.StopMovingOnPath();bot.StopMoving();
        SetRvrStatus(bot,"Defending outside keep",keep.Name,"No usable interior corridor yet; watching for enemies outside the gate");
        return true;
    }
}

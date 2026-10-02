using System.Numerics;

namespace DOL.GS;

public sealed partial class AutonomousWorldBotController
{
    private long _expeditionRouteRetry;
    private Vector3[] _expeditionSeams;
    private int _expeditionLeg;
    private string _expeditionEvent;
    private ushort _expeditionRegion;
    private Vector3 _expeditionGoal;
    private Vector3? _expeditionLastPosition;
    private RvrPlanningNavigation _dragonRallyPlanning;
    private Vector3[] _dragonRallyRoute;
    private Vector3 _dragonRallyOrigin, _dragonRallyGoal;
    private int _dragonRallyLeg;
    private string _dragonRallyEvent;
    private long _dragonRallyRetry;
    private bool TravelRealmExpedition(GameBot bot, AutonomousRealmRaid.View order)
    {
        if (GameLoop.GameLoopTime < _expeditionRouteRetry) return true;
        var c = order.Camp;
        if (_camp == null || _camp.RegionId != c.RegionId || _camp.X != c.X || _camp.Y != c.Y || _camp.Z != c.Z)
            _camp = FromSharedCamp(c);
        if (AutonomousRealmRaid.TryPendingDragonRally(bot,out var home) && bot.CurrentRegionID == c.RegionId &&
            Vector2.DistanceSquared(new(bot.X,bot.Y),new(home.X,home.Y)) <= DragonRallyRoute.ApproachRadius*DragonRallyRoute.ApproachRadius &&
            Vector2.DistanceSquared(new(c.X,c.Y),new(home.X,home.Y)) <= DragonRallyRoute.ApproachRadius*DragonRallyRoute.ApproachRadius)
            return TravelDragonRally(bot,order,home);
        _dragonRallyPlanning=null; _dragonRallyRoute=null; _dragonRallyEvent=null;
        if (AutonomousRealmRaid.TryIndependentApproach(bot, out var exterior, out var via))
        {
            Vector3 position = new(bot.X, bot.Y, bot.Z);
            if (_expeditionEvent != order.EventId || _expeditionRegion != bot.CurrentRegionID ||
                Vector3.DistanceSquared(exterior, _expeditionGoal) > 512 * 512 ||
                _expeditionLastPosition.HasValue && Vector3.DistanceSquared(position, _expeditionLastPosition.Value) > 2048 * 2048)
            {
                _expeditionSeams = null;
                _expeditionEvent = order.EventId; _expeditionRegion = bot.CurrentRegionID; _expeditionGoal = exterior;
            }
            _expeditionLastPosition = position;
            if (_expeditionSeams == null)
            {
                if (!RealmRaidMuster.TryRoute(bot.CurrentRegion, PathfindingProvider.Instance, bot.Realm,
                    new(bot.X, bot.Y, bot.Z), exterior, out _expeditionSeams, via))
                {
                    _expeditionSeams = null;
                    _expeditionRouteRetry = GameLoop.GameLoopTime + 15_000;
                    bot.StopMovingOnPath(); bot.StopMoving();
                    SetStatus(bot, "Expedition route retry", c.MonsterName, "No connected exterior corridor; retaining assignment and retrying safely");
                    return true;
                }
                _expeditionLeg = 0;
            }
            while (_expeditionLeg < _expeditionSeams.Length &&
                Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), _expeditionSeams[_expeditionLeg]) <= 64 * 64)
                _expeditionLeg++;
            if (_expeditionLeg < _expeditionSeams.Length)
            {
                if (!IssuePath(bot, _expeditionSeams[_expeditionLeg], preciseArrival: true))
                { _expeditionSeams = null; _expeditionRouteRetry = GameLoop.GameLoopTime + 15_000; }
                SetStatus(bot, "Traveling to expedition", c.MonsterName, "Following the validated exterior corridor; no town attendance required");
                return true;
            }
        }
        // Released/late members own their trip to the encounter. No follower
        // gate, all-eight meetup or dead leader can hold them in another region.
        if (bot.CurrentRegionID != c.RegionId) return TravelAcrossRegions(bot);
        Vector3 post = new(c.X,c.Y,c.Z);
        int arrival = order.Crossing ? 32 : 175;
        if (Vector3.DistanceSquared(new(bot.X,bot.Y,bot.Z),post) > arrival * arrival)
        {
            if (!TryBeginFasterStableRoute(bot,post,c.ZoneName))
                IssuePath(bot,post,preciseArrival:order.Crossing);
            SetStatus(bot,order.State,c.MonsterName,"Traveling to the expedition; late members rejoin without town staging");
            return true;
        }
        if (!order.Hold) return false;
        bot.StopMovingOnPath();
        bot.StopMoving();
        SetStatus(bot,order.State,c.MonsterName,"Present at expedition staging; waiting for the expedition order");
        return true;
    }

    private bool TravelDragonRally(GameBot bot, AutonomousRealmRaid.View order, Vector3 home)
    {
        long now=GameLoop.GameLoopTime;
        Vector3 current=new(bot.X,bot.Y,bot.Z), goal=new(order.Camp.X,order.Camp.Y,order.Camp.Z);
        if (_dragonRallyEvent!=order.EventId || _dragonRallyGoal!=goal ||
            _dragonRallyPlanning!=null && Vector3.DistanceSquared(current,_dragonRallyOrigin)>96*96)
        {
            _dragonRallyEvent=order.EventId; _dragonRallyGoal=goal;
            _dragonRallyPlanning=null; _dragonRallyRoute=null; _dragonRallyRetry=0;
        }
        if(now<_dragonRallyRetry) return true;
        if(_dragonRallyRoute==null)
        {
            if(_dragonRallyPlanning==null)
            {
                bot.StopMovingOnPath();bot.StopMoving();
                _dragonRallyOrigin=current;
                _dragonRallyPlanning=new RvrPlanningNavigation(PathfindingProvider.Instance);
            }
            var work=_dragonRallyPlanning; work.BeginSlice();
            bool found=false;
            try
            {
                found=DragonRallyRoute.TryBuild(work,bot.CurrentZone,_dragonRallyOrigin,goal,home,out var route);
                if(found) _dragonRallyRoute=route;
            }
            catch(RvrPlanningNavigation.Yield)
            { _dragonRallyRetry=now+250; SetStatus(bot,"Planning safe dragon rally route",order.Camp.MonsterName,"Routing around the dragon; normal defense remains active"); return true; }
            catch(RvrPlanningNavigation.Limit) { }
            finally { work.EndSlice(); }
            _dragonRallyPlanning=null;
            if(!found)
            {
                _dragonRallyRoute=null;_dragonRallyRetry=now+30_000;
                SetStatus(bot,"Dragon rally route retry",order.Camp.MonsterName,"No verified approach outside dragon aggro; not charging through the boss");
                Log.Warn($"DRAGON_RALLY_ROUTE_RETRY bot={bot.Name} event={order.EventId} from={current} goal={goal} queries={work.Queries}");
                return true;
            }
            _dragonRallyLeg=0;
        }
        while(_dragonRallyLeg<_dragonRallyRoute.Length && Vector3.DistanceSquared(current,_dragonRallyRoute[_dragonRallyLeg])<=80*80)
            _dragonRallyLeg++;
        if(_dragonRallyLeg==_dragonRallyRoute.Length)
        {
            bot.StopMovingOnPath();bot.StopMoving();
            SetStatus(bot,order.State,order.Camp.MonsterName,"At safe dragon staging; waiting for the expedition order");
            return true;
        }
        if(!IssuePath(bot,_dragonRallyRoute[_dragonRallyLeg],preciseArrival:true))
        { _dragonRallyRoute=null;_dragonRallyRetry=now+15_000;bot.StopMovingOnPath();bot.StopMoving(); }
        else SetStatus(bot,"Traveling to dragon rally",order.Camp.MonsterName,"Following a checked route outside the dragon's aggro area");
        return true;
    }
}

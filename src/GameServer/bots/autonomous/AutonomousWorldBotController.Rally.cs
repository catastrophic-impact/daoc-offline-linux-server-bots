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

    private string _musterKey;
    private Vector3? _musterPost;

    /// <summary>
    /// Staged assault, first stage: an attacker gathers at a reserved post around its realm's portal
    /// outpost in the target frontier (else its border keep) until the army's march order. Warbands used to walk
    /// into the enemy frontier one at a time and were picked off before the siege camp filled.
    /// </summary>
    private bool HandleSiegeMuster(GameBot bot, string forceId, AutonomousRvrEventLayer.RallyOrder order)
    {
        AutonomousRvrStaging.BorderKeep home;
        if (bot.Realm == order.Attacker ? !AutonomousRvrStaging.TryGetSiegeMuster(bot.Realm, _rvrDestination.RegionId, out home)
                : !TryGetDefenseMuster(bot, order.TargetId, out home)) return false;
        var members = bot.Group?.GetMembersInTheGroup().OfType<GameBot>().OrderBy(member => member.DatabaseID).ToArray() ?? [bot];
        int memberIndex = Array.IndexOf(members, bot);
        if (memberIndex < 0 || memberIndex >= order.Slots.Length) return true;
        int slot = order.Slots[memberIndex];
        string key = $"{order.TargetId}:{forceId}:{slot}";
        if (_musterKey != key) { _musterKey = key; _musterPost = null; }
        Region region = WorldMgr.GetRegion(home.RegionId);
        if (!_musterPost.HasValue && region != null)
        {
            // A sunflower spread around the keep: every slot its own spot, 120 units per ring.
            double angle = slot * 2.39996323;
            float radius = 450 + 120 * MathF.Sqrt(slot);
            var raw = home.Position + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
            Zone zone = region.GetZone((int)raw.X, (int)raw.Y);
            var nav = PathfindingProvider.Instance;
            Vector3? floor = zone != null && nav.IsAvailable && nav.HasNavmesh(zone)
                ? nav.GetClosestPoint(zone, raw, 160, 160, 800, nav.DefaultFilters) : raw;
            _musterPost = floor ?? home.Position;
        }
        Vector3 post = _musterPost ?? home.Position;
        long now = GameLoop.GameLoopTime;
        bool atPost = bot.CurrentRegionID == home.RegionId &&
            Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), post) <= 160 * 160;
        AutonomousRvrEventLayer.ReportMusterAttendance(order.TargetId, forceId, bot.DatabaseID,
            atPost && bot.IsAlive && !bot.InCombat && (bot.Brain as BotBrain)?.HasAggro != true, now, bot, post);
        if (atPost)
        {
            bot.StopMovingOnPath();
            bot.StopMoving();
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Objective);
            SetRvrStatus(bot, $"Mustering at {home.Name}", order.TargetId,
                "Gathering with the realm's army; it marches on the keep together once enough have assembled");
            return true;
        }
        // A plain point, not the target keep's id: with "rvr-keep-N" the walk took the keep-assault
        // approach planner (gates of a keep in another region) and nobody reached the muster (run 10).
        TravelRvrObjective(bot, _rvrDestination with { Id = $"rvr-muster-{home.RegionId}", RegionId = home.RegionId,
            X = (int)post.X, Y = (int)post.Y, Z = (int)post.Z });
        // Abroad, the frontier transport's own status (medallion, boarding) stays visible.
        if (bot.CurrentRegionID == home.RegionId)
            SetRvrStatus(bot, $"Joining the muster at {home.Name}", order.TargetId,
                "Walking to the army's muster at the border keep before the march on the siege target");
        return true;
    }

    /// <summary>
    /// The defense rally gathers at the friendly keep or tower nearest the attacked keep (at least 3,000 units
    /// away, same region), else the realm's border keep, then marches in together.
    /// </summary>
    private bool TryGetDefenseMuster(GameBot bot, string targetId, out AutonomousRvrStaging.BorderKeep muster)
    {
        muster = default;
        var keeps = GameServer.KeepManager.GetKeepsOfRegion(_rvrDestination.RegionId);
        var target = keeps.FirstOrDefault(keep => $"rvr-keep-{keep.KeepID}" == targetId);
        if (target == null) return false;
        var friendly = keeps.Where(keep => keep != target && keep.Realm == bot.Realm &&
                Vector2.DistanceSquared(new(keep.X, keep.Y), new(target.X, target.Y)) >= 3_000f * 3_000f)
            .OrderBy(keep => Vector2.DistanceSquared(new(keep.X, keep.Y), new(target.X, target.Y))).FirstOrDefault();
        if (friendly != null)
        {
            muster = new(friendly.Region, new(friendly.X, friendly.Y, friendly.Z), friendly.Name);
            return true;
        }
        return AutonomousRvrStaging.TryGetBorderKeepNear(bot.Realm, target.Region, new(target.X, target.Y), out muster);
    }

    private bool HandleSiegeRally(GameBot bot, string forceId, AutonomousRvrEventLayer.RallyOrder order)
    {
        // The muster is the realm's portal outpost inside the target's frontier, so roamers already
        // there gather with the army too (see AutonomousRvrStaging.TryGetSiegeMuster).
        // Attackers muster at their portal outpost; defenders gather as one quick rally at a nearby friendly keep.
        if (order.HomeMuster && HandleSiegeMuster(bot, forceId, order)) return true;
        var keep = GameServer.KeepManager.GetKeepsOfRegion(_rvrDestination.RegionId)
            .FirstOrDefault(candidate => $"rvr-keep-{candidate.KeepID}" == order.TargetId);
        if (keep == null) return true;
        if (bot.Realm == order.Attacker && TryMarchWithArmy(bot, keep, order.TargetId)) return true;
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
            // Run 19: 27 of 64 Hibernian attackers stood still at Odin's Gate, beside the marching column on the same
            // navmesh, because the full corridor check to a post 10-11k away failed. Keep closing in on the keep's
            // retained route (as distant responders do) and retry the post every 15 s; never stand idle.
            FollowKeepTravel(bot, _rvrDestination);
            SetRvrStatus(bot, "Closing on the rally", keep.Name,
                "No validated rally post yet; walking the keep route and retrying (not counted as present)");
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

    private long _armyRegroupUntil;
    private long _nextArmyRegroup;
    public const int ArmyAssistRadius = 2500;
    public const int ArmyCombatHoldRadius = 8000;
    private long _armyCombatHoldUntil;
    private long _nextArmyCombatHold;
    public const int ArmyKeepUpDistance = 900;

    /// <summary>
    /// The march from the muster to the siege camp as one column: the leader walks the keep route and holds briefly
    /// when most of the army has fallen behind; everyone else keeps within reach of the leader; a marcher who is not
    /// fighting joins the fight of any army member under attack nearby. Run 17: Midgard's 64 on Caer Sursbrooke and
    /// Albion's 64 on Blendrake Faste each walked 50,000+ units one by one and lost half their strength to roaming
    /// enemies before either reached its camp. Only for the long march; inside the camp radius each bot takes its post.
    /// </summary>
    private bool TryMarchWithArmy(GameBot bot, AbstractGameKeep keep, string targetId)
    {
        if (bot.CurrentRegionID != keep.Region) return false;
        Vector2 here = new(bot.X, bot.Y);
        if (Vector2.Distance(here, new(keep.X, keep.Y)) <= AutonomousRvrRally.CampRadius + 2500)
        {
            // Run 19: the march ended here and handed over to rally posts the bots could not validate, so the army
            // stood still 11k from Bledmeer. The army's arrival opens the battle (owner: start once the threshold is
            // met, no waiting at the keep); the bots then go straight to the assault and their siege jobs.
            if (AutonomousRvrEventLayer.MarchLeader(targetId) == bot)
                AutonomousRvrEventLayer.ReportArmyArrived(targetId, GameLoop.GameLoopTime);
            return false;
        }
        GameBot leader = AutonomousRvrEventLayer.MarchLeader(targetId);
        if (leader == null || leader.CurrentRegionID != bot.CurrentRegionID) return false;
        GameBot[] army = AutonomousRvrEventLayer.ArmyMembers(targetId);
        long now = GameLoop.GameLoopTime;

        // Help a member under attack nearby before anything else.
        GameBot fighting = army.FirstOrDefault(member => member != bot && member.CurrentRegionID == bot.CurrentRegionID &&
            (member.InCombat || member.IsAttacking) && member.TargetObject is GameLiving foe && foe.IsAlive &&
            foe.Realm != bot.Realm && bot.IsWithinRadius(member, ArmyAssistRadius));
        if (fighting?.TargetObject is GameLiving enemy && bot.Brain is BotBrain brain &&
            GameServer.ServerRules.IsAllowedToAttack(bot, enemy, true))
        {
            bot.TargetObject = enemy;
            brain.AddToAggroList(enemy, Math.Max(100, enemy.EffectiveLevel * 12));
            brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
            SetRvrStatus(bot, "Defending the army column", keep.Name, $"Helping {fighting.Name} against {enemy.Name}");
            return true;
        }

        if (leader == bot)
        {
            int near = army.Count(member => member.CurrentRegionID == bot.CurrentRegionID && bot.IsWithinRadius(member, ArmyAssistRadius));
            int marching = army.Count(member => member.CurrentRegionID == bot.CurrentRegionID); // the fallen who released home don't hold the column
            if (now < _armyRegroupUntil && near < marching * 6 / 10)
            {
                bot.StopMovingOnPath(); bot.StopMoving();
                SetRvrStatus(bot, "Army regrouping", keep.Name, $"{near} of {marching} with the leader; waiting for the column");
                return true;
            }
            // Run 18: 13 of 50 marchers fought a hunter's avatar 10k behind while the leader walked on, and
            // the column stretched to 10k before they caught up. The leader holds while a quarter of the
            // column is fighting nearby (60 seconds at most, then two minutes before it holds again).
            int fightingBehind = army.Count(member => member.CurrentRegionID == bot.CurrentRegionID &&
                (member.InCombat || member.IsAttacking) && bot.IsWithinRadius(member, ArmyCombatHoldRadius));
            if (marching >= 8 && fightingBehind >= Math.Max(3, marching / 4) &&
                (now < _armyCombatHoldUntil || now >= _nextArmyCombatHold))
            {
                if (now >= _armyCombatHoldUntil)
                {
                    _armyCombatHoldUntil = now + 60_000;
                    _nextArmyCombatHold = now + 180_000;
                }
                bot.StopMovingOnPath(); bot.StopMoving();
                SetRvrStatus(bot, "Army holding for the column", keep.Name, $"{fightingBehind} of {marching} fighting behind the leader");
                return true;
            }
            if (now >= _nextArmyRegroup && marching >= 8 && near < marching / 2)
            {
                // At most 45 seconds of waiting, then two minutes of marching before the next wait.
                _armyRegroupUntil = now + 45_000;
                _nextArmyRegroup = now + 165_000;
                bot.StopMovingOnPath(); bot.StopMoving();
                SetRvrStatus(bot, "Army regrouping", keep.Name, $"{near} of {marching} with the leader; waiting for the column");
                return true;
            }
            FollowKeepTravel(bot, _rvrDestination);
            SetRvrStatus(bot, "Leading the army", keep.Name, $"{near} of {marching} marching with the leader");
            return true;
        }

        if (!bot.IsWithinRadius(leader, ArmyKeepUpDistance))
        {
            IssuePath(bot, new Vector3(leader.X, leader.Y, leader.Z));
            SetRvrStatus(bot, "Marching with the army", keep.Name, $"Catching up with {leader.Name}");
            return true;
        }
        if (!leader.IsMoving)
        {
            // The leader is holding (regroup or a fight behind); walking on past it splits the column.
            bot.StopMovingOnPath(); bot.StopMoving();
            SetRvrStatus(bot, "Waiting with the army", keep.Name, $"Holding with {leader.Name}");
            return true;
        }
        FollowKeepTravel(bot, _rvrDestination);
        SetRvrStatus(bot, "Marching with the army", keep.Name, $"In the column with {leader.Name}");
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

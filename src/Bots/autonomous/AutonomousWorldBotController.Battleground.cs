using System;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.Keeps;

namespace DOL.GS
{
    /// <summary>
    /// Battleground tours (the fourth bot goal). A bot only ever goes to the battleground of its own level
    /// bracket, by the realm teleporters' [Battlegrounds] choice exactly as a player would, arriving at its
    /// realm's portal keep. Inside, enemies are found by the normal frontier threat scan; between fights the
    /// bot assaults the central keep when another realm holds it (guards, gates, then the lord through the
    /// normal capture pipeline) and otherwise defends or roams around it. Leaving uses the same exit players
    /// take, back to the realm's border keep.
    /// </summary>
    public sealed partial class AutonomousWorldBotController
    {
        // Teleporter trips into a battleground have no walking alternative.
        private const double BattlegroundNoWalkSeconds = 3600;
        private const int BattlegroundExitRadius = 400;
        private long _nextBattlegroundRoamTick;
        private Vector3? _battlegroundRoamPoint;

        private bool ExecuteBattleground(BotBrain brain, GameBot bot)
        {
            BattlegroundBrackets.Bracket bracket = BattlegroundBrackets.ForLevel(bot.Level);
            if (bracket == null)
            {
                // Out-levelled: the tour ends here; leaving the battleground happens on the next goal's travel.
                AutonomousObjectiveAssignments.EndBattlegroundTour(bot, $"Outgrew the battleground brackets at level {bot.Level}");
                _nextPlanTick = 0;
                return true;
            }
            if (bot.CurrentRegionID != bracket.RegionId)
            {
                GameLocation entry = BattlegroundBrackets.BotArrival(bracket, bot.Realm);
                return TravelToBattleground(bot, bracket, new(entry.X, entry.Y, entry.Z));
            }

            AbstractGameKeep central = GameServer.KeepManager.GetKeepsOfRegion(bracket.RegionId)
                .FirstOrDefault(keep => !keep.IsPortalKeep); // starts neutral (realm none); taken from any realm
            bool assault = central != null && central.Realm != bot.Realm;
            if (central != null)
            {
                // Rebuilt only when the target changes (no per-turn allocation).
                if (_rvrDestination?.RegionId != bracket.RegionId || _rvrDestination.Id != $"rvr-keep-{central.KeepID}")
                {
                    _rvrApproachDestination = null;
                    _patrolDestination = null;
                    _rvrDestination = new($"rvr-keep-{central.KeepID}", central.Name, bracket.Name, bracket.RegionId,
                        central.X, central.Y, central.Z, 1, false, true);
                }
            }
            else
            {
                _rvrDestination = null;
            }
            _rvrSharedEvent = false;
            // Too few of the realm here for a wave: fight in the field instead of standing at the staging point or
            // feeding single bots to the keep's guards (Thidranki, 2026-10-17).
            bool tooFew = assault && AutonomousBattlegroundWave.TooFewForAssault(bot.Realm, bracket);
            _rvrIntent = assault && !tooFew ? AutonomousRvrEventLayer.Intent.AssaultKeep : AutonomousRvrEventLayer.Intent.Roam;
            if (tooFew)
                return RoamBattleground(bot, bracket, central);

            if (assault)
            {
                if (TryRunSiegeJob(bot)) return true;
                // The realm assaults as one wave (AutonomousBattlegroundWave); a bot in a fight finishes it first.
                if (!bot.InCombat && central != null &&
                    AutonomousBattlegroundWave.ShouldGather(bot, bracket, central, out Vector3 staging, out string gathered))
                {
                    if (Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), staging) > 250 * 250)
                        IssuePath(bot, staging);
                    else
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                    }
                    SetBattlegroundStatus(bot, bracket, "Gathering for the assault", $"{central.Name}: {gathered}");
                    return true;
                }
                GameLiving target = FindRvrTarget(bot) ?? FindReachableEnemyDoor(bot, central);
                if (target != null)
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                    bot.TargetObject = target;
                    brain.AddToAggroList(target, Math.Max(100, target.EffectiveLevel * 12));
                    brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    SetBattlegroundStatus(bot, bracket, target is GameKeepDoor ? "Battering the keep gate" : "Assaulting the keep",
                        $"{central.Name} is held by {Holder(central)}", target.Name);
                    return false;
                }
                // At the keep the assault goes for the lord. Run 17 (2026-10-17): in all four battlegrounds the gates fell and
                // guards died for four hours while every lord stayed at 100% with no bot beside him; the assault walked to the
                // keep's centre and only took targets in reach there, and the lord stands upstairs. A closed gate on the way
                // is battered above (FindReachableEnemyDoor) as soon as the bot stands at it.
                GameKeepGuard lord = central.Guards.Values.FirstOrDefault(guard => guard is GuardLord && guard.IsAlive);
                if (lord != null && lord.CurrentRegionID == bot.CurrentRegionID && bot.IsWithinRadius(lord, BattlegroundLordReach))
                {
                    if (bot.IsWithinRadius(lord, BattlegroundLordStrike) && GameServer.ServerRules.IsAllowedToAttack(bot, lord, true))
                    {
                        bot.StopMovingOnPath();
                        bot.StopMoving();
                        bot.TargetObject = lord;
                        brain.AddToAggroList(lord, Math.Max(100, lord.EffectiveLevel * 12));
                        brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                        SetBattlegroundStatus(bot, bracket, "Assaulting the keep lord", $"{central.Name} is held by {Holder(central)}", lord.Name);
                        return false;
                    }
                    // In through a broken main gate, as players went: run 19 (2026-10-17) showed the stormers standing at a
                    // closed postern (never batterable) because the shortest path to the lord runs through it.
                    Vector3 here = new(bot.X, bot.Y, bot.Z);
                    if (_battlegroundGateKeep != central.KeepID || !bot.IsWithinRadius(lord, BattlegroundLordReach))
                    {
                        _battlegroundGateKeep = central.KeepID;
                        _battlegroundGatePassed = false;
                    }
                    GameKeepDoor gate = central.Doors.Values
                        .Where(door => door.IsAttackableDoor && !door.IsPostern && (!door.IsAlive || door.State == eDoorState.Open))
                        .OrderBy(door => bot.GetDistanceTo(door)).FirstOrDefault();
                    // Every main gate still shut: march on the nearest one and batter it (FindReachableEnemyDoor above
                    // takes it once in reach). Pathing to the lord through the closed keep found no path: 90 stormers of
                    // Dun Abermenai gave up their route that way in run 21 (2026-10-17).
                    if (gate == null && ClosedMainGate(bot, central) is { } shut)
                    {
                        // Each storm step turns the bot at once (a moving bot otherwise finished its previous walk first).
                        if (IssuePath(bot, new Vector3(shut.X, shut.Y, shut.Z), retargetWhileMoving: true))
                        {
                            TraceStorm(this, bot, central, lord, shut, "closed-gate");
                            SetBattlegroundStatus(bot, bracket, "Marching on the keep gate", $"{central.Name}: the gates are shut", shut.Name);
                            return true;
                        }
                    }
                    if (gate != null && !_battlegroundGatePassed)
                    {
                        if (bot.IsWithinRadius(gate, BattlegroundGateReached))
                            _battlegroundGatePassed = true;
                        else if (IssuePath(bot, new Vector3(gate.X, gate.Y, gate.Z), retargetWhileMoving: true))
                        {
                            TraceStorm(this, bot, central, lord, gate, "gate");
                            SetBattlegroundStatus(bot, bracket, "Storming the keep", $"{central.Name}: through the broken gate", lord.Name);
                            return true;
                        }
                        else TraceStorm(this, bot, central, lord, gate, "gate-path-refused");
                    }
                    if (IssuePath(bot, new Vector3(lord.X, lord.Y, lord.Z), retargetWhileMoving: true))
                    {
                        TraceStorm(this, bot, central, lord, gate, "lord");
                        SetBattlegroundStatus(bot, bracket, "Storming the keep", $"{central.Name}: heading for {lord.Name}", lord.Name);
                        return true;
                    }
                    TraceStorm(this, bot, central, lord, gate, "lord-path-refused");
                }
                if (FollowKeepTravel(bot, _rvrDestination))
                {
                    SetBattlegroundStatus(bot, bracket, $"Marching on {central.Name}", $"{Holder(central)} holds the central keep");
                    return true;
                }
                // With every main gate shut the keep's centre has no path (209 route give-ups in Abermenai, run 21): the
                // approach is then the nearest main gate.
                Vector3 approach = _rvrApproachDestination ?? (ClosedMainGate(bot, central) is { } closedGate &&
                    !central.Doors.Values.Any(door => door.IsAttackableDoor && !door.IsPostern && (!door.IsAlive || door.State == eDoorState.Open))
                        ? new Vector3(closedGate.X, closedGate.Y, closedGate.Z)
                        : new Vector3(central.X, central.Y, central.Z));
                if (Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), approach) > 120 * 120)
                    IssuePath(bot, approach);
                else
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                }
                SetBattlegroundStatus(bot, bracket, "At the keep assault approach", $"{central.Name} is held by {Holder(central)}");
                return true;
            }

            // Own (or no) central keep: a third of the realm's bots hold its walls, the rest roam the field.
            if (central != null && bot.DatabaseID % 3 == 0)
            {
                if (bot.GetDistanceTo(new Point3D(central.X, central.Y, central.Z)) > 3500)
                    return TravelToDefensivePost(bot, central, new(central.X, central.Y, central.Z));
                SetBattlegroundStatus(bot, bracket, $"Defending {central.Name}", "Holding the realm's central keep");
                return HoldDefensiveKeepPost(bot, central);
            }
            return RoamBattleground(bot, bracket, central);
        }

        private bool RoamBattleground(GameBot bot, BattlegroundBrackets.Bracket bracket, AbstractGameKeep central)
        {
            long now = GameLoop.GameLoopTime;
            Vector3 current = new(bot.X, bot.Y, bot.Z);
            if (!_battlegroundRoamPoint.HasValue || now >= _nextBattlegroundRoamTick ||
                Vector3.DistanceSquared(current, _battlegroundRoamPoint.Value) < 200 * 200)
            {
                // Roam between the field's landmarks: the central keep and the other realms' portal approaches.
                eRealm[] others = [eRealm.Albion, eRealm.Midgard, eRealm.Hibernia];
                Vector3[] anchors = others.Where(realm => realm != bot.Realm)
                    .Select(realm => BattlegroundBrackets.BotArrival(bracket, realm))
                    .Select(entry => Vector3.Lerp(new(entry.X, entry.Y, entry.Z),
                        central != null ? new(central.X, central.Y, central.Z) : new(entry.X, entry.Y, entry.Z), 0.6f))
                    .Append(central != null ? new Vector3(central.X, central.Y, central.Z) : current)
                    .ToArray();
                Vector3 anchor = anchors[Random.Shared.Next(anchors.Length)];
                Zone zone = bot.CurrentRegion?.GetZone((int)anchor.X, (int)anchor.Y) ?? bot.CurrentZone;
                IPathfindingMgr nav = PathfindingProvider.Instance;
                if (zone != null && nav.IsAvailable && nav.HasNavmesh(zone))
                {
                    // Landmark heights come from stored coordinates; settle them on the mesh first.
                    anchor = nav.GetClosestPoint(zone, anchor, 700, 700, 6000, nav.DefaultFilters) ?? anchor;
                    anchor = nav.GetRandomPoint(zone, anchor, 900, nav.DefaultFilters) ?? anchor;
                }
                _battlegroundRoamPoint = anchor;
                _nextBattlegroundRoamTick = now + 120_000 + bot.ObjectID % 30_000;
            }
            IssueVariedRvrPath(bot, _battlegroundRoamPoint.Value);
            SetBattlegroundStatus(bot, bracket, "Roaming the battleground",
                central != null ? $"{central.Name} is held by our realm" : "Hunting enemy realms on the field");
            return true;
        }

        // Stormers stop 1,600-2,500 from the lord with the gates down (runs 18-21), though the mesh reaches him from the
        // gates (navmesh probe 2026-10-17). One line every 20 s server-wide says where a stormer stands and what it walks to.
        private static long _nextStormTraceTick;
        private static void TraceStorm(AutonomousWorldBotController controller, GameBot bot, AbstractGameKeep keep, GameKeepGuard lord,
            GameKeepDoor gate, string step)
        {
            long now = GameLoop.GameLoopTime;
            long next = System.Threading.Interlocked.Read(ref _nextStormTraceTick);
            if (now < next || System.Threading.Interlocked.CompareExchange(ref _nextStormTraceTick, now + 20_000, next) != next)
                return;
            var mover = bot.movementComponent;
            Log.Info($"BG_STORM_TRACE bot={bot.Name} keep=\"{keep.Name}\" step={step} at={bot.X},{bot.Y},{bot.Z} " +
                     $"toLord={(int)bot.GetDistanceTo(lord)} lordAt={lord.X},{lord.Y},{lord.Z} " +
                     $"gate={(gate == null ? "none" : $"{gate.DoorId}@{gate.X},{gate.Y},{gate.Z} alive={gate.IsAlive} state={gate.State} toGate={(int)bot.GetDistanceTo(gate)}")} " +
                     $"moving={bot.IsMoving} path={mover?.LastPathfindingStatus} dest={(mover == null ? "-" : $"{mover.Destination.X:0},{mover.Destination.Y:0},{mover.Destination.Z:0}")} " +
                     $"inCombat={bot.InCombat} target={bot.TargetObject?.Name} " +
                     // Why a stormer stands between turns (run 22: 55% of the samples at the gate step stood 3,300 from it).
                     $"issued={Format(controller._issuedRouteDestination)} recovery={Format(controller._routeRecoveryWaypoint)} " +
                     $"orderIn={Math.Max(0, controller._nextMoveOrderTick - now)} stallReplans={controller._routeStallReplans}");

            static string Format(Vector3? point) => point is { } p ? $"{p.X:0},{p.Y:0},{p.Z:0}" : "-";
        }

        public const int BattlegroundLordReach = 4_000;
        public const int BattlegroundGateReached = 250;
        private int _battlegroundGateKeep;
        private bool _battlegroundGatePassed;
        public const int BattlegroundLordStrike = 900;

        private static string Holder(AbstractGameKeep keep) =>
            keep.Realm == eRealm.None ? "its neutral guards" : GlobalConstants.RealmToName(keep.Realm);

        /// <summary>The nearest standing, closed main gate (not a postern) of the keep.</summary>
        private static GameKeepDoor ClosedMainGate(GameBot bot, AbstractGameKeep keep) =>
            keep?.Doors.Values
                .Where(door => door.IsAlive && door.IsAttackableDoor && !door.IsPostern && door.State == eDoorState.Closed)
                .OrderBy(bot.GetDistanceTo)
                .FirstOrDefault();

        /// <summary>A closed gate of the enemy-held keep that this bot can stand at and hit, like players did.</summary>
        private static GameKeepDoor FindReachableEnemyDoor(GameBot bot, AbstractGameKeep keep)
        {
            if (keep == null || keep.Realm == bot.Realm) return null;
            return keep.Doors.Values
                .Where(door => door.IsAlive && door.IsAttackableDoor && door.State == eDoorState.Closed &&
                               bot.IsWithinRadius(door, 450) && GameServer.ServerRules.IsAllowedToAttack(bot, door, true))
                .OrderBy(bot.GetDistanceTo)
                .FirstOrDefault();
        }

        /// <summary>Teleporter trip into the bracket's battleground (walking to a realm teleporter first when needed).</summary>
        private bool TravelToBattleground(GameBot bot, BattlegroundBrackets.Bracket bracket, Vector3 goal)
        {
            if (BattlegroundBrackets.IsBattlegroundRegion(bot.CurrentRegionID))
                return LeaveBattleground(bot);
            if (!BattlegroundBrackets.Allows(bracket, bot.Level)) return false;
            if (TryTownTeleport(bot, bracket.RegionId, goal, BattlegroundNoWalkSeconds)) return true;

            GameNPC porter = NearestRealmTeleporter(bot);
            if (porter == null)
            {
                AutonomousObjectiveAssignments.EndBattlegroundTour(bot, "No realm teleporter could be reached for the battlegrounds");
                return true;
            }
            SetStatus(bot, $"Heading to {porter.Name}", $"Battleground: {bracket.Name}",
                $"Walking to the teleporter for {bracket.Name}", porter.Name, porter.CurrentZone?.Description ?? string.Empty);
            if (bot.CurrentRegionID == porter.CurrentRegionID)
                return IssuePath(bot, AutonomousTownTeleporters.StandOff(porter, bot));
            CampDestination previous = _camp;
            _camp = new("battleground-teleporter", porter.Name, porter.CurrentZone?.Description ?? string.Empty,
                porter.CurrentRegionID, porter.X, porter.Y, porter.Z, 1, false, false);
            try { return TravelAcrossRegions(bot); }
            finally { _camp = previous; }
        }

        /// <summary>The bot's own-realm teleporter: one in this region first, otherwise one in the realm's capital.</summary>
        private static GameNPC NearestRealmTeleporter(GameBot bot)
        {
            GameNPC local = AutonomousTownTeleporters.Porters(bot.CurrentRegion)
                .Where(porter => porter.Realm == bot.Realm)
                .OrderBy(porter => Vector2.DistanceSquared(new(bot.X, bot.Y), new(porter.X, porter.Y)))
                .FirstOrDefault();
            if (local != null) return local;
            ushort capital = bot.Realm switch { eRealm.Albion => 10, eRealm.Midgard => 101, eRealm.Hibernia => 201, _ => (ushort)0 };
            return capital == 0 ? null : AutonomousTownTeleporters.Porters(WorldMgr.GetRegion(capital))
                .FirstOrDefault(porter => porter.Realm == bot.Realm);
        }

        /// <summary>
        /// The players' way out: walk back to the realm's portal-keep arrival point and take the exit to the
        /// realm's border keep (the same destinations KeepManager.ExitBattleground uses).
        /// </summary>
        private bool LeaveBattleground(GameBot bot)
        {
            BattlegroundBrackets.Bracket here = BattlegroundBrackets.ForRegion(bot.CurrentRegionID);
            DbTeleport exit = BattlegroundExit(bot.Realm);
            if (here == null || exit == null) return false;
            GameLocation entry = BattlegroundBrackets.BotArrival(here, bot.Realm);
            // Walk to the mesh floor at the realm's arrival point (the exit portal is there).
            Vector3 exitSpot = AutonomousTownTeleporters.Arrival(new DbTeleport { RegionID = here.RegionId, X = entry.X, Y = entry.Y, Z = entry.Z }, 0);
            if (Vector2.DistanceSquared(new(bot.X, bot.Y), new(exitSpot.X, exitSpot.Y)) > BattlegroundExitRadius * BattlegroundExitRadius)
            {
                IssuePath(bot, exitSpot);
                SetStatus(bot, "Leaving the battleground", GoalText(), $"Walking to the {here.Name} exit portal");
                return true;
            }
            if (bot.InCombat) return false;
            Vector3 arrival = AutonomousTownTeleporters.Arrival(exit, 0);
            bot.StopMovingOnPath();
            bot.StopMoving();
            if (!bot.MoveTo((ushort)exit.RegionID, (int)arrival.X, (int)arrival.Y, (int)arrival.Z, (ushort)exit.Heading))
                return false;
            Log.Info($"AUTONOMOUS_BATTLEGROUND_EXIT bot={bot.Name} id={bot.DatabaseID} realm={GlobalConstants.RealmToName(bot.Realm)} level={bot.Level} " +
                     $"battleground={here.Name} destination=\"{exit.TeleportID}\"");
            bot.ForcePathReplot();
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Movement);
            AutonomousBotStatusPersistence.Queue(bot, true);
            ResetRouteOrderState();
            _nextPlanTick = 0;
            return true;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<eRealm, DbTeleport> BattlegroundExits = new();

        private static DbTeleport BattlegroundExit(eRealm realm)
        {
            string location = realm switch
            {
                eRealm.Albion => "Castle Sauvage",
                eRealm.Midgard => "Svasud Faste",
                eRealm.Hibernia => "Druim Ligen",
                _ => null,
            };
            if (location == null) return null;
            return BattlegroundExits.GetOrAdd(realm, _ =>
                DOLDB<DbTeleport>.SelectObject(DB.Column("TeleportID").IsEqualTo(location)));
        }

        private void SetBattlegroundStatus(GameBot bot, BattlegroundBrackets.Bracket bracket, string activity, string detail, string target = "")
        {
            if (bot.PersistentRecord != null)
                bot.PersistentRecord.ObjectivePhase = activity;
            SetStatus(bot, activity, $"Battleground: {bracket.Name} (levels {bracket.MinLevel}-{bracket.MaxLevel})", detail,
                target, bracket.Name);
        }
    }
}

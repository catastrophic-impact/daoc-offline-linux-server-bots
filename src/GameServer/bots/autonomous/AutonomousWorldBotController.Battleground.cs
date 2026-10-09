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
            _rvrIntent = assault ? AutonomousRvrEventLayer.Intent.AssaultKeep : AutonomousRvrEventLayer.Intent.Roam;

            if (assault)
            {
                if (TryRunSiegeJob(bot)) return true;
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
                if (FollowKeepTravel(bot, _rvrDestination))
                {
                    SetBattlegroundStatus(bot, bracket, $"Marching on {central.Name}", $"{Holder(central)} holds the central keep");
                    return true;
                }
                Vector3 approach = _rvrApproachDestination ?? new(central.X, central.Y, central.Z);
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

        private static string Holder(AbstractGameKeep keep) =>
            keep.Realm == eRealm.None ? "its neutral guards" : GlobalConstants.RealmToName(keep.Realm);

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

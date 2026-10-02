using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DOL.AI.Brain;

namespace DOL.GS
{
    // Optional task-boundary travel only. Existing movement/navmesh, stable and
    // region-transition executors are reused, not replaced with cosmetic paths.
    public sealed partial class AutonomousWorldBotController
    {
        public const string TownIdlePhase = "Town idling";
        private sealed record IdleTown(eRealm Realm, ushort RegionId, string Name,
            GameNPC Anchor, AbstractArea Area, bool Capital, int MinimumLevel, int MaximumLevel);
        private sealed record IdleDestination(IdleTown Town, Vector3 Point);
        private static readonly object TownCatalogSync = new();
        private static IdleTown[] _idleTowns = [];
        private static long _townCatalogExpires;
        private static long _nextTownRouteValidation;
        private IdleTown[] _townCandidates;
        private int _townCandidateIndex;
        private IdleDestination _townIdleDestination;
        private DateTime? _townIdleUntilUtc;
        private bool _townIdleFinished;
        private readonly record struct TownRouteValidation(bool Valid, IdleTown Town, Vector3 Point, double Distance);
        private Task<TownRouteValidation> _townRouteValidation;

        private void ResetTownIdle()
        {
            _townCandidates = null;
            _townCandidateIndex = 0;
            _townRouteValidation = null;
            _townIdleDestination = null;
            _townIdleUntilUtc = null;
            _townIdleFinished = false;
        }

        public static bool IsIdleTownLevelAppropriate(int level, bool capital, int minimum, int maximum)
        {
            return capital || minimum > 0 && maximum >= minimum && level >= Math.Max(1, minimum - 3) && level <= maximum + 5;
        }

        public static bool IsIdleTownArea(AbstractArea area)
        {
            // The installed classic/SI towns are named Circle records rather
            // than SafeArea records. Catalog admission ALSO requires a friendly
            // real merchant/trainer/stable and local leveling coverage.
            return area != null && (area.IsSafeArea || area is Area.BindArea ||
                area is Area.Circle circle && circle.Radius >= 500 && circle.Radius <= 6_000 &&
                !string.IsNullOrWhiteSpace(area.Description));
        }

        public static bool IsAtAssignedIdleTown(GameBot bot)
        {
            return bot?.CurrentRegion?.IsCapitalCity == true || bot?.CurrentAreas?.OfType<AbstractArea>()
                .Any(area => IsIdleTownArea(area) && area.Description == bot.PersistentRecord?.TravelDestination) == true;
        }

        private bool HandleTownIdle(BotBrain brain, GameBot bot)
        {
            if (_townIdleFinished) return false;
            DateTime now = DateTime.UtcNow;
            if (!DateTime.TryParse(bot.PersistentRecord.ObjectiveExpiresUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime deadline))
                return FinishTownIdle(bot, "Invalid maintenance deadline");
            deadline = deadline.ToUniversalTime();

            if (_townIdleUntilUtc.HasValue)
            {
                if (now >= _townIdleUntilUtc.Value || now >= deadline)
                    return FinishTownIdle(bot, "Town break completed");
                if (!IsInsideIdleTown(bot, _townIdleDestination))
                    return FinishTownIdle(bot, "Left the selected safe town; cancelling optional break");
                if (Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), _townIdleDestination.Point) > 96 * 96)
                {
                    bot.WakeRecoveryRest();
                    if (!IssuePath(bot, _townIdleDestination.Point))
                        return FinishTownIdle(bot, "Could not return to the selected town spot after interruption");
                    SetStatus(bot, "Returning to town idle spot", $"Idle in {_townIdleDestination.Town.Name}",
                        "Returning after interruption without extending either deadline", string.Empty, _townIdleDestination.Town.Name);
                    return true;
                }
                bot.StopMovingOnPath();
                bot.StopMoving();
                if (brain.CheckHeals()) return true;
                if (AutonomousRestPolicy.IsFullyRecovered(bot.HealthPercent, bot.ManaPercent, bot.EndurancePercent, bot.MaxMana > 0))
                    bot.WakeRecoveryRest();
                else if (AutonomousRestPolicy.NeedsRecovery(bot.HealthPercent, bot.ManaPercent, bot.EndurancePercent, bot.MaxMana > 0))
                {
                    if (!BotRestRecovery.BlocksRest(bot))
                        bot.BeginRecoveryRest();
                }
                PublishTownIdleStatus(bot, deadline);
                // Existing rare context-aware ambient chat is already called by
                // BotBrain. No additional chat timer, roll or crowd broadcast.
                return true;
            }

            TimeSpan remaining = deadline - now;
            if (remaining < AutonomousTownDowntime.MinimumDuration)
                return FinishTownIdle(bot, "Less than fifteen minutes remain after maintenance/travel");
            if (_townIdleDestination == null)
            {
                _townCandidates ??= IdleTownCatalog().Where(town => town.Realm == bot.Realm &&
                    IsIdleTownLevelAppropriate(bot.Level, town.Capital, town.MinimumLevel, town.MaximumLevel))
                    .OrderBy(_ => Random.Shared.Next()).ToArray();
                SetStatus(bot, "Choosing a reachable town", "Take an optional 15-30 minute town break",
                    "Validating a same-realm town route within the original maintenance deadline");

                // A validation walks up to 64 navmesh path queries. It runs on a
                // worker thread so one bot's route proof never stretches the
                // whole game-loop tick; the result is applied on a later turn.
                if (_townRouteValidation != null)
                {
                    if (!_townRouteValidation.IsCompleted)
                        return true;
                    TownRouteValidation result = _townRouteValidation.IsCompletedSuccessfully
                        ? _townRouteValidation.Result : default;
                    _townRouteValidation = null;
                    remaining = deadline - DateTime.UtcNow;
                    if (result.Valid &&
                        TimeSpan.FromSeconds(result.Distance / Math.Max(1, (int)bot.MaxSpeed) * 1.25 + 10) + AutonomousTownDowntime.MinimumDuration <= remaining)
                    {
                        _townIdleDestination = new(result.Town, result.Point);
                        Log.Info($"AUTONOMOUS_TOWN_IDLE_ROUTE bot=\"{bot.Name}\" id={bot.DatabaseID} level={bot.Level} " +
                            $"town=\"{result.Town.Name}\" region={result.Town.RegionId} destination={result.Point.X},{result.Point.Y},{result.Point.Z} " +
                            $"validatedWalkUnits={(int)result.Distance}");
                    }
                    return true;
                }

                if (_townCandidateIndex >= Math.Min(12, _townCandidates.Length))
                    return FinishTownIdle(bot, "No fully validated town route fits the remaining budget");

                // At most two candidate validations a second server-wide, and
                // only for the 15% task-boundary roll. Never a per-tick world scan.
                long tick = GameLoop.GameLoopTime;
                long next = Volatile.Read(ref _nextTownRouteValidation);
                if (tick < next || Interlocked.CompareExchange(ref _nextTownRouteValidation, tick + 500, next) != next)
                    return true;
                IdleTown town = _townCandidates[_townCandidateIndex++];
                if (TryChooseTownPoint(bot, town, out Vector3 point))
                {
                    eRealm realm = bot.Realm;
                    ushort startRegion = bot.CurrentRegionID;
                    Vector3 start = new(bot.X, bot.Y, bot.Z);
                    _townRouteValidation = Task.Run(() =>
                        TryValidateTownRoute(realm, startRegion, start, town.RegionId, point, out double distance)
                            ? new TownRouteValidation(true, town, point, distance)
                            : default);
                }
                return true;
            }

            IdleDestination selected = _townIdleDestination;
            if (bot.CurrentRegionID != selected.Town.RegionId)
            {
                CampDestination previous = _camp;
                try
                {
                    _camp = new("town-idle", selected.Town.Name, selected.Town.Name, selected.Town.RegionId,
                        (int)selected.Point.X, (int)selected.Point.Y, (int)selected.Point.Z, 0, false, false);
                    return TravelAcrossRegions(bot);
                }
                finally { _camp = previous; }
            }
            if (Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), selected.Point) > 48 * 48)
            {
                bot.WakeRecoveryRest();
                if (TryBeginFasterStableRoute(bot, selected.Point, selected.Town.Name)) return true;
                if (!IssuePath(bot, selected.Point))
                    return FinishTownIdle(bot, "Town route failed during travel; leaving it for diagnostics");
                SetStatus(bot, $"Traveling to {selected.Town.Name}", $"Idle in {selected.Town.Name}",
                    "Following a validated route; the idle timer starts only on arrival", string.Empty, selected.Town.Name);
                return true;
            }

            if (!IsInsideIdleTown(bot, selected))
                return FinishTownIdle(bot, "Arrival is outside the safe area");
            DateTime arrived = DateTime.UtcNow;
            TimeSpan? duration = AutonomousTownDowntime.RollWithinBudget(deadline - arrived);
            if (!duration.HasValue)
                return FinishTownIdle(bot, "Travel left insufficient time for the minimum break");
            _townIdleUntilUtc = arrived.Add(duration.Value);
            bot.StopMovingOnPath();
            bot.StopMoving();
            PublishTownIdleStatus(bot, deadline);
            Log.Info($"AUTONOMOUS_TOWN_IDLE_STARTED bot=\"{bot.Name}\" id={bot.DatabaseID} town=\"{selected.Town.Name}\" " +
                $"durationSeconds={(int)duration.Value.TotalSeconds} untilUtc={_townIdleUntilUtc:O}");
            return true;
        }

        private void PublishTownIdleStatus(GameBot bot, DateTime deadline)
        {
            bot.PersistentRecord.ObjectivePhase = TownIdlePhase;
            SetStatus(bot, TownIdlePhase, $"Idle in {_townIdleDestination.Town.Name}",
                $"Town break ends {_townIdleUntilUtc.Value:HH:mm:ss} UTC; original maintenance deadline {deadline:HH:mm:ss} UTC",
                string.Empty, _townIdleDestination.Town.Name);
        }

        private bool FinishTownIdle(GameBot bot, string reason)
        {
            _townIdleFinished = true;
            _townIdleUntilUtc = null;
            bot.WakeRecoveryRest();
            bot.StopMovingOnPath();
            bot.StopMoving();
            bot.PersistentRecord.ObjectivePhase = "Between tasks";
            Log.Info($"AUTONOMOUS_TOWN_IDLE_ENDED bot=\"{bot.Name}\" id={bot.DatabaseID} reason=\"{reason}\"");
            return false;
        }

        private static bool IsInsideIdleTown(GameBot bot, IdleDestination destination)
        {
            return destination != null && bot.CurrentRegionID == destination.Town.RegionId &&
                (destination.Town.Capital || destination.Town.Area?.IsContaining(new Point3D(bot.X, bot.Y, bot.Z), false) == true);
        }

        private static IdleTown[] IdleTownCatalog()
        {
            long now = GameLoop.GameLoopTime;
            if (now < Volatile.Read(ref _townCatalogExpires)) return _idleTowns;
            lock (TownCatalogSync)
            {
                if (now < _townCatalogExpires) return _idleTowns;
                var towns = new List<IdleTown>();
                var seen = new HashSet<(ushort, ushort)>();
                var camps = CampCatalogSnapshot().GroupBy(cell => cell.Zone.ID).ToDictionary(group => group.Key, group => group.ToArray());
                foreach (Region region in WorldMgr.GetAllRegions().Where(region => region != null &&
                    !region.IsDungeon && AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(region.Expansion)))
                {
                    foreach (GameNPC anchor in region.Objects.OfType<GameNPC>().Where(npc =>
                        npc.ObjectState == GameObject.eObjectState.Active && npc.CurrentZone != null &&
                        npc is GameMerchant or GameTrainer or GameStableMaster))
                    {
                        Zone zone = anchor.CurrentZone;
                        eRealm realm = ProtectedRealm(region.ID, zone.ID);
                        if (realm == eRealm.None || anchor.Realm != eRealm.None && anchor.Realm != realm ||
                            IsFrontierZone(region.ID, zone.ID)) continue;
                        AbstractArea area = anchor.CurrentAreas?.OfType<AbstractArea>().FirstOrDefault(IsIdleTownArea);
                        if (!region.IsCapitalCity && area == null) continue;
                        if (!seen.Add((region.ID, region.IsCapitalCity ? ushort.MaxValue : area.ID))) continue;
                        CampCatalogCell[] nearby = camps.GetValueOrDefault(zone.ID, [])
                            .Where(cell => DistanceSquared(anchor.X, anchor.Y, cell.X, cell.Y) <= 18_000L * 18_000L)
                            .OrderBy(cell => DistanceSquared(anchor.X, anchor.Y, cell.X, cell.Y)).Take(8).ToArray();
                        int[] levels = nearby.SelectMany(cell => cell.Levels).Where(level => level >= 1 && level <= 50).ToArray();
                        if (!region.IsCapitalCity && levels.Length == 0) continue;
                        towns.Add(new(realm, region.ID, region.IsCapitalCity ? region.Description : area.Description,
                            anchor, area, region.IsCapitalCity, levels.Length > 0 ? levels.Min() : 1,
                            levels.Length > 0 ? levels.Max() : 50));
                    }
                }
                _idleTowns = towns.ToArray();
                Volatile.Write(ref _townCatalogExpires, now + 10 * 60_000);
                return _idleTowns;
            }
        }

        private static bool TryChooseTownPoint(GameBot bot, IdleTown town, out Vector3 point)
        {
            point = default;
            GameNPC anchor = town.Anchor;
            IPathfindingMgr nav = PathfindingProvider.Instance;
            if (anchor?.ObjectState != GameObject.eObjectState.Active || !nav.IsAvailable || !nav.HasNavmesh(anchor.CurrentZone)) return false;
            Vector3 center = new(anchor.X, anchor.Y, anchor.Z);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Vector3? candidate = nav.GetRandomPoint(anchor.CurrentZone, center, town.Capital ? 900 : 600, nav.DefaultFilters);
                if (!candidate.HasValue || anchor.CurrentRegion.GetZone((int)candidate.Value.X, (int)candidate.Value.Y) != anchor.CurrentZone ||
                    !town.Capital && town.Area?.IsContaining(new Point3D((int)candidate.Value.X, (int)candidate.Value.Y, (int)candidate.Value.Z), false) != true)
                    continue;
                // Safe PvP areas need not exclude aggressive monsters. Reject a
                // resting spot that lies in a currently live monster's aggro bubble.
                bool dangerous = anchor.GetNPCsInRadius(3_500).Any(npc => npc.IsAlive && IsExperienceMonster(npc) && npc.IsAggressive &&
                    npc.Brain is StandardMobBrain mobBrain &&
                    Vector3.DistanceSquared(candidate.Value, new(npc.X, npc.Y, npc.Z)) <= (double)mobBrain.AggroRange * mobBrain.AggroRange);
                if (dangerous) continue;
                point = candidate.Value;
                return true;
            }
            return false;
        }

        // Conservative proof of the exact canonical walking itinerary. Stable
        // travel can improve it, but is never needed to excuse a missing mesh.
        // Each zone corridor must finish; disconnected islands/partial dead ends
        // are rejected. This never changes the world's route/pathfinding policy.
        private static bool TryValidateTownRoute(GameBot bot, ushort targetRegion, Vector3 target, out double length) =>
            TryValidateTownRoute(bot.Realm, bot.CurrentRegionID, new(bot.X, bot.Y, bot.Z), targetRegion, target, out length);

        // Snapshot inputs only: this runs on a worker thread (navmesh queries
        // are thread-local), so it must not read the live bot.
        private static bool TryValidateTownRoute(eRealm realm, ushort startRegion, Vector3 start,
            ushort targetRegion, Vector3 target, out double length)
        {
            length = 0;
            Vector3 cursor = start;
            ushort regionId = startRegion;
            var visited = new HashSet<ushort>();
            int queryBudget = 64;
            for (int hop = 0; hop < 12; hop++)
            {
                if (!visited.Add(regionId)) return false;
                Region region = WorldMgr.GetRegion(regionId);
                if (regionId == targetRegion)
                    return ValidateTownRegionWalk(region, cursor, target, ref queryBudget, ref length);
                var crossing = FindNextCrossing(realm, regionId, targetRegion, (int)target.X, (int)target.Y);
                if (crossing == null || !ValidateTownRegionWalk(region, cursor,
                    new(crossing.SourceX, crossing.SourceY, crossing.SourceZ), ref queryBudget, ref length)) return false;
                regionId = crossing.TargetRegion;
                cursor = new(crossing.TargetX, crossing.TargetY, crossing.TargetZ);
            }
            return false;
        }

        private static bool ValidateTownRegionWalk(Region region, Vector3 cursor, Vector3 target, ref int budget, ref double length)
        {
            IPathfindingMgr nav = PathfindingProvider.Instance;
            var zones = new HashSet<ushort>();
            for (int seam = 0; region != null && seam < 16; seam++)
            {
                Zone zone = region.GetZone((int)cursor.X, (int)cursor.Y);
                Zone goalZone = region.GetZone((int)target.X, (int)target.Y);
                if (zone == null || goalZone == null || !nav.HasNavmesh(zone) || !zones.Add(zone.ID)) return false;
                if (zone == goalZone) return ValidateTownCorridor(nav, zone, cursor, target, ref budget, ref length);
                if (!AutonomousZoneItinerary.TryNextStep(region, zone, goalZone, cursor, target, nav, out var step) ||
                    !ValidateTownCorridor(nav, zone, cursor, step.Inside, ref budget, ref length)) return false;
                length += Vector3.Distance(step.Inside, step.Outside);
                cursor = step.Outside;
            }
            return false;
        }

        public static bool IsCompleteTownCorridor(PathfindingResult result, Vector3 last, Vector3 target)
        {
            return result.Status == PathfindingStatus.PathFound && result.NodeCount > 0 &&
                Vector3.DistanceSquared(last, target) <= 48 * 48;
        }

        private static bool ValidateTownCorridor(IPathfindingMgr nav, Zone zone, Vector3 cursor, Vector3 target,
            ref int budget, ref double length)
        {
            if (!nav.TrySnapToMesh(zone, ref cursor, 96) || !nav.TrySnapToMesh(zone, ref target, 96)) return false;
            WrappedPathfindingNode[] nodes = ArrayPool<WrappedPathfindingNode>.Shared.Rent(512);
            try
            {
                for (int segment = 0; segment < 12 && budget-- > 0; segment++)
                {
                    PathfindingResult result = nav.GetPathStraight(zone, cursor, target, nav.DefaultFilters, nodes);
                    if (result.NodeCount < 1 || result.NodeCount > nodes.Length) return false;
                    Vector3 end = nodes[result.NodeCount - 1].Position;
                    bool complete = IsCompleteTownCorridor(result, end, target);
                    if (!complete && (!AutonomousRouteRecoveryPolicy.CanContinuePartial(result.Status, cursor, end) ||
                        Vector3.DistanceSquared(end, target) >= Vector3.DistanceSquared(cursor, target))) return false;
                    Vector3 previous = cursor;
                    for (int index = 0; index < result.NodeCount; index++)
                    {
                        length += Vector3.Distance(previous, nodes[index].Position);
                        previous = nodes[index].Position;
                    }
                    if (complete) return true;
                    cursor = end;
                }
                return false;
            }
            finally { ArrayPool<WrappedPathfindingNode>.Shared.Return(nodes); }
        }
    }
}

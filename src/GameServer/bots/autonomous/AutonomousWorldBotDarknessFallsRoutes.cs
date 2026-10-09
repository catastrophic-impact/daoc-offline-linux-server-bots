using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.Database;

namespace DOL.GS;

public sealed partial class AutonomousWorldBotController
{
    private string _darknessFallsRouteKey;
    private string _darknessFallsLastProofId;
    private Vector3[] _darknessFallsWaypoints;
    private int _darknessFallsNextWaypoint;
    private Vector3 _darknessFallsLastProgressPosition;
    private long _darknessFallsLastProgressTick;
    private long _darknessFallsNextOrderTick;
    private string _darknessFallsLastExitProbeKey;
    private Vector3 _darknessFallsLastExitProbePosition;
    private long _darknessFallsNextExitProbeTick;
    private int _darknessFallsExitCandidateWindow;
    private long _darknessFallsNextNearbyCampProbeTick;

    /// <summary>Ordinary DF grinds use the exact spawn's ordered, realm-specific
    /// native route legs. Never ask Detour for one long 256-node partial route
    /// from the entrance to a deep camp, or treat another wing's same-name
    /// creature as the assigned destination.</summary>
    private bool FollowDarknessFallsCampRoute(GameBot bot)
    {
        if (_camp?.RegionId != AutonomousDarknessFallsPolicy.RegionId ||
            bot.CurrentRegionID != AutonomousDarknessFallsPolicy.RegionId ||
            AutonomousRealmRaid.GetView(bot.Group) != null)
            return false;
        if (!AutonomousDungeonGoalCatalog.HasCompleteDarknessFallsCatalog ||
            !AutonomousDarknessFallsRoutePlan.TryGetCampMobId(_camp.Id, out string mobId) ||
            !AutonomousDarknessFallsNavigation.TryGetProof(mobId, out var proof) ||
            !AutonomousDarknessFallsRoutePlan.TryGetWaypoints(
                proof.Routes?.SingleOrDefault(route => route.Realm == bot.Realm)?.InWaypoints,
                out Vector3[] incoming))
        {
            AbandonCamp(bot, "Darkness Falls camp has no matching complete spawn route certificate");
            return true;
        }

        string key = $"in:{bot.Realm}:{mobId}";
        if (string.Equals(_darknessFallsRouteKey, key, StringComparison.Ordinal) &&
            _darknessFallsNextWaypoint >= (_darknessFallsWaypoints?.Length ?? 0) &&
            Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), incoming[^1]) > 220 * 220)
        {
            // After a fight or local patrol, do not treat a stale completed
            // cursor as proof that the actor is still at the certified camp.
            _darknessFallsRouteKey = null;
        }
        if (!string.Equals(_darknessFallsRouteKey, key, StringComparison.Ordinal))
        {
            _darknessFallsLastProofId = mobId;
            if (!StartDarknessFallsRoute(bot, key, incoming))
            {
                AbandonCamp(bot, "No complete native approach to the assigned Darkness Falls route");
                return true;
            }
        }

        if (AdvanceDarknessFallsRoute(bot, $"Approaching {_camp.MonsterName}")) return true;
        return false;
    }

    /// <summary>After a one-spawn room empties, keep the same monster objective
    /// inside this realm's wing. Every replacement has a live, appropriate-level
    /// target and a complete certified route from the bot's current floor.</summary>
    private bool TryContinueNearbyDarknessFallsGrind(GameBot bot)
    {
        if (_camp?.RegionId != AutonomousDarknessFallsPolicy.RegionId ||
            bot.CurrentRegionID != AutonomousDarknessFallsPolicy.RegionId ||
            (bot.Group?.MemberCount ?? 1) > 1 ||
            !AutonomousDarknessFallsNavigation.IsReady)
            return false;

        long now = GameLoop.GameLoopTime;
        if (now < _darknessFallsNextNearbyCampProbeTick) return false;
        _darknessFallsNextNearbyCampProbeTick = now + 15_000;
        Zone zone = bot.CurrentZone;
        if (zone == null) return false;
        Vector3 current = new(bot.X, bot.Y, bot.Z);
        ConColor maximumCon = MaximumTargetCon(1);
        GameNPC[] live = bot.GetNPCsInRadius(TargetSearchRadius)
            .Where(npc => npc.IsAlive && IsExperienceMonster(npc) &&
                npc.CurrentZone == zone &&
                string.Equals(npc.Name, _camp.MonsterName, StringComparison.OrdinalIgnoreCase) &&
                AutonomousDarknessFallsNavigation.TryGetProof(npc.InternalID, out _) &&
                GameServer.ServerRules.IsAllowedToAttack(bot, npc, true))
            .Where(npc =>
            {
                ConColor con = ConLevels.GetConColor(bot.GetConLevel(npc));
                return con >= ConColor.GREEN && con <= maximumCon;
            }).ToArray();
        if (live.Length == 0) return false;

        foreach (CampCatalogCell candidate in CampCatalogSnapshot()
            .Where(cell => cell.RegionId == AutonomousDarknessFallsPolicy.RegionId &&
                cell.Zone == zone && cell.Id != _camp.Id &&
                AutonomousDarknessFallsNavigation.IsNearbySameWingCamp(bot.Realm,
                    cell.DarknessFallsWing, _camp.MonsterName, cell.MonsterName, current,
                    new(cell.X, cell.Y, cell.Z), TargetSearchRadius))
            .OrderBy(cell => Vector3.DistanceSquared(current, new(cell.X, cell.Y, cell.Z))))
        {
            if (!AutonomousDarknessFallsRoutePlan.TryGetCampMobId(candidate.Id, out string mobId) ||
                // Nearby certified rooms overlap. A live neighbor must not
                // make this representative's still-empty room look occupied.
                !live.Any(npc => string.Equals(npc.InternalID, mobId,
                    StringComparison.OrdinalIgnoreCase)) ||
                !AutonomousDarknessFallsNavigation.TryGetProof(mobId, out var proof) ||
                !AutonomousDarknessFallsRoutePlan.TryGetWaypoints(
                    proof.Routes?.SingleOrDefault(route => route.Realm == bot.Realm)?.InWaypoints,
                    out Vector3[] incoming))
                continue;

            string key = $"in:{bot.Realm}:{mobId}";
            if (!StartDarknessFallsRoute(bot, key, incoming)) continue;
            _darknessFallsLastProofId = mobId;
            _camp = new(candidate.Id, candidate.MonsterName, candidate.ZoneName,
                candidate.RegionId, candidate.X, candidate.Y, candidate.Z,
                candidate.LiveMobCount, candidate.IsDungeon, candidate.IsFrontier);
            _emptyCampSinceTick = 0;
            _patrolDestination = null;
            _nextMoveOrderTick = 0;
            if (bot.PersistentRecord != null)
                bot.PersistentRecord.CurrentCampId = candidate.Id;
            BeginCampDiagnostics(bot);
            SetStatus(bot, $"Searching another {_camp.MonsterName} room", GoalText(),
                "The previous room is empty; following a complete route to a live copy in this realm's wing",
                _camp.MonsterName, _camp.ZoneName);
            return true;
        }
        return false;
    }

    /// <summary>On leaving DF, follow a proven tail to the physical own-realm
    /// portal. A saved or reassigned bot may rejoin a nearby out-anchor only
    /// after a complete strict native current-to-anchor check; otherwise it
    /// holds rather than crossing a wall, reversing an entrance fall, or using
    /// another faction's portal.</summary>
    private bool FollowDarknessFallsHomeExit(GameBot bot, DbZonePoint crossing)
    {
        if (bot.CurrentRegionID != AutonomousDarknessFallsPolicy.RegionId ||
            !AutonomousDarknessFallsPolicy.CanUsePortalRow(bot.Realm, crossing))
            return false;
        if (!AutonomousDarknessFallsNavigation.IsReady)
            return EvacuateUncertifiedDarknessFalls(bot);

        string key = $"out:{bot.Realm}:{crossing.Id}";
        if (!string.Equals(_darknessFallsRouteKey, key, StringComparison.Ordinal))
        {
            Vector3 current = new(bot.X, bot.Y, bot.Z);
            long now = GameLoop.GameLoopTime;
            bool shouldProbe = AutonomousDarknessFallsRoutePlan.ShouldProbeExit(
                _darknessFallsLastExitProbeKey, key, _darknessFallsLastExitProbePosition,
                current, now, _darknessFallsNextExitProbeTick);
            if (shouldProbe && (!string.Equals(_darknessFallsLastExitProbeKey, key, StringComparison.Ordinal) ||
                Vector3.DistanceSquared(current, _darknessFallsLastExitProbePosition) >= 96 * 96))
                _darknessFallsExitCandidateWindow = 0;
            if (shouldProbe && TryStartDarknessFallsExitRoute(bot, key))
            {
                _darknessFallsLastExitProbeKey = null;
                _darknessFallsNextExitProbeTick = 0;
                _darknessFallsExitCandidateWindow = 0;
            }
            else
            {
                if (shouldProbe)
                {
                    _darknessFallsLastExitProbeKey = key;
                    _darknessFallsLastExitProbePosition = current;
                    _darknessFallsNextExitProbeTick = now + 15_000;
                    _darknessFallsExitCandidateWindow++;
                }
                bot.StopMovingOnPath();
                bot.StopMoving();
                SetStatus(bot, "Holding inside Darkness Falls", "Reach own realm exit",
                    "No complete certified route from this floor to a home-exit waypoint is available");
                return true;
            }
        }

        return AdvanceDarknessFallsRoute(bot, "Returning to own Darkness Falls exit");
    }

    private bool TryStartDarknessFallsExitRoute(GameBot bot, string key)
    {
        Zone zone = bot.CurrentZone;
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (zone?.ZoneRegion?.ID != AutonomousDarknessFallsPolicy.RegionId || !nav.IsAvailable ||
            !nav.HasNavmesh(zone)) return false;
        Vector3 current = new(bot.X, bot.Y, bot.Z);

        // Reuse the camp we just finished when possible. That approach floor
        // has an explicitly certified return tail, so it avoids a global scan.
        if (!string.IsNullOrWhiteSpace(_darknessFallsLastProofId) &&
            AutonomousDarknessFallsNavigation.TryGetProof(_darknessFallsLastProofId, out var previous) &&
            TryStartExitProof(bot, key, previous, current, nav, zone)) return true;

        // After a restart, the controller has no previous camp. Search the
        // nearest certified out-anchors, not a geometrically nearest other
        // realm portal. Each candidate still needs a full strict native leg.
        var candidates = AutonomousDarknessFallsNavigation.SnapshotCertifiedProofs()
            .Select(proof => (Proof: proof,
                Route: proof.Routes?.SingleOrDefault(route => route.Realm == bot.Realm)))
            .Where(item => item.Route?.OutWaypoints is { Length: >= 2 })
            .SelectMany(item => item.Route.OutWaypoints.Select((point, index) =>
                (item.Proof, item.Route, Index: index,
                    Point: new Vector3(point[0], point[1], point[2]))))
            // A common exit anchor occurs in hundreds of proof chains. Probe
            // distinct positions so duplicates cannot exhaust the search.
            .GroupBy(item => item.Point)
            .Select(group => group.First())
            .OrderBy(item => Vector3.DistanceSquared(current, item.Point))
            .ToArray();
        if (candidates.Length == 0) return false;
        // Each failed, rate-limited retry tests the next distinct window.
        // Otherwise a nearby but wall-separated cluster could starve a
        // farther reachable return anchor forever.
        int windowCount = (candidates.Length + 63) / 64;
        int window = _darknessFallsExitCandidateWindow % windowCount;
        foreach (var candidate in candidates.Skip(window * 64).Take(64))
        {
            if (Vector3.DistanceSquared(current, candidate.Point) > 16 * 16 &&
                !AutonomousDarknessFallsNavigation.HasStrictSegment(nav, zone, current, candidate.Point))
                continue;
            if (!AutonomousDarknessFallsRoutePlan.TryGetWaypoints(candidate.Route.OutWaypoints,
                    out Vector3[] tail)) continue;
            int cursor = Vector3.DistanceSquared(current, candidate.Point) <= 16 * 16
                ? candidate.Index + 1 : candidate.Index;
            ActivateDarknessFallsRoute(bot, key, tail, cursor);
            _darknessFallsLastProofId = candidate.Proof.Id;
            return true;
        }
        return false;
    }

    private bool TryStartExitProof(GameBot bot, string key,
        AutonomousDarknessFallsNavigation.SpawnProof proof, Vector3 current,
        IPathfindingMgr nav, Zone zone)
    {
        if (!AutonomousDarknessFallsRoutePlan.TryGetWaypoints(
                proof.Routes?.SingleOrDefault(route => route.Realm == bot.Realm)?.OutWaypoints,
                out Vector3[] tail) ||
            !AutonomousDarknessFallsRoutePlan.TryRejoin(current, tail,
                (from, to) => AutonomousDarknessFallsNavigation.HasStrictSegment(nav, zone, from, to),
                out int cursor)) return false;
        ActivateDarknessFallsRoute(bot, key, tail, cursor);
        return true;
    }

    /// <summary>
    /// A raid member inside Darkness Falls whose post is not one proven leg away walks the realm's
    /// certified chain down to it, like the raid front did (late members used to retry a direct
    /// path from the entrance floor forever, 2026-10-06). False: no chain needed or none found.
    /// </summary>
    private bool FollowDarknessFallsRaidChain(GameBot bot, Vector3 post, string status)
    {
        Zone zone = bot.CurrentZone;
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (bot.CurrentRegionID != AutonomousDarknessFallsPolicy.RegionId || zone == null ||
            !nav.IsAvailable || !nav.HasNavmesh(zone))
            return false;
        Vector3 current = new(bot.X, bot.Y, bot.Z);
        string key = $"raid:{bot.Realm}:{(int)post.X >> 6}:{(int)post.Y >> 6}:{(int)post.Z >> 6}";
        if (!string.Equals(_darknessFallsRouteKey, key, StringComparison.Ordinal))
        {
            if (AutonomousDarknessFallsNavigation.HasStrictSegment(nav, zone, current, post)) return false;
            if (!RealmRaidDungeonRoute.TryDarknessFallsChain(nav, zone, bot.Realm, post, out Vector3[] chain) ||
                !StartDarknessFallsRoute(bot, key, chain))
                return false;
        }
        return AdvanceDarknessFallsRoute(bot, status);
    }

    private bool StartDarknessFallsRoute(GameBot bot, string key, Vector3[] waypoints)
    {
        Zone zone = bot.CurrentZone;
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (zone?.ZoneRegion?.ID != AutonomousDarknessFallsPolicy.RegionId || !nav.IsAvailable ||
            !nav.HasNavmesh(zone) ||
            !AutonomousDarknessFallsRoutePlan.TryRejoin(new(bot.X, bot.Y, bot.Z), waypoints,
                (from, to) => AutonomousDarknessFallsNavigation.HasStrictSegment(nav, zone, from, to),
                out int cursor)) return false;
        ActivateDarknessFallsRoute(bot, key, waypoints, cursor);
        return true;
    }

    private void ActivateDarknessFallsRoute(GameBot bot, string key, Vector3[] waypoints, int cursor)
    {
        bot.StopMovingOnPath();
        bot.StopMoving();
        _darknessFallsRouteKey = key;
        _darknessFallsWaypoints = waypoints;
        _darknessFallsNextWaypoint = cursor;
        _darknessFallsLastProgressPosition = new(bot.X, bot.Y, bot.Z);
        _darknessFallsLastProgressTick = GameLoop.GameLoopTime;
        _darknessFallsNextOrderTick = 0;
    }

    private bool AdvanceDarknessFallsRoute(GameBot bot, string status)
    {
        Vector3[] waypoints = _darknessFallsWaypoints;
        if (waypoints == null) return true;
        Vector3 current = new(bot.X, bot.Y, bot.Z);
        if (_darknessFallsNextWaypoint >= waypoints.Length)
            return false;
        while (_darknessFallsNextWaypoint < waypoints.Length &&
               AutonomousDarknessFallsRoutePlan.IsAtWaypoint(current, waypoints[_darknessFallsNextWaypoint]))
        {
            _darknessFallsNextWaypoint++;
            _darknessFallsNextOrderTick = 0;
        }
        if (_darknessFallsNextWaypoint >= waypoints.Length)
        {
            bot.StopMovingOnPath();
            bot.StopMoving();
            return false;
        }

        Vector3 target = waypoints[_darknessFallsNextWaypoint];
        Zone zone = bot.CurrentZone;
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (zone?.ZoneRegion?.ID != AutonomousDarknessFallsPolicy.RegionId)
        {
            bot.StopMovingOnPath();
            bot.StopMoving();
            SetStatus(bot, "Holding on Darkness Falls route", status,
                "The certified route is no longer in Darkness Falls");
            return true;
        }

        long now = GameLoop.GameLoopTime;
        if (Vector3.DistanceSquared(current, _darknessFallsLastProgressPosition) >= 32 * 32)
        {
            _darknessFallsLastProgressPosition = current;
            _darknessFallsLastProgressTick = now;
        }
        if (bot.IsMoving && now - _darknessFallsLastProgressTick < 12_000)
            return true;
        if (now < _darknessFallsNextOrderTick)
            return true;

        // The already-issued path passed the strict native check, and the
        // pathfinder independently guards every queued DF path. Re-query only
        // before a new movement order (or after movement stalls), not on
        // every brain tick while the bot is visibly progressing.
        if (!AutonomousDarknessFallsNavigation.HasStrictSegment(nav, zone, current, target))
        {
            bot.StopMovingOnPath();
            bot.StopMoving();
            SetStatus(bot, "Holding on Darkness Falls route", status,
                "The next 3D waypoint has no complete certified native path; no partial path or cliff shortcut is used");
            return true;
        }

        bot.ForcePathReplot();
        bot.PathTo(target, bot.MaxSpeed);
        _darknessFallsNextOrderTick = now + 3_000;
        _darknessFallsLastProgressPosition = current;
        _darknessFallsLastProgressTick = now;
        SetStatus(bot, status, "Certified Darkness Falls route",
            $"Following waypoint {_darknessFallsNextWaypoint + 1}/{waypoints.Length} on this realm's complete 3D route");
        return true;
    }
}

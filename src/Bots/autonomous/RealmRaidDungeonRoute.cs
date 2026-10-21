using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>One bounded route planner per expedition, not one full-world scan per member.</summary>
    public sealed class RealmRaidDungeonRoute
    {
        private readonly ushort _region;
        private readonly string[] _finalTypes;
        private readonly Vector3 _trigger;
        private readonly string[] _objectives;
        private readonly eRealm _realm;
        // Darkness Falls: certified waypoint chain from the realm's entrance to a named encounter.
        private Vector3[] _chain;
        private int _chainIndex;
        private GameNPC _chainTarget;
        private readonly HashSet<GameNPC> _cleared = new();
        // Named encounters defeated this expedition, by name: several scripted bosses come back as new objects (Lich Lord
        // Ilron, Lich Lord Sanguis, Spindler Broodmother were each fought twice in run 10).
        private readonly HashSet<string> _clearedNames = new(StringComparer.Ordinal);
        private long _routeStartTick;
        /// <summary>
        /// An epic dungeon raid clears every monster for its first hour, then goes for its named encounters and the
        /// final one only (monsters on the way are fought when they attack). With trash coming back the list never
        /// emptied: Caer Sidi's raid killed Soul Reckoner, Host and Dorinakka and then cleared custodians and crypt
        /// knights for two hours, never reaching Apocalypse (run 10, 2026-10-15).
        /// </summary>
        public const long EpicTrashPhaseMilliseconds = 60 * 60_000;

        // Run 14 (2026-10-16): Galladoria's Corrupt Primal of Earth took no damage for five minutes from 30 raiders and was
        // skipped: its four Earthmenders (script-spawned, not named) healed it, and after the first hour the raid only
        // targets named encounters. Adds a script spawns beside the front are part of the fight; they come first.
        public const float EncounterAddRadius = 3_000f;
        // Only while the encounter that spawned it is still alive nearby. Run 17 (2026-10-17): Lich Lord Sanguis's blood
        // mages kept spawning after he died, the raid farmed them at the old front for 40 minutes and never went on to
        // Apocalypse.
        public static bool IsEncounterAdd(GameNPC npc, Vector3 front, IReadOnlyCollection<GameNPC> livingEncounters) =>
            npc.LoadedFromScript && npc is not IGameEpicNpc &&
            Vector3.DistanceSquared(front, new(npc.X, npc.Y, npc.Z)) <= EncounterAddRadius * EncounterAddRadius &&
            livingEncounters.Any(boss => boss.CurrentRegionID == npc.CurrentRegionID && boss.IsWithinRadius(npc, (int)EncounterAddRadius));
        private readonly Dictionary<GameNPC, long> _routeRetry = new();
        private readonly Dictionary<string, GameNPC> _finals = new();
        private GameNPC _target;
        private int _probeCursor;
        private static readonly Logging.Logger log = Logging.LoggerManager.Create(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private long _targetProgressTick, _targetStartTick, _holdSince, _nextHoldLog;
        // Lowest health points seen (not percent: a percent of Galladoria's 125,000-health Corrupt Primal of Fire is 1,250
        // points, and a few raiders wearing him down counted as no damage and he was skipped, run 23).
        private int _targetLowestHealth;
        // Final encounter muster (see MusterForFinal).
        private GameNPC _musterTarget;
        private Vector3 _musterPoint;
        private long _musterSince, _nextMusterLog, _musterEngagedTick;
        private bool _musterEngaged;

        public const long TargetNoProgressMilliseconds = 5 * 60_000;
        public const long BlockedTargetRetryMilliseconds = 15 * 60_000;
        public const long HoldReportMilliseconds = 10 * 60_000;
        public const long BlockedEndMilliseconds = 20 * 60_000;
        private long _blockedSince;

        /// <summary>
        /// No live encounter reachable from the front or from any party for 20 minutes.
        /// Tuscaran (Oct 3-4) held 300 bots on "Checking blocked room approaches" for over
        /// six hours after the raid dropped to a lower level it could not climb back from.
        /// </summary>
        public bool Blocked { get; private set; }

        public static bool BlockedTooLong(long blockedSince, long now) =>
            blockedSince > 0 && now - blockedSince >= BlockedEndMilliseconds;

        /// <summary>
        /// A route target nobody has damaged for five minutes is set aside so the
        /// expedition moves on (Caer Sidi and Tuscaran held at their opening area
        /// for 3.5 hours on 2026-10-02). Final bosses are never set aside.
        /// </summary>
        public static bool ShouldSkipTarget(bool finalBoss, long sinceProgressMilliseconds) =>
            !finalBoss && sinceProgressMilliseconds >= TargetNoProgressMilliseconds;
        /// <summary>
        /// The front plus up to <paramref name="max"/> party positions, skipping any within
        /// 150 units across and 64 up/down of one already kept (a party standing 96 units
        /// above the front is a different floor and is kept).
        /// </summary>
        public static Vector3[] DistinctAnchors(Vector3 front, IEnumerable<Vector3> parties, int max)
        {
            var kept = new List<Vector3> { front };
            foreach (Vector3 p in parties)
            {
                if (kept.Count > max) break;
                if (kept.Any(k => Vector2.DistanceSquared(new(k.X, k.Y), new(p.X, p.Y)) <= 150 * 150 &&
                        MathF.Abs(k.Z - p.Z) <= 64))
                    continue;
                kept.Add(p);
            }
            return kept.ToArray();
        }
        public DbZonePoint Entrance { get; }
        public Vector3 Front { get; private set; }
        public Vector3 Destination { get; private set; }
        public string TargetName { get; private set; } = "Encounter staging";
        public int TargetLevel { get; private set; } = 50;
        public bool Hold { get; private set; } = true;
        public bool Complete { get; private set; }
        public string Status { get; private set; } = "Entering dungeon";
        public GameNPC[] FinalBosses => _finals.Values.ToArray();

        // Ordinary flying patrols need not occupy a melee-floor polygon.
        // Never defer a boss, a scripted subclass, or a grounded room guard.
        public const int RangedFlyerReach = 1200;
        public static bool IsOutOfRangedReach(GameNPC.eFlags flags, int monsterZ, int floorZ) =>
            AutonomousPveTargetPolicy.IsUnreachableFlyer(flags, monsterZ, floorZ) && Math.Abs(monsterZ - floorZ) > RangedFlyerReach;

        public static bool CanDeferAirbornePatrol(Type type, GameNPC.eFlags flags) =>
            type == typeof(GameEpicNPC) && (flags & GameNPC.eFlags.FLYING) != 0;

        private RealmRaidDungeonRoute(ushort region, DbZonePoint entrance, Vector3 entry, Vector3 trigger, string[] finals,
            string[] objectives = null, eRealm realm = eRealm.None)
        {
            _region = region; Entrance = entrance; Front = Destination = entry; _trigger = trigger; _finalTypes = finals;
            _objectives = objectives; _realm = realm;
        }

        /// <summary>
        /// The expedition's entrance and its first floor inside. Realm epic dungeons take any
        /// exterior entrance. A neutral expedition (Summoner's Hall, Darkness Falls) takes its own
        /// realm's entrance in its home region, directly or through one dungeon (Summoner's Hall
        /// is entered from Hall of the Corrupt, Dodens Gruva or Marfach Caverns), nearest to the
        /// muster hub first; <see cref="Entrance"/> is then the edge out of the home region, where
        /// the expedition stages.
        /// </summary>
        /// <summary>Why the last TryCreate found no entrance: each candidate and the check it failed (raid start log).</summary>
        public static string LastFailure { get; private set; } = "";

        public static bool TryCreate(ushort region, Vector3 trigger, string[] finals, out RealmRaidDungeonRoute route,
            eRealm realm = eRealm.None, ushort homeRegion = 0, string[] objectives = null, Vector3? hub = null)
        {
            route = null;
            var nav = PathfindingProvider.Instance;
            var why = new List<string>();
            LastFailure = "pathfinding unavailable";
            if (!nav.IsAvailable) return false;
            var crossings = AutonomousWorldBotController.RealmEventCrossings();
            IEnumerable<(DbZonePoint Outer, DbZonePoint Inner)> candidates = homeRegion == 0
                ? crossings.Where(e => e.TargetRegion == region).Select(e => (e, e))
                : RealmRaidNeutralEvents.Entrances(crossings, realm, homeRegion, region)
                    .OrderBy(pair => RealmRaidNeutralEvents.EntrancePreference(realm, pair.Outer.Id))
                    .ThenBy(pair => hub.HasValue ? Vector2.DistanceSquared(new(pair.Outer.SourceX, pair.Outer.SourceY), new(hub.Value.X, hub.Value.Y)) : 0);
            foreach (var (outer, inner) in candidates)
            {
                Zone outside = WorldMgr.GetRegion(outer.SourceRegion)?.GetZone(outer.SourceX, outer.SourceY);
                Zone inside = WorldMgr.GetRegion(region)?.GetZone(inner.TargetX, inner.TargetY);
                string id = $"zp{outer.Id}/{inner.Id}";
                if (outside == null || outside.IsDungeon || inside == null || !nav.HasNavmesh(inside))
                { why.Add($"{id}: {(outside == null ? "no outside zone" : outside.IsDungeon ? "outside is a dungeon" : inside == null ? "no inside zone" : "no inside navmesh")}"); continue; }
                if (homeRegion != 0 && !(region == AutonomousDarknessFallsPolicy.RegionId
                        ? AutonomousDarknessFallsNavigation.IsHomeEntrance(inner)
                        : AutonomousDungeonGoalCatalog.CanUseEntrance(inner, region, (int)trigger.X, (int)trigger.Y)))
                { why.Add($"{id}: not this realm's entrance"); continue; }
                if (homeRegion != 0 && !RealmRaidStaging.TryDungeonPost(nav, outside, new(outer.SourceX, outer.SourceY, outer.SourceZ), 0, [], out _))
                { why.Add($"{id}: no staging post outside"); continue; }
                Vector3 raw = new(inner.TargetX, inner.TargetY, inner.TargetZ);
                Vector3? floor = nav.GetClosestPoint(inside, raw, 48, 48, 64, nav.DefaultFilters);
                if (!floor.HasValue) { why.Add($"{id}: no navmesh floor at the inside arrival point"); continue; }
                if (!AutonomousRendezvousNavigation.HasLocalExit(nav, inside, floor.Value)) { why.Add($"{id}: inside arrival has no local exit"); continue; }
                // A neutral dungeon is too large for one entrance-to-final-boss proof (Darkness
                // Falls); the route proves each named encounter from the raid's positions instead.
                if (homeRegion == 0 && !AutonomousZonePointApproach.TryResolve(nav, inside, floor.Value, trigger, 220, out _))
                { why.Add($"{id}: no approach to the encounter"); continue; }
                route = new(region, outer, floor.Value, trigger, finals, objectives, realm);
                route.ObserveCompletion();
                LastFailure = "";
                return true;
            }
            LastFailure = why.Count == 0 ? "no entrance crossing into the region for this realm" : string.Join("; ", why);
            return false;
        }

        /// <summary>
        /// Moves the front along the chain: the next waypoint once formed parties stand within 300
        /// of the current one. At the end the encounter becomes the ordinary target. False when no
        /// chain is being walked.
        /// </summary>
        private bool AdvanceChain(GameLiving[] inside, long now)
        {
            if (_chain == null) return false;
            if (_chainTarget?.IsAlive != true || _chainTarget.ObjectState != GameObject.eObjectState.Active)
            { _chain = null; _chainTarget = null; return false; }
            while (_chainIndex < _chain.Length - 1 &&
                   inside.Count(p => p.IsWithinRadius(new Point3D((int)_chain[_chainIndex].X, (int)_chain[_chainIndex].Y, (int)_chain[_chainIndex].Z), 300)) >= 8)
                _chainIndex++;
            Front = Destination = _chain[_chainIndex];
            if (_chainIndex == _chain.Length - 1)
            {
                _target = _chainTarget; _chain = null; _chainTarget = null;
                // A fresh start: the chain's target kept the previous target's start, so Prince Ba'alorien
                // inherited Prince Abdin's absence allowance and was skipped at 100% with the raid arriving (2026-10-14).
                _targetProgressTick = _targetStartTick = now; _targetLowestHealth = _target.Health;
                _targetClosest = float.MaxValue; _targetApproachTick = now;
                Hold = false; Status = "Clearing " + _target.Name;
                return true;
            }
            Hold = false;
            Status = $"Advancing to {TargetName} (waypoint {_chainIndex + 1}/{_chain.Length})";
            return true;
        }

        /// <summary>
        /// A Darkness Falls encounter reached through a certified ordinary-spawn route: the proofs give,
        /// per realm, a chain of proven legs from the realm's entrance to a spawn's approach; the
        /// encounter must then be a short proven leg from that approach (nearest proofs first).
        /// </summary>
        private bool TryDarknessFallsChain(IPathfindingMgr nav, Zone zone, Vector3 encounter, out Vector3[] chain) =>
            TryDarknessFallsChain(nav, zone, _realm, encounter, out chain);

        /// <summary>The same certified chain for one realm's member (late raid members walk it to their post).</summary>
        public static bool TryDarknessFallsChain(IPathfindingMgr nav, Zone zone, eRealm realm, Vector3 encounter, out Vector3[] chain)
        {
            // The answer depends on the realm, the post and the mesh only, never on the asking bot. Every
            // raider whose post was not one leg away re-ran the full search (8 approaches x ~38 candidate
            // points, ~300 path queries, ~1 s) on every AI turn; the Prince Abdin raid on 2026-10-12 made
            // 577 such turns. Failures are remembered too: they repeat until the post moves.
            if (nav is not LocalPathfindingMgr || zone == null)
                return SearchDarknessFallsChain(nav, zone, realm, encounter, out chain);
            long now = GameLoop.GameLoopTime;
            var key = (realm, zone.ID, (int)encounter.X >> 6, (int)encounter.Y >> 6, (int)encounter.Z >> 6, NavigationGeometryRevision.Read(zone));
            if (DarknessFallsChains.TryGetValue(key, out var known) && now < known.Until)
            {
                chain = known.Chain;
                return chain != null;
            }
            // A failure holds for every post near it. The raid front moves a few dozen units at a time, so the exact
            // post rarely repeats: on 2026-10-17 every Prince Abdin raider re-ran the failing ~300-query search
            // (1-5 s) on each AI turn and the server lagged.
            var area = (realm, zone.ID, (int)encounter.X >> DarknessFallsChainFailureCellShift, (int)encounter.Y >> DarknessFallsChainFailureCellShift,
                (int)encounter.Z >> DarknessFallsChainFailureCellShift, key.Item6);
            if (DarknessFallsChainFailures.TryGetValue(area, out long failedUntil) && now < failedUntil)
            {
                chain = null;
                return false;
            }
            bool found = SearchDarknessFallsChain(nav, zone, realm, encounter, out chain);
            if (DarknessFallsChains.Count > 4_096) DarknessFallsChains.Clear();
            DarknessFallsChains[key] = (found ? chain : null, now + (found ? DarknessFallsChainMemoryMilliseconds : DarknessFallsChainFailureMemoryMilliseconds));
            if (!found)
            {
                if (DarknessFallsChainFailures.Count > 4_096) DarknessFallsChainFailures.Clear();
                DarknessFallsChainFailures[area] = now + DarknessFallsChainFailureMemoryMilliseconds;
            }
            return found;
        }

        public const long DarknessFallsChainMemoryMilliseconds = 120_000;
        public const long DarknessFallsChainFailureMemoryMilliseconds = 60_000;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(eRealm, ushort, int, int, int, long), (Vector3[] Chain, long Until)>
            DarknessFallsChains = new();
        /// <summary>Failures are shared by posts in the same 512-unit cell.</summary>
        public const int DarknessFallsChainFailureCellShift = 9;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(eRealm, ushort, int, int, int, long), long>
            DarknessFallsChainFailures = new();

        private static bool SearchDarknessFallsChain(IPathfindingMgr nav, Zone zone, eRealm realm, Vector3 encounter, out Vector3[] chain)
        {
            chain = null;
            var _realm = realm;
            if (_realm == eRealm.None) return false;
            var nearest = AutonomousDarknessFallsNavigation.SnapshotCertifiedProofs()
                .Where(proof => proof.Approach is { Length: 3 } && proof.Routes != null)
                .Select(proof => (Proof: proof, Approach: new Vector3(proof.Approach[0], proof.Approach[1], proof.Approach[2]),
                    Route: proof.Routes.FirstOrDefault(route => route.Realm == _realm && route.InWaypoints is { Length: >= 2 })))
                .Where(item => item.Route != null && Vector3.DistanceSquared(item.Approach, encounter) <=
                    DarknessFallsChainReach * DarknessFallsChainReach)
                .OrderBy(item => Vector3.DistanceSquared(item.Approach, encounter))
                .Take(8);
            foreach (var (_, approach, route) in nearest)
            {
                if (!AutonomousZonePointApproach.TryResolve(nav, zone, approach, encounter, 220, out Vector3 final)) continue;
                chain = route.InWaypoints.Select(point => new Vector3(point[0], point[1], point[2])).Append(final).ToArray();
                return true;
            }
            return false;
        }

        /// <summary>
        /// The furthest chain waypoint (final approach first) that a party leader inside the dungeon has a
        /// complete corridor to; -1 when none of them can reach any waypoint.
        /// </summary>
        private int ReachableChainStart(IPathfindingMgr nav, Vector3[] chain, GameLiving[] inside)
        {
            // Where the parties stand, not the front: a stale front can sit on the unreachable waypoint itself.
            Vector3[] anchors = DistinctAnchors(Front, inside
                .Where(p => p.Group == null || p.Group.LivingLeader == p)
                .Select(p => new Vector3(p.X, p.Y, p.Z)), 3);
            Vector3[] leaders = anchors.Length > 1 ? anchors.Skip(1).ToArray() : anchors;
            for (int i = chain.Length - 1; i >= 0; i--)
            foreach (Vector3 from in leaders)
            {
                Zone zone = WorldMgr.GetRegion(_region)?.GetZone((int)from.X, (int)from.Y);
                if (zone != null && nav.HasNavmesh(zone) &&
                    AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, from, chain[i]))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Raiders a final encounter waits for before it is pulled: half of the living raid, at least
        /// <see cref="MinimumRaidersAtTarget"/> and at most 150; after 20 minutes of mustering a third will do.
        /// </summary>
        public static int FinalMusterNeeded(int livingRaiders, long musterMilliseconds) =>
            Math.Max(MinimumRaidersAtTarget, Math.Min(FinalMusterMaximum,
                (int)(livingRaiders * (musterMilliseconds >= FinalMusterPatienceMilliseconds ? 0.33 : 0.5))));
        public const int FinalMusterMaximum = 150;
        public const long FinalMusterPatienceMilliseconds = 20 * 60_000;
        public const int FinalMusterRadius = 1_500;
        public const float FinalMusterStandOff = 1_400f;
        public const long FinalEngageGraceMilliseconds = 2 * 60_000;

        /// <summary>
        /// Darkness Falls run 2026-10-13: the Albion raid reached Legion once eight raiders stood at the last
        /// waypoint and was then pulled in by twos and threes as the rest arrived; it wiped, released to Camelot
        /// and Legion reset to full health a minute after the last raider fell. A final encounter not yet in a
        /// fight now waits at a muster point on the walked approach, outside its aggro range, until enough of
        /// the raid stands there (<see cref="FinalMusterNeeded"/>); then the whole muster goes in together. After a
        /// wipe the boss leaves combat and the raid musters again. True while the raid is holding.
        /// </summary>
        private bool MusterForFinal(GameLiving[] participants, long now)
        {
            if (_musterTarget != _target)
            {
                _musterTarget = _target;
                _musterSince = now;
                _musterEngaged = false;
                _musterPoint = MusterPoint(participants);
            }
            if (_target.InCombat)
                return false; // a fight in progress is never interrupted
            // The raid needs time to cross from the muster point to the boss (run 5: the engage repeated every ten
            // seconds while Govannon was still out of combat); only after that is "out of combat" a wipe.
            if (_musterEngaged && now - _musterEngagedTick < FinalEngageGraceMilliseconds)
                return false;
            if (_musterEngaged)
            {
                // Out of combat after the pull: wiped or broke off. Muster again from scratch.
                _musterEngaged = false;
                _musterSince = now;
            }
            int living = participants.Count(p => p.IsAlive);
            int needed = FinalMusterNeeded(living, now - _musterSince);
            var point = new Point3D((int)_musterPoint.X, (int)_musterPoint.Y, (int)_musterPoint.Z);
            int mustered = participants.Count(p => p.IsAlive && p.CurrentRegionID == _region &&
                                                   (p.IsWithinRadius(point, FinalMusterRadius) || p.IsWithinRadius(_target, TargetPresenceRadius)));
            if (mustered >= needed)
            {
                _musterEngaged = true;
                _musterEngagedTick = now;
                Destination = Front;
                log.Info($"REALM_EXPEDITION_FINAL_ENGAGE region={_region} target=\"{_target.Name}\" mustered={mustered} needed={needed} " +
                         $"living={living} minutes={(now - _musterSince) / 60_000.0:0.0}");
                return false;
            }
            Destination = _musterPoint;
            Hold = true;
            Status = $"Mustering for {_target.Name} ({mustered}/{needed})";
            // Waiting for the raid is not a stalled fight.
            _targetProgressTick = now;
            if (now >= _nextMusterLog)
            {
                _nextMusterLog = now + 2 * 60_000;
                log.Info($"REALM_EXPEDITION_FINAL_MUSTER region={_region} target=\"{_target.Name}\" mustered={mustered} needed={needed} " +
                         $"living={living} at={(int)_musterPoint.X},{(int)_musterPoint.Y},{(int)_musterPoint.Z} minutes={(now - _musterSince) / 60_000.0:0.0}");
            }
            return true;
        }

        /// <summary>
        /// The point on the walked path from the raid's main body to the encounter's approach that lies
        /// <see cref="FinalMusterStandOff"/> from the encounter (or the main body itself when it is closer).
        /// </summary>
        private Vector3 MusterPoint(GameLiving[] participants)
        {
            var nav = PathfindingProvider.Instance;
            Vector3 boss = new(_target.X, _target.Y, _target.Z);
            GameLiving[] inside = participants.Where(p => p.IsAlive && p.CurrentRegionID == _region).ToArray();
            Zone zone = WorldMgr.GetRegion(_region)?.GetZone((int)Front.X, (int)Front.Y);
            if (!TryRegroupFront(inside, out Vector3 body) || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
                return Front;
            if (Vector3.Distance(body, boss) <= FinalMusterStandOff + 200)
                return body;
            // Searched from the encounter's approach toward the raid, so the path always starts at the encounter even
            // when it is too long to finish. From the raid's side a partial path ended wherever the search gave up:
            // Legion's muster in Darkness Falls was placed 19,000 units from him and nobody ever reached it (run 9).
            Span<WrappedPathfindingNode> nodes = stackalloc WrappedPathfindingNode[256];
            PathfindingResult path = nav.GetPathStraight(zone, Front, body, nav.DefaultFilters, nodes);
            if (path.Status == PathfindingStatus.PathFound || path.Status == PathfindingStatus.PartialPathFound)
            {
                for (int i = 1; i < path.NodeCount; i++)
                {
                    Vector3 a = nodes[i - 1].Position, b = nodes[i].Position;
                    if (Vector3.Distance(b, boss) < FinalMusterStandOff) continue;
                    // a inside the stand-off circle, b outside: the crossing point on a-b.
                    float lo = 0, hi = 1;
                    for (int step = 0; step < 12; step++)
                    {
                        float mid = (lo + hi) / 2;
                        if (Vector3.Distance(Vector3.Lerp(a, b, mid), boss) >= FinalMusterStandOff) hi = mid; else lo = mid;
                    }
                    Vector3? floor = nav.GetClosestPoint(zone, Vector3.Lerp(a, b, hi), 64, 64, 128, nav.DefaultFilters);
                    return floor ?? b;
                }
                if (path.NodeCount > 0) return nodes[path.NodeCount - 1].Position;
            }
            return Front;
        }

        public const long CallTargetAfterMilliseconds = 90_000;
        public const long FrontRegroupMilliseconds = 3 * 60_000;
        private long _frontAbsentSince;
        // The raiders' closest approach to an unreached front: while they keep closing in, the front is not given up.
        private float _frontClosest = float.MaxValue;
        private Vector3 _frontClosestFor;

        /// <summary>The living raider with the most living raiders within 1,000 units (the raid's main body).</summary>
        private static bool TryRegroupFront(GameLiving[] inside, out Vector3 front)
        {
            front = default;
            GameLiving best = null;
            int bestCount = 0;
            foreach (GameLiving candidate in inside.Where(p => p.Group == null || p.Group.LivingLeader == p))
            {
                int count = inside.Count(p => p.IsWithinRadius(candidate, 1000));
                if (count > bestCount) { best = candidate; bestCount = count; }
            }
            if (best == null || bestCount < MinimumRaidersAtTarget) return false;
            front = new(best.X, best.Y, best.Z);
            return true;
        }

        // The certified file holds ordinary spawns only; the nearest Albion approach is 6,100 units
        // from High Lord Saeor, 5,500 from Oro, 3,000 from Ba'alorien, and Nahemah's 199-unit one is a
        // floor above. With a 2,500 reach late raiders found no chain, failed their direct path and were
        // pocket-escaped out of Darkness Falls (run 12). The last leg is still a complete-corridor check.
        // (Nahemah's spawn moved off her unconnected dais onto the floor in front of it on 2026-10-13.)
        public const float DarknessFallsChainReach = 8_000f;
        public const int TargetPresenceRadius = 2_000;
        public const int MinimumRaidersAtTarget = 8;
        public const long TargetAbsencePauseLimitMilliseconds = 20 * 60_000;
        public const float TargetApproachStepUnits = 300f;
        private int _healthSample = -1;
        private GameLiving _healthSampleTarget;
        private long _nextHealLog;

        /// <summary>
        /// REALM_EXPEDITION_TARGET_HEALED: the raid's target got 5% or more of its health back since the last pulse, with
        /// what the fight looked like then. Hurionthex (Galladoria) went 76% -> 96% in half a minute with 270 raiders
        /// beside him, and none of the known resets explained it (2026-10-15).
        /// </summary>
        private void ReportHealthJump(GameLiving[] participants, long now)
        {
            int health = _target.HealthPercent;
            if (_healthSampleTarget != _target) { _healthSampleTarget = _target; _healthSample = health; return; }
            int before = _healthSample;
            _healthSample = health;
            if (health - before < 5 || now < _nextHealLog) return;
            _nextHealLog = now + 10_000;
            int reach = participants.Count(p => p.IsAlive && p.TargetObject == _target && p.IsWithinRadius(_target, 600));
            int targeting = participants.Count(p => p.IsAlive && p.TargetObject == _target);
            var npc = _target as GameNPC;
            string effects = string.Join(",", _target.effectListComponent.GetEffects().Select(e => e.SpellHandler?.Spell?.Name ?? e.EffectType.ToString()).Distinct().Take(8));
            log.Warn($"REALM_EXPEDITION_TARGET_HEALED region={_region} target=\"{_target.Name}\" from={before} to={health} inCombat={_target.InCombat} " +
                     $"lastHitAgoMs={(_target.LastAttackedByEnemyTick > 0 ? now - _target.LastAttackedByEnemyTick : -1)} " +
                     $"aggro={(npc?.Brain as DOL.AI.Brain.StandardMobBrain)?.AggroCount ?? -1} targeting={targeting} inReach={reach} " +
                     $"returning={npc?.IsReturningToSpawnPoint} fromSpawn={(npc == null ? -1 : (int)npc.GetDistanceTo(npc.SpawnPoint))} " +
                     $"model={npc?.Model} effects=\"{effects}\"");
        }
        public const long TargetApproachStallMilliseconds = 3 * 60_000;
        private float _targetClosest = float.MaxValue;
        private long _targetApproachTick;
        private long _nextTargetCall;
        public const long BusyCallAfterMilliseconds = 3 * 60_000;
        public const int ThinAttackTargeting = 8;

        /// <summary>
        /// An encounter's hazard, not a monster to clear: Galladoria's Watery Vortexes float over the water primal's pool,
        /// are out of reach, come back every six minutes and hit hard. Taken as the primal's adds, they were the raid's
        /// target over and over; three were skipped for no damage and the raid fell from 230 inside to 28 (run 22).
        /// </summary>
        private static bool IsEncounterHazard(GameNPC npc) =>
            npc.Brain?.GetType().Name is "VortexBrain" or "WaterfallAntipassBrain";
        public const int ThinAttackNear = 16;

        /// <summary>
        /// The raid calls its target: when the encounter has taken no damage for 90 s although raid
        /// members stand near it (Summoner Lossren and Council Nokkvi skipped at 100%, 2026-10-06),
        /// idle living members within 1,500 units are ordered to attack it, at most every 30 s.
        /// Ordinary combat, healing and group defense take it from there.
        /// </summary>
        private void CallTarget(GameLiving[] participants, long now)
        {
            if (_target == null || now < _nextTargetCall) return;
            // A target losing health slowly to a handful while the raid stands round it is called too: Galladoria's
            // Guardian Earthmender (60,000 health, stationary) went from 100% to 73% in 11 minutes with 20-190 raiders
            // within 1,000 and 1-7 of them on it, and the no-damage call never came (run 22). Idle raiders only.
            bool thin = false;
            if (now - _targetProgressTick < CallTargetAfterMilliseconds)
            {
                int onIt = 0, around = 0;
                foreach (GameLiving p in participants)
                {
                    if (!p.IsAlive || p.CurrentRegionID != _target.CurrentRegionID) continue;
                    if (p.TargetObject == _target) onIt++;
                    if (p.IsWithinRadius(_target, 1500)) around++;
                }
                thin = onIt < ThinAttackTargeting && around >= ThinAttackNear;
                if (!thin) return;
            }
            _nextTargetCall = now + 30_000;
            int called = 0;
            // After three idle minutes the call also takes raiders already fighting the encounter's trash: in
            // Darkness Falls the succubi and ambassadors around Princess Nahemah kept every one of 65 raiders
            // busy for five minutes and she was skipped at 100% (run 17). Some of the fighters switch; the
            // rest keep the adds off them.
            bool takeBusy = now - _targetProgressTick >= BusyCallAfterMilliseconds;
            int busyLeft = takeBusy ? Math.Max(MinimumRaidersAtTarget, participants.Count(p => p.IsAlive && p.IsWithinRadius(_target, 1500)) / 3) : 0;
            foreach (GameLiving member in participants)
            {
                if (member is not GameBot bot || !bot.IsAlive || bot.CurrentRegionID != _target.CurrentRegionID ||
                    !bot.IsWithinRadius(_target, 1500) || bot.TargetObject == _target ||
                    bot.Brain is not DOL.AI.Brain.BotBrain brain || !GameServer.ServerRules.IsAllowedToAttack(bot, _target, true))
                    continue;
                if (bot.InCombat || bot.IsAttacking)
                {
                    if (busyLeft <= 0) continue;
                    busyLeft--;
                }
                bot.TargetObject = _target;
                brain.AddToAggroList(_target, bot.InCombat || bot.IsAttacking ? 10_000 : 1);
                brain.FSM.SetCurrentState(DOL.GS.eFSMStateType.AGGRO);
                brain.NextThinkTick = GameLoop.GameLoopTime; // act on the call now, not at the next idle think
                called++;
            }
            if (called > 0)
                log.Info($"REALM_EXPEDITION_TARGET_CALLED region={_region} target=\"{_target.Name}\" health={_target.HealthPercent} called={called} " +
                         $"idleSeconds={(now - _targetProgressTick) / 1000}");
        }

        public void ObserveCompletion()
        {
            foreach (var npc in WorldMgr.GetRegion(_region)?.Objects.OfType<GameNPC>() ?? [])
                if (_finalTypes.Contains(npc.GetType().Name) && (!_finals.TryGetValue(npc.GetType().Name, out var old) || old.IsAlive))
                    _finals[npc.GetType().Name] = npc;
            if (_finalTypes.All(type => _finals.TryGetValue(type, out var boss) && !boss.IsAlive))
            { Complete = true; Status = "Final encounter defeated"; }
        }

        // Run 21 (2026-10-17): 70+ raiders fought Spindler Broodmother for over 30 minutes and nothing showed how she
        // fared. Once a minute: the current target's health, whether it is in combat, and the raiders around it.
        private long _nextTargetStatusLog;

        public void Advance(GameLiving[] participants, long now)
        {
            AdvanceCore(participants, now);
            GameNPC target = _target;
            if (target != null && target.IsAlive && now >= _nextTargetStatusLog)
            {
                _nextTargetStatusLog = now + 60_000;
                int near = participants.Count(p => p.IsAlive && p.CurrentRegionID == target.CurrentRegionID && p.IsWithinRadius(target, 1_000));
                int onIt = participants.Count(p => p.IsAlive && p.TargetObject == target);
                log.Info($"REALM_EXPEDITION_TARGET_STATUS region={_region} target=\"{target.Name}\" level={target.Level} " +
                         $"health={target.HealthPercent}% inCombat={target.InCombat} raidersWithin1000={near} targeting={onIt} " +
                         $"at={target.X},{target.Y},{target.Z} spawnDistance={(int)target.GetDistanceTo(target.SpawnPoint)}");
            }
            if (!Hold || Complete) { _holdSince = 0; return; }
            if (_holdSince == 0) { _holdSince = now; return; }
            if (now - _holdSince < HoldReportMilliseconds || now < _nextHoldLog) return;
            _nextHoldLog = now + HoldReportMilliseconds;
            int inside = participants.Count(p => p.IsAlive && p.CurrentRegionID == _region);
            log.Warn($"REALM_EXPEDITION_HOLD region={_region} minutes={(now - _holdSince) / 60_000} status=\"{Status}\" " +
                     $"front={(int)Front.X},{(int)Front.Y},{(int)Front.Z} target=\"{TargetName}\" inside={inside}");
        }

        private void AdvanceCore(GameLiving[] participants, long now)
        {
            if (Complete) return;
            if (_routeStartTick == 0) _routeStartTick = now;
            GameNPC[] live = WorldMgr.GetRegion(_region)?.Objects.OfType<GameNPC>().ToArray() ?? [];
            foreach (GameNPC stale in _routeRetry.Keys.Where(n => !n.IsAlive || n.ObjectState != GameObject.eObjectState.Active).ToArray())
                _routeRetry.Remove(stale);
            foreach (GameNPC npc in live.Where(n => _finalTypes.Contains(n.GetType().Name)))
                if (!_finals.TryGetValue(npc.GetType().Name, out var old) || old.IsAlive) _finals[npc.GetType().Name] = npc;
            if (_target != null && !_target.IsAlive)
            {
                log.Info($"REALM_EXPEDITION_TARGET_DEFEATED region={_region} target=\"{_target.Name}\" level={_target.Level} " +
                         $"minutes={(now - _targetStartTick) / 60_000.0:0.0}");
                _cleared.Add(_target);
                if (_target is IGameEpicNpc) _clearedNames.Add(_target.Name);
                _target = null;
            }
            bool finalDeaths = _finalTypes.All(type => _finals.TryGetValue(type, out var boss) && !boss.IsAlive);
            if (finalDeaths) { Complete = true; Status = "Final encounter defeated"; return; }
            if (_target?.IsAlive == true && _target.ObjectState == GameObject.eObjectState.Active)
            {
                if (_target.Health < _targetLowestHealth) { _targetLowestHealth = _target.Health; _targetProgressTick = now; }
                ReportHealthJump(participants, now);
                // The no-damage clock runs only while the raid is actually at its target. A raid
                // wiped or regrouping elsewhere skipped Prince Ba'alorien and Princess Nahemah at
                // 100% (run 11). Paused at most 20 minutes per target, so an unreachable one still goes.
                int near = participants.Count(p => p.IsAlive && p.CurrentRegionID == _region &&
                                                   p.IsWithinRadius(_target, TargetPresenceRadius));
                // Still closing in also pauses it: a raid walking a long way to its target is not stalled.
                float closest = participants.Where(p => p.IsAlive && p.CurrentRegionID == _region)
                    .Select(p => (float)p.GetDistanceTo(_target)).DefaultIfEmpty(float.MaxValue).Min();
                if (closest < _targetClosest - TargetApproachStepUnits) { _targetClosest = closest; _targetApproachTick = now; }
                bool approaching = now - _targetApproachTick < TargetApproachStallMilliseconds;
                if (near < MinimumRaidersAtTarget && (now - _targetStartTick < TargetAbsencePauseLimitMilliseconds || approaching))
                    _targetProgressTick = Math.Max(_targetProgressTick, now - 1_000);
                // A tethered boss outside its tether is immune while the raid leads it home: not a stall.
                if (_target is GameNPC tethered && AutonomousTetherReset.IsOutOfTether(tethered))
                {
                    _targetProgressTick = Math.Max(_targetProgressTick, now - 1_000);
                    Hold = false; Status = $"Drawing {_target.Name} back to its lair ({_target.HealthPercent}%)"; return;
                }
                // Never switch a live grind objective because another party has not
                // arrived, but never hold the whole expedition on a target nobody damages.
                if (_finalTypes.Contains(_target.GetType().Name) && MusterForFinal(participants, now))
                    return;
                if (!ShouldSkipTarget(_finalTypes.Contains(_target.GetType().Name), now - _targetProgressTick))
                {
                    CallTarget(participants, now);
                    Hold = false; Status = $"Clearing {_target.Name} ({_target.HealthPercent}%)"; return;
                }
                log.Warn($"REALM_EXPEDITION_TARGET_SKIPPED region={_region} target=\"{_target.Name}\" at={_target.X},{_target.Y},{_target.Z} " +
                         $"health={_target.HealthPercent} front={(int)Front.X},{(int)Front.Y},{(int)Front.Z} near={near} reason=\"no damage for five minutes\"");
                _routeRetry[_target] = now + BlockedTargetRetryMilliseconds;
            }
            _target = null;
            GameLiving[] inside = participants.Where(p => p.IsAlive && p.CurrentRegionID == _region).ToArray();
            if (AdvanceChain(inside, now)) return;
            bool atFront = inside.Any(p => p.IsWithinRadius(new Point3D((int)Front.X, (int)Front.Y, (int)Front.Z), 1000));
            if (inside.Length >= 8 && !atFront)
            {
                // The front can be left on a spot nobody reaches: Tuscaran Glacier's griffon icewing
                // was skipped and its approach stayed the front, so 268 raiders inside "waited for
                // formed parties" for eight hours (2026-10-12). After a few minutes the front moves to
                // where most raiders stand, and the next encounter is searched from there.
                if (_frontAbsentSince == 0) _frontAbsentSince = now;
                // A long climb is not a stall: Caer Sidi's final staging is three proven path legs up from the raid, and
                // the front was moved back down after three minutes with the raid still on its way (run 22).
                if (_frontClosestFor != Front) { _frontClosestFor = Front; _frontClosest = float.MaxValue; }
                float closestToFront = float.MaxValue;
                foreach (GameLiving p in inside)
                    closestToFront = Math.Min(closestToFront, Vector3.Distance(new(p.X, p.Y, p.Z), Front));
                if (closestToFront < _frontClosest - TargetApproachStepUnits)
                {
                    _frontClosest = closestToFront;
                    _frontAbsentSince = now;
                }
                if (now - _frontAbsentSince >= FrontRegroupMilliseconds && TryRegroupFront(inside, out Vector3 regrouped))
                {
                    log.Warn($"REALM_EXPEDITION_FRONT_REGROUP region={_region} from={(int)Front.X},{(int)Front.Y},{(int)Front.Z} " +
                             $"to={(int)regrouped.X},{(int)regrouped.Y},{(int)regrouped.Z} inside={inside.Length} " +
                             $"reason=\"nobody reached the front for {(now - _frontAbsentSince) / 60_000} minutes\"");
                    Front = Destination = regrouped;
                    _frontAbsentSince = 0;
                    atFront = true;
                }
            }
            else _frontAbsentSince = 0;
            if (inside.Length < 8 || !atFront)
            { Hold = true; Destination = Front; Status = "Waiting for formed parties inside"; return; }
            var nav = PathfindingProvider.Instance;
            Zone zone = WorldMgr.GetRegion(_region)?.GetZone((int)Front.X, (int)Front.Y);
            if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
            { Hold = true; Status = "Blocked: dungeon navigation unavailable"; return; }
            GameNPC[] encounters = live.Where(n => n is IGameEpicNpc && n.IsAlive && n.ObjectState == GameObject.eObjectState.Active).ToArray();
            GameNPC[] candidates = live.Where(n => n.IsAlive && n.ObjectState == GameObject.eObjectState.Active && n.Realm == eRealm.None &&
                n.Level > 0 && n is not GameBot && n is not GameSummonedPet && n.Brain is not IControlledBrain &&
                n is not GameMerchant && n is not GameTrainer && n is not GameTeleporter && n is not GameTaxi &&
                (n.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) == 0 && !_cleared.Contains(n) &&
                // A pet class's charm body (a copy of an overland monster it is charming) is no dungeon monster: a
                // Hibernian raid stood four minutes by a "grogan" in Galladoria that none of it could attack (run 22).
                n.TempProperties.GetProperty<bool>(AutonomousPetSupport.SyntheticCharmPetProperty) != true &&
                !IsEncounterHazard(n) &&
                !(n is IGameEpicNpc && _clearedNames.Contains(n.Name) && !_finalTypes.Contains(n.GetType().Name)) &&
                // A neutral expedition goes for its named encounters only; monsters on the way are
                // fought when they attack (Darkness Falls holds 2,465 spawns).
                (_objectives == null ? now - _routeStartTick < EpicTrashPhaseMilliseconds || n is IGameEpicNpc || _finalTypes.Contains(n.GetType().Name) ||
                        IsEncounterAdd(n, Front, encounters)
                    : RealmRaidNeutralEvents.IsObjective(_objectives, n.Name)))
                .OrderBy(n => _finalTypes.Contains(n.GetType().Name) ? 1 : 0)
                .ThenBy(n => IsEncounterAdd(n, Front, encounters) ? 0 : 1)
                .ThenBy(n => Vector3.DistanceSquared(Front, new(n.X, n.Y, n.Z))).ToArray();
            // Probe from the front and from where the parties actually stand, near or far:
            // Tuscaran's front once sat on a pad under the floor its parties stood on.
            Vector3[] anchors = DistinctAnchors(Front, inside
                    .Where(p => p.Group == null || p.Group.LivingLeader == p)
                    .Select(p => new Vector3(p.X, p.Y, p.Z)), 6);
            int probes = 0;
            // Resume after the last examined candidate. With more blocked
            // spawns than the per-minute probe budget, always starting at zero
            // would retry the same expired failures and starve later rooms.
            for (int examined = 0; examined < candidates.Length && probes < 3; examined++)
            {
                GameNPC npc = candidates[_probeCursor % candidates.Length];
                _probeCursor = (_probeCursor + 1) % candidates.Length;
                if (_routeRetry.GetValueOrDefault(npc) > now) continue;
                probes++;
                Vector3 raw = AutonomousDungeonGoalCatalog.TryGet(npc, out var point) ? point.Position : new(npc.X, npc.Y, npc.Z);
                // A flyer is approached on the floor under it (the approach search only looks 256 up or down).
                if ((npc.Flags & GameNPC.eFlags.FLYING) != 0 &&
                    nav.GetClosestPoint(zone, raw, 128, 128, RangedFlyerReach, nav.DefaultFilters) is Vector3 below && raw.Z - below.Z > 256)
                    raw = below;
                Vector3 approach = default;
                bool reachable = false;
                foreach (Vector3 anchor in anchors)
                {
                    Zone anchorZone = WorldMgr.GetRegion(_region)?.GetZone((int)anchor.X, (int)anchor.Y) ?? zone;
                    if (nav.HasNavmesh(anchorZone) && AutonomousZonePointApproach.TryResolve(nav, anchorZone, anchor, raw, 220, out approach))
                    { reachable = true; break; }
                }
                if (!reachable && _region == AutonomousDarknessFallsPolicy.RegionId && _objectives != null &&
                    TryDarknessFallsChain(nav, zone, raw, out Vector3[] chain))
                {
                    // Too far for one proven path: walk the certified waypoint chain to it. Run 18: the chain
                    // always began at its first waypoint by the realm entrance; a raid already down on Oro's floor
                    // (one-way drops) could not path back up, and 300 raiders looped on NoPath for half an hour.
                    // Start at the furthest waypoint the parties can reach; with none, set the target aside.
                    int start = ReachableChainStart(nav, chain, inside);
                    if (start < 0)
                    { _routeRetry[npc] = now + 60_000; continue; }
                    _chain = chain; _chainIndex = start; _chainTarget = npc;
                    TargetName = npc.Name; TargetLevel = npc.Level; _probeCursor = 0; _blockedSince = 0;
                    AdvanceChain(inside, now);
                    return;
                }
                if (!reachable)
                { _routeRetry[npc] = now + 60_000; continue; }
                // A flyer hovering far above its floor approach cannot be meleed, but one within spell and bow reach is
                // shot down from below (2026-10-16: Galladoria's Corrupt Primal of Air circles 690 above the floor and never
                // fights; skipped, it never died, and Olcasgean wakes only when all four primals are dead).
                if (IsOutOfRangedReach(npc.Flags, npc.Z, (int)approach.Z))
                { _routeRetry[npc] = now + BlockedTargetRetryMilliseconds; continue; }
                _target = npc; Front = Destination = approach; TargetName = npc.Name; TargetLevel = npc.Level;
                _targetProgressTick = _targetStartTick = now; _targetLowestHealth = npc.Health;
                _targetClosest = float.MaxValue; _targetApproachTick = now;
                _probeCursor = 0; _blockedSince = 0;
                Hold = false; Status = "Clearing " + npc.Name; return;
            }
            Hold = true;
            if (candidates.Any(n => !_routeRetry.ContainsKey(n) || !CanDeferAirbornePatrol(n.GetType(), n.Flags)))
            {
                Status = "Checking blocked room approaches; no false completion";
                if (_blockedSince == 0) _blockedSince = now;
                if (BlockedTooLong(_blockedSince, now) && !Blocked)
                {
                    Blocked = true;
                    log.Warn($"REALM_EXPEDITION_ROUTE_BLOCKED region={_region} front={(int)Front.X},{(int)Front.Y},{(int)Front.Z} " +
                             $"remaining={candidates.Length} nearest=\"{candidates.FirstOrDefault()?.Name}\" at={candidates.FirstOrDefault()?.X},{candidates.FirstOrDefault()?.Y},{candidates.FirstOrDefault()?.Z} " +
                             $"reason=\"no remaining encounter reachable from the front or any party for 20 minutes\"");
                }
                return;
            }
            _blockedSince = 0;
            // The encounter controller must see a real party at its native trigger.
            // Waiting through dialogue/phase timers is not an empty-camp failure.
            if (AutonomousZonePointApproach.TryResolve(nav, zone, Front, _trigger, 220, out Vector3 trigger))
                Front = Destination = trigger;
            TargetName = "Final encounter staging";
            Status = candidates.Length == 0 ? "Waiting for the next scripted encounter phase" :
                $"Waiting for the next scripted encounter phase; {candidates.Length} airborne patrols remain";
        }
    }
}

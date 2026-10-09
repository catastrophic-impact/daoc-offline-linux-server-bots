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
        private readonly Dictionary<GameNPC, long> _routeRetry = new();
        private readonly Dictionary<string, GameNPC> _finals = new();
        private GameNPC _target;
        private int _probeCursor;
        private static readonly Logging.Logger log = Logging.LoggerManager.Create(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private long _targetProgressTick, _targetStartTick, _holdSince, _nextHoldLog;
        private int _targetLowestHealth;

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
        public static bool TryCreate(ushort region, Vector3 trigger, string[] finals, out RealmRaidDungeonRoute route,
            eRealm realm = eRealm.None, ushort homeRegion = 0, string[] objectives = null, Vector3? hub = null)
        {
            route = null;
            var nav = PathfindingProvider.Instance;
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
                if (outside == null || outside.IsDungeon || inside == null || !nav.HasNavmesh(inside)) continue;
                if (homeRegion != 0 && (!(region == AutonomousDarknessFallsPolicy.RegionId
                        ? AutonomousDarknessFallsNavigation.IsHomeEntrance(inner)
                        : AutonomousDungeonGoalCatalog.CanUseEntrance(inner, region, (int)trigger.X, (int)trigger.Y)) ||
                    !RealmRaidStaging.TryDungeonPost(nav, outside, new(outer.SourceX, outer.SourceY, outer.SourceZ), 0, [], out _)))
                    continue;
                Vector3 raw = new(inner.TargetX, inner.TargetY, inner.TargetZ);
                Vector3? floor = nav.GetClosestPoint(inside, raw, 48, 48, 64, nav.DefaultFilters);
                if (!floor.HasValue || !AutonomousRendezvousNavigation.HasLocalExit(nav, inside, floor.Value)) continue;
                // A neutral dungeon is too large for one entrance-to-final-boss proof (Darkness
                // Falls); the route proves each named encounter from the raid's positions instead.
                if (homeRegion == 0 && !AutonomousZonePointApproach.TryResolve(nav, inside, floor.Value, trigger, 220, out _)) continue;
                route = new(region, outer, floor.Value, trigger, finals, objectives, realm);
                route.ObserveCompletion();
                return true;
            }
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
                _targetProgressTick = now; _targetLowestHealth = _target.HealthPercent;
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

        public const long CallTargetAfterMilliseconds = 90_000;
        // The certified file holds ordinary spawns only; the nearest Albion approach is 6,100 units
        // from High Lord Saeor, 5,500 from Oro, 3,000 from Ba'alorien, and Nahemah's 199-unit one is a
        // floor above. With a 2,500 reach late raiders found no chain, failed their direct path and were
        // pocket-escaped out of Darkness Falls (run 12). The last leg is still a complete-corridor check.
        public const float DarknessFallsChainReach = 8_000f;
        public const int TargetPresenceRadius = 2_000;
        public const int MinimumRaidersAtTarget = 8;
        public const long TargetAbsencePauseLimitMilliseconds = 20 * 60_000;
        private long _nextTargetCall;
        public const long BusyCallAfterMilliseconds = 3 * 60_000;

        /// <summary>
        /// The raid calls its target: when the encounter has taken no damage for 90 s although raid
        /// members stand near it (Summoner Lossren and Council Nokkvi skipped at 100%, 2026-10-06),
        /// idle living members within 1,500 units are ordered to attack it, at most every 30 s.
        /// Ordinary combat, healing and group defense take it from there.
        /// </summary>
        private void CallTarget(GameLiving[] participants, long now)
        {
            if (_target == null || now - _targetProgressTick < CallTargetAfterMilliseconds || now < _nextTargetCall) return;
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

        public void Advance(GameLiving[] participants, long now)
        {
            AdvanceCore(participants, now);
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
            GameNPC[] live = WorldMgr.GetRegion(_region)?.Objects.OfType<GameNPC>().ToArray() ?? [];
            foreach (GameNPC stale in _routeRetry.Keys.Where(n => !n.IsAlive || n.ObjectState != GameObject.eObjectState.Active).ToArray())
                _routeRetry.Remove(stale);
            foreach (GameNPC npc in live.Where(n => _finalTypes.Contains(n.GetType().Name)))
                if (!_finals.TryGetValue(npc.GetType().Name, out var old) || old.IsAlive) _finals[npc.GetType().Name] = npc;
            if (_target != null && !_target.IsAlive)
            {
                log.Info($"REALM_EXPEDITION_TARGET_DEFEATED region={_region} target=\"{_target.Name}\" level={_target.Level} " +
                         $"minutes={(now - _targetStartTick) / 60_000.0:0.0}");
                _cleared.Add(_target); _target = null;
            }
            bool finalDeaths = _finalTypes.All(type => _finals.TryGetValue(type, out var boss) && !boss.IsAlive);
            if (finalDeaths) { Complete = true; Status = "Final encounter defeated"; return; }
            if (_target?.IsAlive == true && _target.ObjectState == GameObject.eObjectState.Active)
            {
                if (_target.HealthPercent < _targetLowestHealth) { _targetLowestHealth = _target.HealthPercent; _targetProgressTick = now; }
                // The no-damage clock runs only while the raid is actually at its target. A raid
                // wiped or regrouping elsewhere skipped Prince Ba'alorien and Princess Nahemah at
                // 100% (run 11). Paused at most 20 minutes per target, so an unreachable one still goes.
                int near = participants.Count(p => p.IsAlive && p.CurrentRegionID == _region &&
                                                   p.IsWithinRadius(_target, TargetPresenceRadius));
                if (near < MinimumRaidersAtTarget && now - _targetStartTick < TargetAbsencePauseLimitMilliseconds)
                    _targetProgressTick = Math.Max(_targetProgressTick, now - 1_000);
                // A tethered boss outside its tether is immune while the raid leads it home: not a stall.
                if (_target is GameNPC tethered && AutonomousTetherReset.IsOutOfTether(tethered))
                {
                    _targetProgressTick = Math.Max(_targetProgressTick, now - 1_000);
                    Hold = false; Status = $"Drawing {_target.Name} back to its lair ({_target.HealthPercent}%)"; return;
                }
                // Never switch a live grind objective because another party has not
                // arrived, but never hold the whole expedition on a target nobody damages.
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
            if (inside.Length < 8 || !inside.Any(p => p.IsWithinRadius(new Point3D((int)Front.X, (int)Front.Y, (int)Front.Z), 1000)))
            { Hold = true; Destination = Front; Status = "Waiting for formed parties inside"; return; }
            var nav = PathfindingProvider.Instance;
            Zone zone = WorldMgr.GetRegion(_region)?.GetZone((int)Front.X, (int)Front.Y);
            if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
            { Hold = true; Status = "Blocked: dungeon navigation unavailable"; return; }
            GameNPC[] candidates = live.Where(n => n.IsAlive && n.ObjectState == GameObject.eObjectState.Active && n.Realm == eRealm.None &&
                n.Level > 0 && n is not GameBot && n is not GameSummonedPet && n.Brain is not IControlledBrain &&
                n is not GameMerchant && n is not GameTrainer && n is not GameTeleporter && n is not GameTaxi &&
                (n.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) == 0 && !_cleared.Contains(n) &&
                // A neutral expedition goes for its named encounters only; monsters on the way are
                // fought when they attack (Darkness Falls holds 2,465 spawns).
                (_objectives == null || RealmRaidNeutralEvents.IsObjective(_objectives, n.Name)))
                .OrderBy(n => _finalTypes.Contains(n.GetType().Name) ? 1 : 0)
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
                // A flyer hovering far above its floor approach cannot be meleed.
                if (AutonomousPveTargetPolicy.IsUnreachableFlyer(npc.Flags, npc.Z, (int)approach.Z))
                { _routeRetry[npc] = now + BlockedTargetRetryMilliseconds; continue; }
                _target = npc; Front = Destination = approach; TargetName = npc.Name; TargetLevel = npc.Level;
                _targetProgressTick = _targetStartTick = now; _targetLowestHealth = npc.HealthPercent;
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

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
        private readonly HashSet<GameNPC> _cleared = new();
        private readonly Dictionary<GameNPC, long> _routeRetry = new();
        private readonly Dictionary<string, GameNPC> _finals = new();
        private GameNPC _target;
        private int _probeCursor;
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

        private RealmRaidDungeonRoute(ushort region, DbZonePoint entrance, Vector3 entry, Vector3 trigger, string[] finals)
        {
            _region = region; Entrance = entrance; Front = Destination = entry; _trigger = trigger; _finalTypes = finals;
        }

        public static bool TryCreate(ushort region, Vector3 trigger, string[] finals, out RealmRaidDungeonRoute route)
        {
            route = null;
            var nav = PathfindingProvider.Instance;
            if (!nav.IsAvailable) return false;
            foreach (DbZonePoint edge in AutonomousWorldBotController.RealmEventCrossings().Where(e => e.TargetRegion == region))
            {
                Zone outside = WorldMgr.GetRegion(edge.SourceRegion)?.GetZone(edge.SourceX, edge.SourceY);
                Zone inside = WorldMgr.GetRegion(region)?.GetZone(edge.TargetX, edge.TargetY);
                if (outside == null || outside.IsDungeon || inside == null || !nav.HasNavmesh(inside)) continue;
                Vector3 raw = new(edge.TargetX, edge.TargetY, edge.TargetZ);
                Vector3? floor = nav.GetClosestPoint(inside, raw, 48, 48, 64, nav.DefaultFilters);
                if (!floor.HasValue || !AutonomousRendezvousNavigation.HasLocalExit(nav, inside, floor.Value)) continue;
                if (!AutonomousZonePointApproach.TryResolve(nav, inside, floor.Value, trigger, 220, out _)) continue;
                route = new(region, edge, floor.Value, trigger, finals);
                route.ObserveCompletion();
                return true;
            }
            return false;
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
            if (Complete) return;
            GameNPC[] live = WorldMgr.GetRegion(_region)?.Objects.OfType<GameNPC>().ToArray() ?? [];
            foreach (GameNPC stale in _routeRetry.Keys.Where(n => !n.IsAlive || n.ObjectState != GameObject.eObjectState.Active).ToArray())
                _routeRetry.Remove(stale);
            foreach (GameNPC npc in live.Where(n => _finalTypes.Contains(n.GetType().Name)))
                if (!_finals.TryGetValue(npc.GetType().Name, out var old) || old.IsAlive) _finals[npc.GetType().Name] = npc;
            if (_target != null && !_target.IsAlive) { _cleared.Add(_target); _target = null; }
            bool finalDeaths = _finalTypes.All(type => _finals.TryGetValue(type, out var boss) && !boss.IsAlive);
            if (finalDeaths) { Complete = true; Status = "Final encounter defeated"; return; }
            if (_target?.IsAlive == true && _target.ObjectState == GameObject.eObjectState.Active)
            {
                // Never switch a live grind objective because another party has not arrived.
                Hold = false; Status = "Clearing " + _target.Name; return;
            }
            _target = null;
            GameLiving[] inside = participants.Where(p => p.IsAlive && p.CurrentRegionID == _region).ToArray();
            if (inside.Length < 8 || !inside.Any(p => p.IsWithinRadius(new Point3D((int)Front.X, (int)Front.Y, (int)Front.Z), 1000)))
            { Hold = true; Destination = Front; Status = "Waiting for formed parties inside"; return; }
            var nav = PathfindingProvider.Instance;
            Zone zone = WorldMgr.GetRegion(_region)?.GetZone((int)Front.X, (int)Front.Y);
            if (zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
            { Hold = true; Status = "Blocked: dungeon navigation unavailable"; return; }
            GameNPC[] candidates = live.Where(n => n.IsAlive && n.ObjectState == GameObject.eObjectState.Active && n.Realm == eRealm.None &&
                n.Level > 0 && n is not GameBot && n is not GameSummonedPet && n.Brain is not IControlledBrain &&
                n is not GameMerchant && n is not GameTrainer && n is not GameTeleporter && n is not GameTaxi &&
                (n.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) == 0 && !_cleared.Contains(n))
                .OrderBy(n => _finalTypes.Contains(n.GetType().Name) ? 1 : 0)
                .ThenBy(n => Vector3.DistanceSquared(Front, new(n.X, n.Y, n.Z))).ToArray();
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
                if (!AutonomousZonePointApproach.TryResolve(nav, zone, Front, raw, 220, out Vector3 approach))
                { _routeRetry[npc] = now + 60_000; continue; }
                _target = npc; Front = Destination = approach; TargetName = npc.Name; TargetLevel = npc.Level;
                _probeCursor = 0;
                Hold = false; Status = "Clearing " + npc.Name; return;
            }
            Hold = true;
            if (candidates.Any(n => !_routeRetry.ContainsKey(n) || !CanDeferAirbornePatrol(n.GetType(), n.Flags)))
            { Status = "Checking blocked room approaches; no false completion"; return; }
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

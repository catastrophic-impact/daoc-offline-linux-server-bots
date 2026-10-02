using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;
using DOL.GS;

namespace DOL.GS
{
    /// <summary>Total summons, not concurrent living adds. Deaths never replenish the budget.</summary>
    public sealed class DragonAddBudget
    {
        public const int Maximum = 6;
        public const long MinimumSpacing = 20_000;
        public int Spawned { get; private set; }
        private long _next;
        public bool Due(long now, int healthPercent) => Spawned < Maximum && now >= _next && healthPercent <= 85 - Spawned * 15;
        public void Commit(long now) { Spawned++; _next = now + MinimumSpacing; }
        public void Reset() { Spawned = 0; _next = 0; }
    }

    /// <summary>One per dragon brain. No repeating timers and at most six owned NPCs.</summary>
    public sealed class DragonEncounterAdds
    {
        private readonly DragonAddBudget _budget = new();
        private readonly List<GameNPC> _adds = new(DragonAddBudget.Maximum);
        private long _nextAttempt;

        public void Reset()
        {
            foreach (var add in _adds)
                if (add.ObjectState == GameObject.eObjectState.Active) add.Delete();
            _adds.Clear();
            _budget.Reset();
            _nextAttempt = 0;
        }

        public void Tick(GameNPC dragon, IEnumerable<GameLiving> enemies, bool engaged, bool airborne)
        {
            if (!dragon.IsAlive || dragon.ObjectState != GameObject.eObjectState.Active)
            { Reset(); return; }
            // A momentary target change must not replenish the six-add budget.
            if (!engaged)
            {
                if (dragon.HealthPercent == 100 && !dragon.InCombatInLast(30_000)) Reset();
                return;
            }
            long now = GameLoop.GameLoopTime;
            if (airborne || now < _nextAttempt || !_budget.Due(now, dragon.HealthPercent)) return;
            _nextAttempt = now + 5_000; // Failed geometry/target checks are bounded, not retried every tick.
            var targets = enemies.Where(t => ValidTarget(dragon, t) && dragon.IsWithinRadius(t, 3000)).ToArray();
            if (targets.Length == 0) return;
            int first = Random.Shared.Next(targets.Length);
            for (int i = 0; i < Math.Min(12, targets.Length); i++)
            {
                GameLiving target = targets[(first + i) % targets.Length];
                var nav = PathfindingProvider.Instance;
                var zone = target.CurrentZone;
                if (!TryPosition(nav, zone, new(target.X, target.Y, target.Z), _budget.Spawned,
                    _adds.Where(a => a.IsAlive).Select(a => new Vector3(a.X,a.Y,a.Z)).ToArray(), out var position)) continue;
                GameNPC add = Create(dragon);
                add.CurrentRegion = dragon.CurrentRegion;
                add.X = (int)position.X; add.Y = (int)position.Y; add.Z = (int)position.Z;
                add.Heading = dragon.Heading;
                // No legacy ChoosePath package: this NPC is already on a connected combat floor.
                if (!add.AddToWorld()) { add.Delete(); continue; }
                _adds.Add(add);
                _budget.Commit(now);
                if (add.Brain is StandardMobBrain brain)
                {
                    brain.AddToAggroList(target, 100);
                    brain.NextThinkTick = now;
                }
                return; // Never emit a catch-up batch when several health phases were crossed.
            }
        }

        public static GameNPC Create(GameNPC dragon) => dragon switch
        {
            AlbGolestandt => new GolestandtSpawnedAdd(),
            MidGjalpinulva => new GjalpinulvaSpawnedAdd(),
            HibCuuldurach => new CuuldurachSpawnedAdd(),
            _ => throw new ArgumentException("Not a mainland dragon", nameof(dragon))
        };

        public static bool ValidTarget(GameNPC source, GameLiving target)
        {
            if (target?.IsAlive != true || target.ObjectState != GameObject.eObjectState.Active ||
                source.CurrentRegionID != target.CurrentRegionID) return false;
            bool participant = target is GamePlayer or GameBot || target is GameNPC { Brain: ControlledMobBrain pet } &&
                pet.GetLivingOwner() is GamePlayer or GameBot;
            if (!participant || target.IsStealthed || target.effectListComponent.ContainsEffectForEffectType(eEffect.Shade)) return false;
            if (target is GameBot { IsOnStableMasterRoute: true } || target is GamePlayer { Steed: not null }) return false;
            return GameServer.ServerRules.IsAllowedToAttack(source, target, true);
        }

        public static bool TryPosition(IPathfindingMgr nav, Zone zone, Vector3 target, int slot,
            IReadOnlyCollection<Vector3> occupied, out Vector3 position)
        {
            position = default;
            if (nav == null || zone == null || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
            var targetFloor = nav.GetClosestPoint(zone, target, 32, 32, 128, nav.DefaultFilters);
            if (!targetFloor.HasValue || Math.Abs(targetFloor.Value.Z-target.Z)>128) return false;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                double angle = (slot * 137.5 + attempt * 30) * Math.PI / 180;
                float radius = 240 + attempt % 3 * 100;
                Vector3 raw = targetFloor.Value + new Vector3((float)Math.Cos(angle)*radius,(float)Math.Sin(angle)*radius,0);
                var floor = nav.GetClosestPoint(zone,raw,32,32,192,nav.DefaultFilters);
                if (!floor.HasValue || occupied.Any(p=>Vector3.DistanceSquared(p,floor.Value)<160*160) ||
                    !nav.HasLineOfSight(zone,floor.Value,targetFloor.Value,nav.DefaultFilters) ||
                    !AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,floor.Value,targetFloor.Value) ||
                    !AutonomousZoneItinerary.HasCompleteCorridor(nav,zone,targetFloor.Value,floor.Value)) continue;
                position = floor.Value;
                return true;
            }
            return false; // Never spawn into an unvalidated rock or an isolated pocket.
        }
    }
}

namespace DOL.AI.Brain
{
    /// <summary>Only the three dragons' approved adds; ordinary mob AI is unchanged.</summary>
    public class DragonEncounterAddBrain : StandardMobBrain
    {
        public override bool CanBaf { get => false; set { } }
        public override bool CanAggroTarget(GameLiving target) => DragonEncounterAdds.ValidTarget(Body, target);
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_LongRunPetRecovery
    {
        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public override byte Level { get; set; }
            public override bool IsAlive => true;
            public override int GetModifiedSpecLevel(string key) => 8;
            public override IControlledBrain ControlledBrain { get; set; }
        }

        private sealed class Pet : GameNPC
        {
            public bool RecentCombat;
            public bool Alive = true;
            public short Speed;
            public override bool IsAlive => Alive;
            public override bool InCombatInLast(int milliseconds) => RecentCombat;
            public override short CurrentSpeed => Speed;
            public override byte Level { get; set; }
        }

        private sealed class Zombie : NecromancerPet
        {
            private Zombie() : base(null) { }
            public override byte Level { get; set; }
        }

        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }

        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private GameServer _previousServer;
        private long _previousTime;
        private readonly List<GameLiving> _actors = new();
        private static readonly PropertyInfo Clock = typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime));

        [SetUp]
        public void Setup()
        {
            _previousServer = GameServer.Instance;
            _previousTime = GameLoop.GameLoopTime;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
            Clock.SetValue(null, 100_000L);
        }

        [TearDown]
        public void Teardown()
        {
            foreach (GameLiving actor in _actors)
            {
                ServiceObjectStore.Remove(actor.effectListComponent);
                if (actor.castingComponent != null) ServiceObjectStore.Remove(actor.castingComponent);
            }
            _actors.Clear();
            GameServer.LoadTestDouble(_previousServer);
            Clock.SetValue(null, _previousTime);
        }

        private T Actor<T>() where T : GameLiving
        {
            var actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            actor.ObjectState = GameObject.eObjectState.Active;
            if (actor is GameNPC)
                typeof(GameNPC).GetField("m_brains", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(actor, new ArrayList());
            actor.effectListComponent = EffectListComponent.Create(actor);
            _actors.Add(actor);
            return actor;
        }

        private static ControlledMobBrain Attach(GameLiving owner, GameNPC pet)
        {
            var brain = new ControlledMobBrain(owner) { Body = pet };
            typeof(GameNPC).GetField("m_ownBrain", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(pet, brain);
            return brain;
        }

        [TestCase(571, 60)]
        [TestCase(4451, 50)]
        public void FocusShieldReleasesRealCastingComponentAfterDeadPetOrder(int spellId, int frequency)
        {
            Bot owner = Actor<Bot>();
            owner.castingComponent = (NpcCastingComponent)CastingComponent.Create(owner);
            ((GameLiving)owner).castingComponent = owner.castingComponent;
            Pet pet = Actor<Pet>();
            Pet target = Actor<Pet>();
            target.Alive = true;
            var brain = Attach(owner, pet);
            typeof(ControlledMobBrain).GetProperty(nameof(ControlledMobBrain.OrderedAttackTarget)).SetValue(brain, target);
            Spell spell = new(new DbSpell { SpellID = spellId, Type = "DamageShield", Target = "Pet",
                IsFocus = true, Pulse = 1, Frequency = frequency, Duration = 8, CastTime = 2.5 }, 1);
            var handler = new SpellHandler(owner, spell, new SpellLine("test", "test", "test", true));
            typeof(CastingComponent).GetProperty(nameof(CastingComponent.SpellHandler)).SetValue(owner.castingComponent, handler);
            var pulse = new ECSPulseEffect(new(owner, 0, 1, handler), spell.Frequency);
            pulse.FinalizeState(EffectListComponent.AddEffectResult.Added);
            var effects = (Dictionary<eEffect, List<ECSGameEffect>>)typeof(EffectListComponent)
                .GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner.effectListComponent);
            effects[eEffect.Pulse] = new() { pulse };
            typeof(GameLiving).GetField("<ActivePulseSpells>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(owner, new ConcurrentDictionary<eSpellType, Spell>());
            pulse.OnStartEffect();
            var shield = new ECSGameSpellEffect(new(pet, 8000, 1, handler)) { EffectType = eEffect.FocusShield };
            shield.FinalizeState(EffectListComponent.AddEffectResult.Added);
            ((Dictionary<eEffect, List<ECSGameEffect>>)typeof(EffectListComponent)
                .GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(pet.effectListComponent))
                [eEffect.FocusShield] = new() { shield };
            pulse.ChildEffects[pet] = shield;
            // No timer registration/world loop: invoke the real timer's decision
            // and real CancelFocusSpells -> SpellHandler.Tick -> component cleanup.
            Type timerType = typeof(DamageShieldECSEffect).GetNestedType("CombatCheckTimer", BindingFlags.NonPublic);
            object timer = RuntimeHelpers.GetUninitializedObject(timerType);
            timerType.GetField("_brain", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(timer, brain);
            timerType.GetField("_pulseEffect", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(timer, pulse);
            MethodInfo tick = timerType.GetMethod("OnTick", BindingFlags.NonPublic | BindingFlags.Instance);
            void Tick() => tick.Invoke(timer, new object[] { null });
            Tick(); // Has not fought yet: do not cancel a freshly cast focus.
            Assert.That(owner.IsCasting, Is.True);
            pet.RecentCombat = true;
            Tick();
            pet.RecentCombat = false;
            Tick(); // A live explicit order can bridge a gap between swings.
            Assert.That(owner.IsCasting, Is.True);
            target.Alive = false;
            pet.Speed = 10;
            Tick(); // Returning/moving pet does not drop focus in mid-pull.
            Assert.That(pulse.IsEnding, Is.False);
            pet.Speed = 0;
            Tick();
            Assert.That(pulse.IsEnding || pulse.IsEnded, Is.True,
                "The combat timer must cancel focus after the retained target dies");
            // ECS removes state synchronously but runs OnStopEffect on the
            // component's next tick, which also ends the focus child effect.
            owner.effectListComponent.BeginTick();
            pet.effectListComponent.BeginTick();
            handler.Tick();
            Assert.That(owner.IsCasting, Is.False, "A corpse order must not latch focus and freeze the owner's AI");
            Assert.That(pulse.IsEnding || pulse.IsEnded, Is.True);
            Assert.That(shield.IsEnding || shield.IsEnded, Is.True, "Ending focus also ends the pet's child damage shield");
        }

        [Test]
        public void PulseChildPruningCannotRemoveReplacementAndStopReleasesAllTargets()
        {
            Bot owner = Actor<Bot>();
            Pet target = Actor<Pet>();
            Spell spell = new(new DbSpell { SpellID = 992001, Type = "SpeedEnhancement", Target = "Realm", Pulse = 1,
                Frequency = 5000, Duration = 8 }, 1);
            var handler = new SpellHandler(owner, spell, new SpellLine("test", "test", "test", true));
            var pulse = new ECSPulseEffect(new(owner, 0, 1, handler), spell.Frequency);
            var stale = new ECSGameSpellEffect(new(target, 1000, 1, handler));
            var current = new ECSGameSpellEffect(new(target, 1000, 1, handler));

            pulse.ChildEffects[target] = stale;
            pulse.ChildEffects[target] = current;
            Assert.That(pulse.RemoveChildIfCurrent(target, stale), Is.False);
            Assert.That(pulse.ChildEffects[target], Is.SameAs(current));

            typeof(GameLiving).GetField("<ActivePulseSpells>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(owner, new ConcurrentDictionary<eSpellType, Spell>());
            pulse.OnStopEffect();
            Assert.That(pulse.ChildEffects, Is.Empty);
        }

        [Test]
        public void HunterSummonHandlerRejectsAnExistingBotPet()
        {
            Bot owner = Actor<Bot>();
            owner.ControlledBrain = Attach(owner, Actor<Pet>());
            Spell spell = new(new DbSpell { Type = "SummonHunterPet", Target = "Self" }, 1);
            Assert.That(new SummonHunterPet(owner, spell, new SpellLine("test", "test", "test", true))
                .CheckEndCast(owner), Is.False);
        }

        [TestCase("Lifedrain")]
        [TestCase("PowerDrain")]
        public void ZombieDamageVarianceUsesItsBotOwnersLevelAndSpecialization(string type)
        {
            Bot owner = Actor<Bot>();
            owner.Level = 10;
            Zombie pet = Actor<Zombie>();
            pet.Level = 8;
            Attach(owner, pet);
            Pet target = Actor<Pet>();
            target.Level = 10;
            Spell spell = new(new DbSpell { Type = type, Damage = 10, Target = "Enemy" }, 1);
            SpellLine line = new("Death Sight", "Death Sight", "Death Sight", true);
            new SpellHandler(owner, spell, line).CalculateDamageVariance(target, out double expectedMin, out double expectedMax);
            new SpellHandler(pet, spell, line).CalculateDamageVariance(target, out double min, out double max);
            Assert.Multiple(() =>
            {
                Assert.That(min, Is.EqualTo(expectedMin));
                Assert.That(max, Is.EqualTo(expectedMax));
            });
        }
    }
}

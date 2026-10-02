using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_OrphanFocusRecovery
    {
        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public override bool IsAlive => true;
            public override byte Level => 1;
            public override int Mana { get; set; }
        }

        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }

        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly PropertyInfo Clock = typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime));
        private readonly List<GameLiving> _actors = new();
        private GameServer _previousServer;
        private long _previousTime;

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
                ServiceObjectStore.Remove(actor.castingComponent);
            }
            _actors.Clear();
            GameServer.LoadTestDouble(_previousServer);
            Clock.SetValue(null, _previousTime);
        }

        private (Bot Owner, SpellHandler Handler, ECSPulseEffect Pulse) Create(int id, bool pulsing = true)
        {
            Bot owner = (Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
            owner.ObjectState = GameObject.eObjectState.Active;
            typeof(GameNPC).GetField("m_brains", PrivateInstance).SetValue(owner, new ArrayList());
            owner.effectListComponent = EffectListComponent.Create(owner);
            owner.castingComponent = (NpcCastingComponent)CastingComponent.Create(owner);
            ((GameLiving)owner).castingComponent = owner.castingComponent;
            _actors.Add(owner);
            typeof(GameLiving).GetField("<ActivePulseSpells>k__BackingField", PrivateInstance)
                .SetValue(owner, new ConcurrentDictionary<eSpellType, Spell>());
            Spell spell = new(new DbSpell
            {
                SpellID = id, Type = "DamageShield", Target = "Pet", IsFocus = true,
                Pulse = pulsing ? 1 : 0, PulsePower = 1, Frequency = id == 571 ? 60 : 50,
                Duration = 8, CastTime = 2.5
            }, 1);
            SpellHandler handler = new(owner, spell, new SpellLine("test", "test", "test", true));
            SetCurrent(owner, handler);
            typeof(SpellHandler).GetProperty(nameof(SpellHandler.CastState)).SetValue(handler, eCastState.Focusing);
            ECSPulseEffect pulse = new(new(owner, 0, 1, handler), spell.Frequency);
            pulse.FinalizeState(EffectListComponent.AddEffectResult.Added);
            var effects = (Dictionary<eEffect, List<ECSGameEffect>>)typeof(EffectListComponent).GetField("_effects", PrivateInstance)
                .GetValue(owner.effectListComponent);
            effects[eEffect.Pulse] = new() { pulse };
            pulse.OnStartEffect();
            typeof(SpellHandler).GetProperty(nameof(SpellHandler.PulseEffect)).SetValue(handler, pulse);
            return (owner, handler, pulse);
        }

        private static void SetCurrent(Bot owner, SpellHandler handler)
        {
            typeof(CastingComponent).GetProperty(nameof(CastingComponent.SpellHandler)).SetValue(owner.castingComponent, handler);
        }

        private sealed class FailingInstant : SpellHandler
        {
            public FailingInstant(GameLiving caster) : base(caster,
                new Spell(new DbSpell { SpellID = 991001, Type = "DirectDamage", Target = "Enemy", CastTime = 0 }, 1),
                new SpellLine("test", "test", "test", true)) { }
            public void Fail() => InterruptCasting(false);
        }

        private sealed class CompletingCast : SpellHandler
        {
            public int Completions;
            public CompletingCast(GameLiving caster) : base(caster,
                new Spell(new DbSpell { SpellID = 991002, Type = "DirectDamage", Target = "Enemy", CastTime = 2 }, 1),
                new SpellLine("test", "test", "test", true)) { }
            public override bool CheckDuringCast(GameLiving target) => true;
            public override bool CheckEndCast(GameLiving target) => true;
            public override void FinishSpellCast(GameLiving target) => Completions++;
        }

        [Test]
        public void PrimaryCastFinishesOnceAfterSecondaryInstantFailure()
        {
            var (owner, _, _) = Create(571);
            var primary = new CompletingCast(owner);
            SetCurrent(owner, primary);
            typeof(SpellHandler).GetProperty(nameof(SpellHandler.CastState)).SetValue(primary, eCastState.Casting);
            typeof(SpellHandler).GetField("_castEndTick", PrivateInstance).SetValue(primary, GameLoop.GameLoopTime + 2000);
            new FailingInstant(owner).Fail();
            owner.castingComponent.Tick();
            Assert.That(primary.Completions, Is.Zero);
            Assert.That(owner.castingComponent.SpellHandler, Is.SameAs(primary));
            Clock.SetValue(null, GameLoop.GameLoopTime + 2001);
            owner.castingComponent.Tick();
            Assert.That(primary.Completions, Is.EqualTo(1));
            Assert.That(owner.IsCasting, Is.False);
        }

        [Test]
        public void FailedInstantDoesNotInterruptAnIndependentPrimaryBotCast()
        {
            var (owner, primary, pulse) = Create(571);
            new FailingInstant(owner).Fail();
            Assert.That(owner.castingComponent.SpellHandler, Is.SameAs(primary));
            Assert.That(primary.CastState, Is.EqualTo(eCastState.Focusing));
            Assert.That(pulse.IsActive, Is.True);
        }

        [Test]
        public void StoppingBotCastingAlsoClearsNotYetStartedRequests()
        {
            var (owner, handler, _) = Create(571);
            var requests = (Queue<CastingComponent.StartSkillRequest>)typeof(CastingComponent)
                .GetField("_startSkillRequests", PrivateInstance).GetValue(owner.castingComponent);
            var request = new CastingComponent.CastSpellRequest();
            request.Init(owner.castingComponent, handler.Spell, handler.SpellLine, null, owner, null);
            requests.Enqueue(request);
            owner.castingComponent.ClearSpellHandlers();
            Assert.That(requests, Is.Empty, "A stopped/dead/transferred bot must not start a stale cast next tick");
            Assert.That(owner.IsCasting, Is.False);
        }

        [TestCase(571)]
        [TestCase(4451)]
        public void RealPowerExhaustionEndsFocusAndReleasesCasting(int id)
        {
            var (owner, handler, pulse) = Create(id);
            pulse.NextTick = GameLoop.GameLoopTime;
            typeof(EffectService).GetMethod("TickPulsingEffect", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { pulse, handler.Spell, handler, owner });
            owner.effectListComponent.BeginTick();
            Assert.That(pulse.IsEnded, Is.True);
            Assert.That(owner.ActivePulseSpells, Is.Empty);
            for (int i = 0; i < 10; i++) owner.castingComponent.Tick();
            Assert.That(owner.IsCasting, Is.False, "An ended shield must not block the next movement/AI decision");
        }

        [TestCase(571)]
        [TestCase(4451)]
        public void DispelOrExplicitPulseEndReleasesCasting(int id)
        {
            var (owner, handler, pulse) = Create(id);
            pulse.End();
            // Cleanup must not depend on the deferred OnStop callback running.
            owner.castingComponent.Tick();
            Assert.That(owner.IsCasting, Is.False);
            owner.effectListComponent.BeginTick();
        }

        [TestCase(571)]
        [TestCase(4451)]
        public void ActiveFocusIsNotInterruptedEvenAtZeroPowerBetweenPulses(int id)
        {
            var (owner, handler, pulse) = Create(id);
            for (int i = 0; i < 10; i++) owner.castingComponent.Tick();
            Assert.That(pulse.IsActive, Is.True);
            Assert.That(owner.castingComponent.SpellHandler, Is.SameAs(handler));
            Assert.That(handler.CastState, Is.EqualTo(eCastState.Focusing));
        }

        [TestCase(571)]
        [TestCase(4451)]
        public void MissingOrRejectedPulseCannotLeaveAFocusingHandler(int id)
        {
            var (owner, handler, pulse) = Create(id);
            pulse.End();
            owner.effectListComponent.BeginTick();
            typeof(SpellHandler).GetProperty(nameof(SpellHandler.PulseEffect)).SetValue(handler, null);
            owner.castingComponent.Tick();
            Assert.That(owner.IsCasting, Is.False);
        }

        [Test]
        public void ExistingQueuedSpellIsPromotedWithoutBeingInterrupted()
        {
            var (owner, handler, pulse) = Create(571);
            SpellHandler next = new(owner, handler.Spell, handler.SpellLine);
            typeof(CastingComponent).GetProperty(nameof(CastingComponent.QueuedSpellHandler)).SetValue(owner.castingComponent, next);
            pulse.End();
            owner.effectListComponent.BeginTick();
            owner.castingComponent.Tick();
            Assert.That(owner.castingComponent.SpellHandler, Is.SameAs(next));
            Assert.That(owner.castingComponent.QueuedSpellHandler, Is.Null);
            Assert.That(next.CastState, Is.EqualTo(eCastState.Precast));
        }

        [Test]
        public void ObsoleteFocusHandlerCannotClearANewerCast()
        {
            var (owner, old, pulse) = Create(571);
            SpellHandler next = new(owner, old.Spell, old.SpellLine);
            SetCurrent(owner, next);
            pulse.End();
            owner.effectListComponent.BeginTick();
            old.Tick();
            Assert.That(owner.castingComponent.SpellHandler, Is.SameAs(next));
            Assert.That(next.CastState, Is.EqualTo(eCastState.Precast));
        }

        [Test]
        public void NonPulsingFocusIsOutsideThisRecovery()
        {
            var (owner, handler, pulse) = Create(571, pulsing: false);
            pulse.End();
            owner.effectListComponent.BeginTick();
            typeof(SpellHandler).GetProperty(nameof(SpellHandler.PulseEffect)).SetValue(handler, null);
            owner.castingComponent.Tick();
            Assert.That(owner.castingComponent.SpellHandler, Is.SameAs(handler));
            Assert.That(handler.CastState, Is.EqualTo(eCastState.Focusing));
        }
    }
}

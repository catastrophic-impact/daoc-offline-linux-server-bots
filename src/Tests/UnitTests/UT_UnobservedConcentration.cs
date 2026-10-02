using System;
using System.Collections;
using System.Collections.Generic;
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
    public class UT_UnobservedConcentration
    {
        // No world, clients, accounts or running AI. Exercise the actual effect
        // service and range checks with deliberately unobserved actor bodies.
        private sealed class UnobservedBot : GameBot
        {
            private UnobservedBot() : base((OfflineWorldBotRecord)null) { }
            public bool HumanVisible;
            public bool TestAlive;
            public int TestX;
            public override bool IsVisibleToPlayers => HumanVisible;
            public override bool IsVisibleToPlayersOrBots => HumanVisible;
            public override bool IsAlive => TestAlive;
            public override int X => TestX;
            public override int Y => 0;
            public override int Z => 0;
            public override eRealm Realm { get; set; }
        }

        private sealed class AmbientNpc : GameNPC
        {
            public bool HumanVisible;
            public bool BotVisible;
            public override bool IsVisibleToPlayers => HumanVisible;
            public override bool IsVisibleToPlayersOrBots => HumanVisible || BotVisible;
            public override bool IsAlive => true;
        }

        private sealed class InertServer : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }

        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, EmptyReads>();
        public class EmptyReads : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (!method.Name.StartsWith("Select") && !method.Name.StartsWith("Find"))
                    throw new InvalidOperationException("Unexpected database write in effect test: " + method.Name);
                Type type = method.ReturnType;
                return type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type)
                    ? Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetGenericArguments()[0])) : null;
            }
        }

        private static readonly MethodInfo TickSpellEffect = typeof(EffectService)
            .GetMethod("TickSpellEffect", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly PropertyInfo Clock = typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime));
        private readonly List<GameLiving> _actors = new();
        private long _oldTime;
        private GameServer _oldServer;

        [SetUp]
        public void SetUp()
        {
            _oldTime = GameLoop.GameLoopTime;
            _oldServer = GameServer.Instance;
            GameServer.LoadTestDouble((InertServer)RuntimeHelpers.GetUninitializedObject(typeof(InertServer)));
            Clock.SetValue(null, 10_000L);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameLiving actor in _actors) ServiceObjectStore.Remove(actor.effectListComponent);
            _actors.Clear();
            Clock.SetValue(null, _oldTime);
            GameServer.LoadTestDouble(_oldServer);
        }

        private T Actor<T>() where T : GameLiving
        {
            T actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            actor.ObjectState = GameObject.eObjectState.Active;
            if (actor is UnobservedBot bot) bot.TestAlive = true;
            actor.effectListComponent = EffectListComponent.Create(actor);
            if (actor is GameNPC)
                typeof(GameNPC).GetField("m_brains", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(actor, new ArrayList());
            _actors.Add(actor);
            return actor;
        }

        private static ECSGameSpellEffect Buff(GameLiving caster, GameLiving target = null, string type = "BaseArmorFactorBuff")
        {
            // Shaman's level-one Guardian's Lesser Ward uses concentration=1,
            // duration=0. Endurance/other class variants share this lifecycle.
            Spell spell = new(new DbSpell { SpellID = 3151, Name = "Test concentration buff", Type = type,
                Target = "Realm", Concentration = 1, Duration = 0, Value = 6, Range = 1500, CastTime = 3 }, 1);
            SpellHandler handler = new(caster, spell, new SpellLine("Augmentation", "Augmentation", "Augmentation", true));
            ECSGameSpellEffect effect = new(new(target ?? caster, 0, 1, handler));
            effect.FinalizeState(EffectListComponent.AddEffectResult.Added);
            return effect;
        }

        private static void Pulse(ECSGameSpellEffect effect)
        {
            Clock.SetValue(null, effect.NextTick);
            TickSpellEffect.Invoke(null, new object[] { effect });
        }

        [TestCase(eRealm.Albion, false)] [TestCase(eRealm.Midgard, false)] [TestCase(eRealm.Hibernia, false)]
        [TestCase(eRealm.Albion, true)] [TestCase(eRealm.Midgard, true)] [TestCase(eRealm.Hibernia, true)]
        public void BotBuffSurvivesWithZeroHumanObserversInEveryRealm(eRealm realm, bool temporary)
        {
            UnobservedBot bot = Actor<UnobservedBot>();
            bot.Realm = realm;
            typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot, !temporary);
            typeof(GameBot).GetProperty(nameof(GameBot.IsTemporaryGroupHelper)).SetValue(bot, temporary);
            ECSGameSpellEffect effect = Buff(bot);
            for (int i = 0; i < 30; i++)
            {
                Pulse(effect);
                Assert.That(effect.IsActive && !effect.IsEnding, Is.True, "No client may be required to maintain a bot buff");
                Assert.That(effect.NextTick, Is.GreaterThan(GameLoop.GameLoopTime));
            }
        }

        [TestCase("BaseArmorFactorBuff")] [TestCase("StrengthBuff")] [TestCase("ConstitutionBuff")]
        [TestCase("DexterityBuff")] [TestCase("StrengthConstitutionBuff")]
        [TestCase("DexterityQuicknessBuff")] [TestCase("EnduranceRegenBuff")]
        public void LosingTheHumanObserverDoesNotCancelAnyConcentrationBuffType(string type)
        {
            UnobservedBot bot = Actor<UnobservedBot>();
            bot.HumanVisible = true;
            ECSGameSpellEffect effect = Buff(bot, type: type);
            Pulse(effect);
            bot.HumanVisible = false;
            Pulse(effect);
            Assert.That(effect.IsActive && !effect.IsEnding, Is.True);
        }

        [Test]
        public void BotOwnedPetDoesNotRequireHumanObserverEither()
        {
            UnobservedBot bot = Actor<UnobservedBot>();
            AmbientNpc pet = Actor<AmbientNpc>();
            typeof(GameNPC).GetField("m_ownBrain", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(pet, new ControlledMobBrain(bot) { Body = pet });
            ECSGameSpellEffect effect = Buff(pet);
            Pulse(effect);
            Assert.That(effect.IsActive && !effect.IsEnding, Is.True);
        }

        [TestCase(false, GameObject.eObjectState.Active)]
        [TestCase(true, GameObject.eObjectState.Inactive)]
        [TestCase(true, GameObject.eObjectState.Deleted)]
        public void BotDeathOrWorldRemovalStillEndsItsBuffOnAnotherGroupMember(bool alive, GameObject.eObjectState state)
        {
            UnobservedBot caster = Actor<UnobservedBot>();
            UnobservedBot recipient = Actor<UnobservedBot>();
            ECSGameSpellEffect effect = Buff(caster, recipient);
            caster.TestAlive = alive;
            caster.ObjectState = state;
            Pulse(effect);
            Assert.That(effect.IsEnding || effect.IsEnded, Is.True);
        }

        [Test]
        public void UnobservedPetWithDepartedBotOwnerDoesNotKeepOrphanedBuffs()
        {
            UnobservedBot bot = Actor<UnobservedBot>();
            AmbientNpc pet = Actor<AmbientNpc>();
            typeof(GameNPC).GetField("m_ownBrain", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(pet, new ControlledMobBrain(bot) { Body = pet });
            ECSGameSpellEffect effect = Buff(pet);
            bot.ObjectState = GameObject.eObjectState.Inactive;
            Pulse(effect);
            Assert.That(effect.IsEnding || effect.IsEnded, Is.True);
        }

        [TestCase(false, false, true)] [TestCase(true, false, false)] [TestCase(false, true, false)]
        public void OrdinaryNpcCleanupStillAppliesOnlyWhenDormant(bool human, bool bot, bool ends)
        {
            AmbientNpc npc = Actor<AmbientNpc>();
            npc.HumanVisible = human;
            npc.BotVisible = bot;
            ECSGameSpellEffect effect = Buff(npc);
            Pulse(effect);
            Assert.That(effect.IsEnding || effect.IsEnded, Is.EqualTo(ends));
        }

        [Test]
        public void UnobservedGroupBuffStillDisablesOutOfRangeAndReenablesWhenNear()
        {
            UnobservedBot caster = Actor<UnobservedBot>();
            UnobservedBot recipient = Actor<UnobservedBot>();
            ECSGameSpellEffect effect = Buff(caster, recipient, "EnduranceRegenBuff");
            recipient.TestX = 100_000;
            Pulse(effect);
            Assert.That(effect.IsDisabling || effect.IsDisabled, Is.True);
            effect.FinalizeState(EffectListComponent.AddEffectResult.RenewedDisabled);
            recipient.TestX = 0;
            Pulse(effect);
            Assert.That(effect.IsEnabling || effect.IsActive, Is.True);
            Assert.That(effect.IsEnding || effect.IsEnded, Is.False);
        }
    }
}

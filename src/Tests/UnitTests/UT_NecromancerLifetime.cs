using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using DOL.GS.Spells;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_NecromancerLifetime
    {
        private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private GameServer _previousServer;
        private PetTestLanguageScope _language;
        private readonly List<GameLiving> _actors = new();

        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }
        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public override bool IsAlive => Health > 0;
            public override int Health { get; set; }
            public override byte Level { get; set; }
            public override ushort Model { get; set; }
            public override ICharacterClass CharacterClass => new ClassNecromancer();
            public override void Shade(bool state) => Model = (ushort)(state ? 822 : 1);
        }
        private sealed class Player : GamePlayer
        {
            private Player() : base(null, null) { }
            public override bool IsAlive => true;
        }
        private sealed class Servant : NecromancerPet
        {
            private Servant() : base(null) { }
            public int Deaths;
            public int PositionX;
            public override int X => PositionX;
            public override int Health { get; set; }
            public override bool IsAlive => Health > 0;
            public override void Die(GameObject killer) { Deaths++; Health = 0; }
        }
        private sealed class Brain : NecromancerPetBrain
        {
            public int Releases;
            public Brain(GameLiving owner) : base(owner) { }
            public override bool Stop() => true;
            public override void OnRelease() => Releases++;
        }

        [SetUp]
        public void Setup()
        {
            _language = new PetTestLanguageScope();
            _previousServer = GameServer.Instance;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }
        [TearDown]
        public void Teardown()
        {
            foreach (GameLiving actor in _actors) ServiceObjectStore.Remove(actor.effectListComponent);
            _actors.Clear();
            GameServer.LoadTestDouble(_previousServer);
            _language.Dispose();
        }

        [TestCase(false, 1601)]
        [TestCase(false, 2001)]
        [TestCase(true, 1601)]
        [TestCase(true, 2001)]
        public void BotServantIsNotKilledOrPutOnADistanceExpiry(bool companion, int distance)
        {
            var (owner, pet, brain) = CreateBot(companion);
            pet.PositionX = distance;
            for (int i = 0; i < 20; i++)
                typeof(NecromancerPetBrain).GetMethod("CheckTether", Hidden).Invoke(brain, null);
            Assert.That(pet.IsAlive, Is.True);
            Assert.That(pet.Deaths, Is.Zero);
            Assert.That(owner.ControlledBrain, Is.SameAs(brain));
            Assert.That(typeof(NecromancerPetBrain).GetField("_tetherTimer", Hidden).GetValue(brain), Is.Null);
        }

        [Test]
        public void HumanPlayerHardTetherStillKillsThePet()
        {
            Player owner = Actor<Player>();
            Servant pet = Actor<Servant>();
            pet.Health = 100;
            pet.PositionX = 2001;
            Brain brain = Attach(owner, pet);
            typeof(NecromancerPetBrain).GetMethod("CheckTether", Hidden).Invoke(brain, null);
            Assert.That(pet.Deaths, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LivingServantCannotBeVoluntarilyReleasedEvenWithOneHp(bool companion)
        {
            var (owner, pet, brain) = CreateBot(companion);
            owner.Level = 50; // Includes the companion's endgame refresh path.
            pet.Health = 1;
            owner.Shade(true);
            owner.CommandNpcRelease();
            Assert.That(brain.Releases, Is.Zero);
            Assert.That(owner.ControlledBrain, Is.SameAs(brain));
            Assert.That(owner.IsShade, Is.True);
            Assert.That(pet.Health, Is.EqualTo(1));
        }

        [Test]
        public void RealPetDeathStillClearsShadeAndLeavesOneHp()
        {
            var (owner, pet, brain) = CreateBot(false);
            owner.Shade(true);
            pet.Health = 0;
            Assert.That(owner.RemoveControlledBrain(brain), Is.True);
            Assert.That(owner.ControlledBrain, Is.Null);
            Assert.That(owner.IsShade, Is.False);
            Assert.That(owner.Health, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LivingServantIsAggroIdentityWhileOwnerShadeIsProtected(bool companion)
        {
            var (owner, pet, _) = CreateBot(companion);
            owner.Shade(true);

            Assert.Multiple(() =>
            {
                Assert.That(StandardMobBrain.NaturalAggroIdentity(pet), Is.SameAs(owner));
                Assert.That(StandardMobBrain.IsPlayerLikeNaturalAggroTarget(
                    StandardMobBrain.NaturalAggroIdentity(pet)), Is.True);
                Assert.That(AbstractServerRules.IsProtectedNecromancerShade(owner), Is.True);
            });
        }

        [TestCase("StableTravel")]
        [TestCase("WorldRemoval")]
        public void RequiredTravelAndWorldCleanupStillReleaseLivingServant(string reason)
        {
            var (owner, pet, brain) = CreateBot(false);
            owner.Shade(true);
            Type reasonType = typeof(GameBot).GetNestedType("PetReleaseReason", BindingFlags.NonPublic);
            typeof(GameBot).GetMethod("ReleaseControlledPet", Hidden).Invoke(owner, new[] { Enum.Parse(reasonType, reason) });
            Assert.That(brain.Releases, Is.EqualTo(1));
            Assert.That(owner.ControlledBrain, Is.Null);
            Assert.That(owner.IsShade, Is.False);
            Assert.That(owner.Health, Is.EqualTo(100), "Required cleanup is not combat death");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SummonCannotBeginOrCompleteOverExistingLivingServant(bool companion)
        {
            var (owner, pet, brain) = CreateBot(companion);
            // Intentionally no shade effect: exercise the independent pet guard.
            Spell spell = new(new DbSpell { Type = "SummonNecroPet", Target = "Self", LifeDrainReturn = -999 }, 1);
            var handler = new SummonNecromancerPet(owner, spell, new SpellLine("test", "test", "test", true));
            Assert.That(handler.CheckBeginCast(owner), Is.False);
            Assert.DoesNotThrow(() => handler.ApplyEffectOnTarget(owner));
            Assert.That(owner.ControlledBrain, Is.SameAs(brain));
            Assert.That(handler.Pet, Is.Null);
            Assert.That(pet.IsAlive, Is.True);
        }

        private (Bot, Servant, Brain) CreateBot(bool companion)
        {
            Bot bot = Actor<Bot>();
            bot.Health = 100;
            bot.Level = 1;
            bot.Model = 1;
            typeof(GameBot).GetProperty(nameof(GameBot.IsTemporaryGroupHelper)).SetValue(bot, companion);
            Servant pet = Actor<Servant>();
            pet.Health = 100;
            Brain brain = Attach(bot, pet);
            bot.InitControlledBrainArray(1);
            bot.ControlledBrain = brain;
            return (bot, pet, brain);
        }
        private static Brain Attach(GameLiving owner, Servant pet)
        {
            Brain brain = new(owner) { Body = pet };
            typeof(GameNPC).GetField("m_ownBrain", Hidden).SetValue(pet, brain);
            return brain;
        }
        private T Actor<T>() where T : GameLiving
        {
            T actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            actor.ObjectState = GameObject.eObjectState.Active;
            typeof(GameLiving).GetField("<TempProperties>k__BackingField", Hidden).SetValue(actor, new PropertyCollection());
            if (actor is GameNPC) typeof(GameNPC).GetField("m_brains", Hidden).SetValue(actor, new ArrayList());
            actor.effectListComponent = EffectListComponent.Create(actor);
            _actors.Add(actor);
            return actor;
        }
    }
}

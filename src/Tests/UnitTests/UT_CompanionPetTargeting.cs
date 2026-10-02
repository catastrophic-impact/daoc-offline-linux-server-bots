using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerClass;
using DOL.GS.ServerProperties;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Companion pets keep one target while a party fights several enemies,
    /// finish their casts, heal the most injured ally, and ranged companions
    /// chase a distant player-led target instead of standing still.
    /// </summary>
    [TestFixture, NonParallelizable]
    public class UT_CompanionPetTargeting
    {
        private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private GameServer _previous;
        private int _previousHealThreshold;
        private PetTestLanguageScope _language;
        private readonly List<GameLiving> _actors = new();

        private sealed class Rules : NormalServerRules
        {
            public override bool IsAllowedToAttack(GameLiving attacker, GameLiving defender, bool quiet) =>
                attacker.Realm != defender.Realm;
        }

        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
            protected override IServerRules ServerRulesImpl => new Rules();
        }

        private sealed class Player : GamePlayer
        {
            private Player() : base(null, null) { }
            public override bool IsAlive => true;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override eRealm Realm { get => eRealm.Albion; set { } }
            public override IControlledBrain ControlledBrain { get; set; }
            public override IPacketLib Out => new BotDummyPacketLib();
        }

        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public ICharacterClass TestClass;
            public byte TestHealth = 100;
            public override bool IsAlive => true;
            public override int X => 0;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override eRealm Realm { get => eRealm.Albion; set { } }
            public override byte Level { get => 50; set { } }
            public override int EffectiveLevel => 50;
            public override byte HealthPercent => TestHealth;
            public override ICharacterClass CharacterClass => TestClass;
            public override IControlledBrain ControlledBrain { get; set; }
        }

        private sealed class Enemy : GameNPC
        {
            public int TestX;
            public bool Alive = true;
            public override bool IsAlive => Alive;
            public override int X => TestX;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override eRealm Realm { get => eRealm.Midgard; set { } }
            public override byte Level { get => 50; set { } }
            public override int EffectiveLevel => 50;
            public override GameObject TargetObject { get; set; }
        }

        private sealed class Pet : GameNPC
        {
            public bool Casting;
            public int CastStops;
            public byte TestHealth = 100;
            public override bool IsAlive => true;
            public override bool IsCasting => Casting;
            public override void StopCurrentSpellcast() { CastStops++; Casting = false; }
            public override int X => 0;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override eRealm Realm { get => eRealm.Albion; set { } }
            public override byte HealthPercent => TestHealth;
            public override int GetModified(eProperty property) => property == eProperty.SpellRange ? 100 : 0;
        }

        /// <summary>Only the order/cancel step is under test; target pursuit is not.</summary>
        private sealed class OrderOnlyPetBrain : ControlledMobBrain
        {
            public OrderOnlyPetBrain(GameLiving owner) : base(owner) { }
            public override void AttackMostWanted() { }
        }

        private sealed class HealingPetBrain : ControlledMobBrain
        {
            public HealingPetBrain(GameLiving owner) : base(owner) { }
            public GameLiving HealTarget(Spell spell) => FindTargetForDefensiveSpell(spell);
        }

        [SetUp] public void Setup()
        {
            _previous = GameServer.Instance;
            _previousHealThreshold = Properties.PET_HEAL_THRESHOLD;
            Properties.PET_HEAL_THRESHOLD = 75;
            _language = new PetTestLanguageScope();
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }

        [TearDown] public void Cleanup()
        {
            foreach (GameLiving actor in _actors) ServiceObjectStore.Remove(actor.effectListComponent);
            _actors.Clear();
            Properties.PET_HEAL_THRESHOLD = _previousHealThreshold;
            _language.Dispose();
            GameServer.LoadTestDouble(_previous);
        }

        [Test] public void PetKeepsItsLiveTargetWhenTheOwnerLooksAtAnotherEnemy()
        {
            Bot owner = MakeBot(out ControlledMobBrain petBrain);
            Enemy first = NewEnemy(300);
            Enemy second = NewEnemy(400);
            petBrain.OrderedAttackTarget = first;

            Assert.That(OwnerBrain(owner).ResolvePetCombatTarget(second), Is.SameAs(first),
                "an automatic owner target change must not re-order the pet off a live enemy");
            Assert.That(OwnerBrain(owner).ResolvePetCombatTarget(first), Is.SameAs(first));
        }

        [Test] public void PetPeelsAnEnemyOffItsOwnerButNeverAlternates()
        {
            Bot owner = MakeBot(out ControlledMobBrain petBrain);
            Enemy current = NewEnemy(300);
            Enemy onOwner = NewEnemy(400);
            petBrain.OrderedAttackTarget = current;
            onOwner.TargetObject = owner;

            Assert.That(OwnerBrain(owner).ResolvePetCombatTarget(onOwner), Is.SameAs(onOwner),
                "an enemy beating on the owner outranks one that is fighting someone else");

            current.TargetObject = owner;
            petBrain.OrderedAttackTarget = current;
            Assert.That(OwnerBrain(owner).ResolvePetCombatTarget(onOwner), Is.SameAs(current),
                "with two enemies on the owner the pet stays put instead of flipping");

            current.TargetObject = petBrain.Body;
            Assert.That(OwnerBrain(owner).ResolvePetCombatTarget(onOwner), Is.SameAs(current),
                "the pet keeps tanking the enemy that is attacking it");
        }

        [Test] public void PetMovesOnWhenItsTargetDies()
        {
            Bot owner = MakeBot(out ControlledMobBrain petBrain);
            Enemy first = NewEnemy(300);
            Enemy second = NewEnemy(400);
            petBrain.OrderedAttackTarget = first;
            first.Alive = false;

            Assert.That(OwnerBrain(owner).ResolvePetCombatTarget(second), Is.SameAs(second));
        }

        [Test] public void ExplicitOrderedPullSwitchesThePet()
        {
            Bot owner = MakeBot(out ControlledMobBrain petBrain);
            Enemy first = NewEnemy(300);
            Enemy ordered = NewEnemy(400);
            petBrain.OrderedAttackTarget = first;
            Field(typeof(BotBrain), OwnerBrain(owner), "_orderedPullTarget", ordered);

            Assert.That(OwnerBrain(owner).ResolvePetCombatTarget(ordered), Is.SameAs(ordered));
            Assert.That(OwnerBrain(owner).ResolvePetCombatTarget(first), Is.SameAs(first),
                "the ordered target is only forced when it is the candidate being ordered");
        }

        [Test] public void BotOwnersNewOrderDoesNotCancelThePetsCast()
        {
            Bot owner = MakeBot(out ControlledMobBrain petBrain);
            Pet pet = (Pet)petBrain.Body;
            Enemy first = NewEnemy(300);
            Enemy second = NewEnemy(400);
            pet.TargetObject = first;
            pet.Casting = true;

            petBrain.Attack(second);

            Assert.That(pet.CastStops, Is.Zero, "the accepted cast completes before the new order takes over");
            Assert.That(petBrain.OrderedAttackTarget, Is.SameAs(second));
        }

        [Test] public void HumansOwnPetCommandStillInterruptsTheCast()
        {
            Player player = Actor<Player>();
            Pet pet = NewPet();
            ControlledMobBrain petBrain = new OrderOnlyPetBrain(player) { Body = pet };
            Field(typeof(GameNPC), pet, "m_ownBrain", petBrain);
            Enemy first = NewEnemy(300);
            Enemy second = NewEnemy(400);
            pet.TargetObject = first;
            pet.Casting = true;

            petBrain.Attack(second);

            Assert.That(pet.CastStops, Is.EqualTo(1));
        }

        [Test] public void PetHealsTheMostInjuredAllyNotTheFirstFound()
        {
            Bot owner = MakeHealingPetOwner(out HealingPetBrain brain, out Pet pet);
            owner.TestHealth = 60;
            pet.TestHealth = 70;
            Bot ally = NewBot(20);
            var group = new Group(owner);
            Add(group, owner);
            Add(group, ally);

            Assert.That(brain.HealTarget(HealSpell("Realm")), Is.SameAs(ally),
                "a companion bot at 20% outranks the owner at 60%");
        }

        [Test] public void EmergencyInTheGroupIsNotOverwrittenByTheOwner()
        {
            Bot owner = MakeHealingPetOwner(out HealingPetBrain brain, out Pet pet);
            owner.TestHealth = 30;
            Bot ally = NewBot(10);
            var group = new Group(owner);
            Add(group, owner);
            Add(group, ally);

            Assert.That(brain.HealTarget(HealSpell("Realm")), Is.SameAs(ally));
        }

        [Test] public void SingleTargetPetHealStillPrefersTheLowerOfOwnerAndSelf()
        {
            Bot owner = MakeHealingPetOwner(out HealingPetBrain brain, out Pet pet);
            owner.TestHealth = 50;
            pet.TestHealth = 40;
            Bot ally = NewBot(5);
            var group = new Group(owner);
            Add(group, owner);
            Add(group, ally);

            Assert.That(brain.HealTarget(HealSpell("Pet")), Is.SameAs(pet),
                "a non-group heal keeps its original owner/self reach");
        }

        [Test] public void NobodyBelowTheThresholdMeansNoHeal()
        {
            MakeHealingPetOwner(out HealingPetBrain brain, out _);
            Assert.That(brain.HealTarget(HealSpell("Realm")), Is.Null);
        }

        [TestCase(1400)]
        [TestCase(1800)]
        public void PlayerLedApproachChasesTargetsFarOutsideSpellRange(int engagementRange)
        {
            (int min, int max) = BotBrain.PlayerLedApproachFollowDistances(engagementRange);
            Assert.That(min, Is.EqualTo(engagementRange - 100), "stops just inside spell range");
            Assert.That(max, Is.EqualTo(BotBrain.MAX_AGGRO_LIST_DISTANCE),
                "native follow gives up beyond this distance, so it must cover distant pet pulls");
            Assert.That(max, Is.GreaterThan(engagementRange + 1000));
        }

        private Bot MakeBot(out ControlledMobBrain petBrain)
        {
            Bot owner = NewBot(100);
            var brain = new BotBrain { Body = owner };
            Field(typeof(GameNPC), owner, "m_ownBrain", brain);
            Pet pet = NewPet();
            petBrain = new OrderOnlyPetBrain(owner) { Body = pet };
            Field(typeof(GameNPC), pet, "m_ownBrain", petBrain);
            owner.ControlledBrain = petBrain;
            return owner;
        }

        private Bot MakeHealingPetOwner(out HealingPetBrain brain, out Pet pet)
        {
            Bot owner = NewBot(100);
            pet = NewPet();
            pet.castingComponent = (NpcCastingComponent)CastingComponent.Create(pet);
            ((GameLiving)pet).castingComponent = pet.castingComponent;
            brain = new HealingPetBrain(owner) { Body = pet };
            Field(typeof(GameNPC), pet, "m_ownBrain", brain);
            owner.ControlledBrain = brain;
            return owner;
        }

        // GetUninitializedObject skips field initializers, so set every test default here.
        private Enemy NewEnemy(int x)
        {
            Enemy enemy = Actor<Enemy>();
            enemy.TestX = x;
            enemy.Alive = true;
            return enemy;
        }

        private Bot NewBot(byte health)
        {
            Bot bot = Actor<Bot>();
            bot.TestClass = new ClassEnchanter();
            bot.TestHealth = health;
            return bot;
        }

        private Pet NewPet()
        {
            Pet pet = Actor<Pet>();
            pet.TestHealth = 100;
            pet.attackComponent = new AttackComponent(pet);
            return pet;
        }

        private static Spell HealSpell(string target) =>
            new(new DbSpell { SpellID = 990001, Type = "Heal", Target = target, Range = 2000, Value = 100, CastTime = 2 }, 1);

        private static BotBrain OwnerBrain(Bot bot) => (BotBrain)bot.Brain;

        private T Actor<T>() where T : GameLiving
        {
            T actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            actor.ObjectState = GameObject.eObjectState.Active;
            Field(typeof(GameLiving), actor, "<TempProperties>k__BackingField", new PropertyCollection());
            if (actor is GameNPC npc)
            {
                Field(typeof(GameNPC), actor, "m_brains", new ArrayList());
                Field(typeof(GameNPC), actor, "m_spells", new List<Spell>());
                npc.movementComponent = new NpcMovementComponent(npc);
            }
            actor.effectListComponent = EffectListComponent.Create(actor);
            _actors.Add(actor);
            return actor;
        }

        private static List<GameLiving> Members(Group group) => (List<GameLiving>)typeof(Group).GetField("_groupMembers", Hidden).GetValue(group);
        private static void Add(Group group, GameLiving member) { Members(group).Add(member); member.Group = group; }
        private static void Field(Type type, object owner, string name, object value) => type.GetField(name, Hidden).SetValue(owner, value);
    }
}

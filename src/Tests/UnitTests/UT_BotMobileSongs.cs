using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.Effects;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerClass;
using DOL.GS.PropertyCalc;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_BotMobileSongs
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly SpellLine Line = new("test songs", "test songs", "", true);
        private static readonly IObjectDatabase Empty = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private GameServer _previous;
        private long _time;
        private PetTestLanguageScope _language;
        private readonly List<GameLiving> _actors = new();
        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => Empty;
            protected override GS.ServerRules.IServerRules ServerRulesImpl => new GS.ServerRules.NormalServerRules();
        }
        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public ICharacterClass Class;
            public bool Moving, Attacking, CaptureCasts;
            public int Walks, Stops;
            public Spell LastRequested;
            public int Cooldown;
            public DbInventoryItem Instrument;
            public override DbInventoryItem ActiveWeapon => Instrument;
            public override ICharacterClass CharacterClass => Class;
            public override byte Level => 50;
            public override bool IsAlive => true;
            public override bool IsCrowdControlled => false;
            public override bool IsMoving => Moving;
            public override bool IsAttacking => Attacking;
            public override bool IsCasting => castingComponent.IsCasting;
            public override bool IsBeingInterrupted => false;
            public override int X => 0;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override short MaxSpeed => 200;
            public override int Health { get; set; }
            public override int Mana { get; set; }
            public override int Endurance { get; set; }
            public override int MaxHealth => 100;
            public override int MaxMana => 100;
            public override int MaxEndurance => 100;
            public override int GetModified(eProperty p) => p == eProperty.SpellRange ? 100 : 3;
            public override int GetSkillDisabledDuration(Skill skill) => Cooldown;
            public override void WalkTo(Vector3 target, short speed) { Walks++; }
            public override void StopMoving() { Stops++; }
            public override void StopMovingOnPath() { Stops++; }
            public void InvokeGameBotWalkTo(Vector3 target, short speed) => base.WalkTo(target, speed);
            public void InvokeGameBotPathTo(Vector3 target, short speed) => base.PathTo(target, speed);
            public void InvokeGameBotFollow(GameObject target) => base.Follow(target, 25, 125);
            public void InvokeGameBotStartAttack(GameObject target) => base.StartAttack(target);
            public override bool CastSpell(Spell spell, SpellLine line, ISpellCastingAbilityHandler ability = null, bool checkLos = true)
            {
                if (CaptureCasts) { LastRequested = spell; return true; }
                return base.CastSpell(spell, line, ability, checkLos);
            }
            public int PowerTick() => PowerRegenerationTimerCallback(null);
            public int HealthTick() => HealthRegenerationTimerCallback(null);
            public int EnduranceTick() => EnduranceRegenerationTimerCallback(null);
        }
        private sealed class Player : GamePlayer
        {
            public IPacketLib Packets;
            public override IPacketLib Out => Packets ?? base.Out;
            public short? SpeedForFollow;
            public override short MaxSpeed => SpeedForFollow ?? base.MaxSpeed;
            private Player() : base(null, null) { }
            public bool Moving = true;
            public int PositionX = 100;
            public override bool IsAlive => true;
            public override bool IsMoving => Moving;
            public override bool IsAttacking => false;
            public override bool IsCasting => false;
            public override bool InCombat => false;
            public override byte Level => 50;
            public override int X => PositionX;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
        }
        private sealed class BuffPet : GameNPC
        {
            public override void OnMaxSpeedChange() { }
            public bool Alive = true;
            public int PositionX;
            public override bool IsAlive => Alive;
            public override int X => PositionX;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
        }
        private sealed class Brain : BotBrain
        {
            public override bool CheckSpells(eCheckSpellType type) => true;
            public bool GenericAllows(Spell spell) => CanCastDefensiveSpell(spell);
            public bool InstantAllows(Spell spell) => CheckInstantDefensiveSpells(spell);
        }

        private sealed class DruidCommandBrain : BotBrain
        {
            public override bool CanAggroTarget(GameLiving target) => target?.IsAlive == true;
        }

        private sealed class DruidTestPetBrain : ControlledMobBrain
        {
            public GameObject CommandTarget;
            public DruidTestPetBrain(GameLiving owner) : base(owner) { }
            public override void Attack(GameObject target) { CommandTarget = target; }
        }
        private sealed class Handler : SpellHandler
        {
            public bool Interrupted;
            public Handler(GameLiving bot, Spell spell) : base(bot, spell, Line) { }
            protected override void InterruptCasting(bool moving) { Interrupted = true; }
        }

        [SetUp] public void SetUp()
        {
            _previous = GameServer.Instance; _time = GameLoop.GameLoopTime;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
            _language = new PetTestLanguageScope();
            typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime)).SetValue(null, 100_000L);
        }
        [TearDown] public void TearDown()
        {
            foreach (GameLiving actor in _actors)
            {
                ServiceObjectStore.Remove(actor.castingComponent);
                ServiceObjectStore.Remove(actor.effectListComponent);
            }
            _actors.Clear();
            _language.Dispose();
            typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime)).SetValue(null, _time);
            GameServer.LoadTestDouble(_previous);
        }
        private T Actor<T>() where T : GameLiving
        {
            T actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            actor.ObjectState = GameObject.eObjectState.Active;
            foreach (string mutex in new[] { "_changeHealthLock", "_changeManaLock", "_changeEnduranceLock", "_abilitiesLock" })
                Field(typeof(GameLiving), actor, mutex, new System.Threading.Lock());
            foreach (string p in new[] { "ItemBonus", "AbilityBonus", "BaseBuffBonusCategory", "SpecBuffBonusCategory", "OtherBonus", "DebuffCategory", "SpecDebuffCategory" })
                Field(typeof(GameLiving), actor, "<" + p + ">k__BackingField", new PropertyIndexer());
            Field(typeof(GameLiving), actor, "<TempProperties>k__BackingField", new PropertyCollection());
            Field(typeof(GameLiving), actor, "<BuffBonusMultCategory1>k__BackingField", new MultiplicativePropertiesHybrid());
            Field(typeof(GameLiving), actor, "m_effects", new GameEffectList(actor));
            Field(typeof(GameLiving), actor, "m_abilities", new Dictionary<string, Ability>());
            Field(typeof(GameLiving), actor, "<ActivePulseSpells>k__BackingField", new ConcurrentDictionary<eSpellType, Spell>());
            if (actor is GameNPC)
            {
                Field(typeof(GameNPC), actor, "m_brains", new ArrayList());
                Field(typeof(GameNPC), actor, "m_spells", new List<Spell>());
            }
            actor.castingComponent = CastingComponent.Create(actor);
            if (actor is GameNPC npc) npc.castingComponent = (NpcCastingComponent)actor.castingComponent;
            actor.effectListComponent = EffectListComponent.Create(actor);
            actor.attackComponent = new AttackComponent(actor);
            if (actor is GameNPC movingNpc)
            {
                movingNpc.movementComponent = new NpcMovementComponent(movingNpc);
                actor.movementComponent = movingNpc.movementComponent;
            }
            _actors.Add(actor);
            return actor;
        }
        private Bot NewBot(Type type)
        {
            Bot bot = Actor<Bot>(); bot.Class = (ICharacterClass)Activator.CreateInstance(type);
            bot.Name = "Song test"; bot.Health = bot.Mana = bot.Endurance = 100;
            Field(typeof(GameNPC), bot, "m_ownBrain", new Brain { Body = bot });
            return bot;
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ReplacedSpeedSongCannotRemoveTheCurrentSingersMovementBonus(bool playerTarget, bool processFirst)
        {
            var firstSinger = NewBot(typeof(ClassMinstrel));
            var secondSinger = NewBot(typeof(ClassMinstrel));
            GameLiving target;
            if (playerTarget)
            {
                var player = Actor<Player>();
                player.Packets = DispatchProxy.Create<IPacketLib, UT_RealmExchangeEconomy.NoOpPacketLib>();
                target = player;
            }
            else { var pet = Actor<BuffPet>(); pet.Alive = true; target = pet; }
            var song = new Spell(new DbSpell { SpellID = 1105, Type = "SpeedEnhancement", Target = "Group",
                Range = 2000, Value = 204, Pulse = 1, Frequency = 60, Duration = 6, ClientEffect = 123 }, 1);
            var first = new SpeedEnhancementECSEffect(new(target, 6000, 1, new SpeedEnhancementSpellHandler(firstSinger, song, Line)));
            var second = new SpeedEnhancementECSEffect(new(target, 6000, 1, new SpeedEnhancementSpellHandler(secondSinger, song, Line)));
            try
            {
                Assert.That(first.Start(), Is.True);
                if (processFirst) target.effectListComponent.BeginTick();
                Assert.That(second.Start(), Is.True);
                target.effectListComponent.BeginTick();
                Assert.That(target.BuffBonusMultCategory1.Get((int)eProperty.MaxSpeed), Is.EqualTo(2.04), "Initial refreshed song must apply speed");
                // The old singer's pulse still holds its previous child instance.
                // Expiring that stale child must not clear the refreshed song.
                first.End();
                target.effectListComponent.BeginTick();
                Assert.That(second.IsActive, Is.True);
                if (playerTarget) Assert.That(target.effectListComponent.TryGetEffectFromEffectId(second.Icon), Is.SameAs(second));
                Assert.That(target.BuffBonusMultCategory1.Get((int)eProperty.MaxSpeed), Is.EqualTo(2.04), "Retired song must not remove the current speed");
                second.End();
                target.effectListComponent.BeginTick();
                Assert.That(target.BuffBonusMultCategory1.Get((int)eProperty.MaxSpeed), Is.EqualTo(1));
            }
            finally { ServiceObjectStore.Remove<ECSGameEffect>(first); ServiceObjectStore.Remove<ECSGameEffect>(second); }
        }

        [Test]
        public void SpeedSongFallbackReenablesThenExpiresWithoutStackingOrLeavingSpeedBehind()
        {
            var singer = NewBot(typeof(ClassMinstrel));
            var strongerSinger = NewBot(typeof(ClassMinstrel));
            var target = Actor<BuffPet>(); target.Alive = true;
            SpeedEnhancementECSEffect Make(int id, int value)
            {
                var song = new Spell(new DbSpell { SpellID = id, Type = "SpeedEnhancement", Target = "Group",
                    Value = value, Pulse = 1, Duration = 6 }, 1);
                return new(new(target, 6000, 1, new SpeedEnhancementSpellHandler(value == 204 ? strongerSinger : singer, song, Line)));
            }
            var weak = Make(1101, 144); var strong = Make(1105, 204); var refreshedWeak = Make(1101, 144);
            try
            {
                weak.Start(); target.effectListComponent.BeginTick();
                strong.Start(); target.effectListComponent.BeginTick();
                Assert.That(target.BuffBonusMultCategory1.Get((int)eProperty.MaxSpeed), Is.EqualTo(2.04));
                refreshedWeak.Start(); target.effectListComponent.BeginTick();
                weak.End(); target.effectListComponent.BeginTick();
                strong.End(); target.effectListComponent.BeginTick();
                Assert.That(refreshedWeak.IsActive, Is.True);
                Assert.That(target.BuffBonusMultCategory1.Get((int)eProperty.MaxSpeed), Is.EqualTo(1.44));
                refreshedWeak.End(); target.effectListComponent.BeginTick();
                Assert.That(target.BuffBonusMultCategory1.Get((int)eProperty.MaxSpeed), Is.EqualTo(1));
            }
            finally { foreach (ECSGameEffect effect in new[] { weak, strong, refreshedWeak }) ServiceObjectStore.Remove(effect); }
        }
        [TestCase(false)]
        [TestCase(true)]
        public void PartyBuffsIncludeOwnBotAndPlayerPetsButRejectDeadFarOrForeignPets(bool temporary)
        {
            Bot caster = NewBot(typeof(ClassCleric));
            Bot companion = NewBot(typeof(ClassMinstrel));
            Player player = Actor<Player>();
            Field(typeof(GameBot), caster, "<IsTemporaryGroupHelper>k__BackingField", temporary);
            var group = new Group(player);
            Field(typeof(Group), group, "_groupMembers", new List<GameLiving> { player, caster, companion });
            player.Group = caster.Group = companion.Group = group;
            BuffPet Attach(GameLiving owner)
            {
                BuffPet pet = Actor<BuffPet>();
                pet.Alive = true;
                var brain = new ControlledMobBrain(owner) { Body = pet };
                Field(typeof(GameNPC), pet, "m_ownBrain", brain);
                Field(typeof(GameLiving), owner, "m_controlledBrain", new IControlledBrain[] { brain });
                return pet;
            }
            BuffPet own = Attach(caster), humanPet = Attach(player), botPet = Attach(companion);
            var realm = new Spell(new DbSpell { Type = "StrengthBuff", Target = "Realm", Range = 1500, Value = 10 }, 1);
            Assert.That(BotGroupPetBuffTargets.Enumerate(caster, realm), Is.EquivalentTo(new[] { own, humanPet, botPet }));
            botPet.Alive = false;
            humanPet.PositionX = 2000;
            Assert.That(BotGroupPetBuffTargets.Enumerate(caster, realm), Is.EqualTo(new[] { own }));
            botPet.Alive = true;
            companion.Group = null;
            Assert.That(BotGroupPetBuffTargets.Enumerate(caster, realm), Is.EqualTo(new[] { own }));
            humanPet.PositionX = 0;
            player.PositionX = 2000;
            var groupBuff = new Spell(new DbSpell { Type = "StrengthBuff", Target = "Group", Range = 1500, Value = 10 }, 1);
            Assert.That(BotGroupPetBuffTargets.Enumerate(caster, groupBuff), Is.EqualTo(new[] { own }),
                "Do not repeatedly request group buffs for pets whose owner is outside native expansion range.");
        }

        [TestCase("Pet")]
        [TestCase("Controlled")]
        public void DedicatedOwnPetMaintenanceStillSelectsOwnPet(string target)
        {
            Bot caster = NewBot(typeof(ClassCabalist));
            BuffPet own = Actor<BuffPet>();
            own.Alive = true;
            var petBrain = new ControlledMobBrain(caster) { Body = own };
            Field(typeof(GameNPC), own, "m_ownBrain", petBrain);
            Field(typeof(GameLiving), caster, "m_controlledBrain", new IControlledBrain[] { petBrain });
            var spell = new Spell(new DbSpell { Type = "StrengthBuff", Target = target, Range = 1500, Value = 10 }, 1);
            object selected = typeof(BotBrain).GetMethod("FindMissingMaintenanceTarget", Hidden)
                .Invoke(caster.Brain, new object[] { spell });
            Assert.That(selected, Is.SameAs(own));
            Assert.That(BotGroupPetBuffTargets.Enumerate(caster, spell), Is.Empty);
        }

        private static Spell Song(string type = "HealthRegenBuff", double cast = 0, int instrument = 0,
            bool focus = false, int id = 99971, int power = 0) =>
            new(new DbSpell { SpellID = id, Name = "Test chant", Type = type, Target = "Group",
                Pulse = 1, Frequency = 50, Duration = 6, Value = 5, CastTime = cast,
                InstrumentRequirement = instrument, IsFocus = focus, Range = 1500, Power = power }, 1);
        private static void Active(GameLiving bot, Spell spell) =>
            Field(typeof(CastingComponent), bot.castingComponent, "<SpellHandler>k__BackingField", new SpellHandler(bot, spell, Line));

        [TestCase(40, 1)] [TestCase(40, 39)]
        [TestCase(80, 1)] [TestCase(80, 79)]
        public void SpeedSongTargetsTheWholeNativeRaidAndAttachedPets(int capacity, int singerSlot)
        {
            Player owner = Actor<Player>();
            var members = new List<GameLiving> { owner };
            var pets = new List<GameNPC>();
            var group = new Group(owner);
            for (int i = 1; i < capacity; i++)
            {
                Bot member = NewBot(typeof(ClassMinstrel));
                Field(typeof(GameBot), member, "<IsTemporaryGroupHelper>k__BackingField", true);
                Field(typeof(GameBot), member, "<Owner>k__BackingField", owner);
                members.Add(member);
                if (i % 8 == 7)
                {
                    BuffPet pet = Actor<BuffPet>(); pet.Alive = true;
                    var brain = new ControlledMobBrain(member) { Body = pet };
                    Field(typeof(GameNPC), pet, "m_ownBrain", brain);
                    Field(typeof(GameLiving), member, "m_controlledBrain", new IControlledBrain[] { brain });
                    pets.Add(pet);
                }
            }
            Field(typeof(Group), group, "_groupMembers", members);
            foreach (GameLiving member in members) member.Group = group;
            Assert.That(group.EnableCompanionRaid(owner, capacity), Is.True);
            var song = new Spell(new DbSpell { SpellID = 1105, Type = "SpeedEnhancement", Target = "Group",
                Range = 2000, Value = 204, Pulse = 1, Frequency = 60, Duration = 6,
                CastTime = 3, InstrumentRequirement = 1 }, 1);
            var handler = new SpeedEnhancementSpellHandler(members[singerSlot], song, Line);
            var expected = members.Concat(pets).ToArray();
            Assert.That(handler.SelectTargets(members[singerSlot]).ToArray(), Is.EquivalentTo(expected));
            Assert.That(handler.GetGroupAndPets(song).ToArray(), Is.EquivalentTo(expected));
            owner.PositionX = 2100;
            Assert.That(handler.SelectTargets(members[singerSlot]), Does.Not.Contain(owner),
                "Raid support still obeys the actual song range; it is not a global speed cheat.");
        }

        [TestCase(typeof(ClassMinstrel))] [TestCase(typeof(ClassBard))] [TestCase(typeof(ClassSkald))]
        public void TravelingPerformerPreflightFollowsDuringSongWithoutDuplicateMovement(Type type)
        {
            Bot bot = NewBot(type); Player player = Actor<Player>(); player.Moving = true; player.PositionX = 1000;
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", true);
            Field(typeof(GameBot), bot, "<PlayerGroupLeader>k__BackingField", player);
            BotBrain brain = (BotBrain)bot.Brain;
            brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);
            bot.Walks = 0;
            Active(bot, Song());
            var preflight = typeof(BotBrain).GetMethod("FollowTravelingCompanionPerformer", Hidden);
            preflight.Invoke(brain, null);
            brain.FSM.Think();
            Assert.That(bot.Walks, Is.EqualTo(1));
            Assert.That(bot.IsCasting, Is.True);

            typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime)).SetValue(null, 100_500L);
            bot.Walks = 0;
            Active(bot, Song("Heal", 3));
            preflight.Invoke(brain, null);
            Assert.That(bot.Walks, Is.Zero, "Do not interrupt a real heal to follow.");

            Active(bot, Song()); player.Moving = false;
            preflight.Invoke(brain, null);
            Assert.That(bot.Walks, Is.Zero, "A parked leader retains the existing rest and formation rules.");
        }
        [TestCase(typeof(ClassCleric))]
        [TestCase(typeof(ClassHealer))]
        [TestCase(typeof(ClassDruid))]
        public void CompanionTravelBlocksAndCancelsBuffsButAllowsHealsAndResurrection(Type type)
        {
            Bot bot = NewBot(type);
            Player player = Actor<Player>(); player.Moving = true;
            CompanionGroup(bot, player);
            var buff = new Spell(new DbSpell { Type = "StrengthBuff", Target = "Realm", CastTime = 3, Duration = 600 }, 1);
            Assert.That(CompanionFollowPolicy.DeferBuff(bot, buff), Is.True);
            Active(bot, buff);
            CompanionFollowPolicy.ObserveAndCancelBuffs(bot);
            Assert.That(bot.castingComponent.SpellHandler, Is.Null);
            foreach (string spellType in new[] { "Heal", "HealOverTime", "Resurrect", "CureDisease" })
            {
                var heal = new Spell(new DbSpell { Type = spellType, Target = "Realm", CastTime = 3, Duration = 60 }, 1);
                Assert.That(CompanionFollowPolicy.DeferBuff(bot, heal), Is.False, spellType);
                Active(bot, heal);
                CompanionFollowPolicy.ObserveAndCancelBuffs(bot);
                Assert.That(bot.castingComponent.SpellHandler?.Spell, Is.SameAs(heal), spellType);
            }
            player.Moving = false;
            typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime)).SetValue(null, 100_700L);
            Assert.That(CompanionFollowPolicy.ObserveAndCancelBuffs(bot), Is.True);
            Assert.That(CompanionFollowPolicy.DeferBuff(bot, buff), Is.False);
            player.Moving = true;
            Active(bot, buff);
            CompanionFollowPolicy.ObserveAndCancelBuffs(bot);
            Assert.That(bot.castingComponent.SpellHandler, Is.Null);
        }

        [TestCase(typeof(ClassMinstrel))]
        [TestCase(typeof(ClassBard))]
        [TestCase(typeof(ClassSkald))]
        public void MovingCompanionKeepsMobileTwisting(Type type)
        {
            Bot bot = NewBot(type); Player player = Actor<Player>(); player.Moving = true;
            CompanionGroup(bot, player);
            Spell song = Song("SpeedEnhancement", type == typeof(ClassSkald) ? 0 : 3,
                type == typeof(ClassSkald) ? 0 : 1);
            Active(bot, song);
            Assert.That(CompanionFollowPolicy.DeferBuff(bot, song), Is.False);
            CompanionFollowPolicy.ObserveAndCancelBuffs(bot);
            Assert.That(bot.castingComponent.SpellHandler?.Spell, Is.SameAs(song));
        }

        [TestCase(typeof(ClassEnchanter))]
        [TestCase(typeof(ClassSorcerer))]
        [TestCase(typeof(ClassHealer))]
        [TestCase(typeof(ClassRunemaster))]
        public void MovingCompanionStopsForOnlyItsNativeGroupSpeed(Type type)
        {
            Bot bot = NewBot(type); Player player = Actor<Player>(); player.Moving = true;
            CompanionGroup(bot, player);
            Spell speed = Song("SpeedEnhancement", cast: 3, id: 99986);
            Spell buff = new(new DbSpell { SpellID = 99987, Type = "StrengthBuff", Target = "Realm",
                Range = 1500, Duration = 600, CastTime = 3 }, 1);
            bot.MiscSpells = new List<Spell> { buff, speed };
            bot.CaptureCasts = true;

            Assert.That(CompanionFollowPolicy.CanStopForNativeTravelSpeed(bot, speed), Is.True);
            Assert.That(CompanionFollowPolicy.DeferBuff(bot, buff), Is.True);
            Assert.That(CompanionFollowPolicy.DeferBuff(bot, speed), Is.False);
            Active(bot, speed);
            Assert.That(CompanionFollowPolicy.ObserveAndCancelBuffs(bot, out bool started), Is.False);
            Assert.That(started, Is.True);
            Assert.That(bot.castingComponent.SpellHandler?.Spell, Is.SameAs(speed),
                "The movement observer must not cancel a legal native speed cast.");
            CompanionFollowPolicy.ObserveAndCancelBuffs(bot, out started);
            Assert.That(started, Is.False, "Only the movement edge refreshes the upkeep scan.");

            bot.castingComponent.ClearSpellHandlers();
            Assert.That(typeof(BotBrain).GetMethod("TryMaintainTravelAndClassBuffs", Hidden)
                .Invoke(bot.Brain, null), Is.EqualTo(true));
            Assert.That(bot.LastRequested, Is.SameAs(speed));
            Assert.That(bot.Stops, Is.GreaterThanOrEqualTo(2), "A non-mobile pulse needs a real casting stop.");

            bot.LastRequested = null;
            Active(bot, speed);
            bot.castingComponent.SpellHandler.Target = bot;
            Assert.That(typeof(BotBrain).GetMethod("FindMissingMaintenanceTarget", Hidden)
                .Invoke(bot.Brain, new object[] { speed }), Is.Null,
                "A briefly missing group-member child effect must not restart the caster's pulse.");

            MethodInfo follow = typeof(BotBrain).GetMethod("FollowFormation", Hidden);
            player.PositionX = 1_000;
            bot.Walks = 0;
            follow.Invoke(bot.Brain, new object[] { false });
            Assert.That(bot.Walks, Is.Zero, "Following must not interrupt the native three-second cast.");
            Assert.That(CompanionFollowPolicy.HasFormationOrder(bot), Is.False);
            bot.castingComponent.ClearSpellHandlers();
            Assert.That(CompanionFollowPolicy.NativeTravelSpeedCastInProgress(bot), Is.False);
            follow.Invoke(bot.Brain, new object[] { false });
            Assert.That(CompanionFollowPolicy.HasFormationOrder(bot), Is.True,
                "The normal formation order resumes when its cast completes.");
        }

        [Test]
        public void NativeTravelSpeedNeverExpandsToDistantOrAutonomousBots()
        {
            Bot bot = NewBot(typeof(ClassEnchanter)); Player player = Actor<Player>(); player.Moving = true;
            CompanionGroup(bot, player);
            Spell speed = Song("SpeedEnhancement", cast: 3, id: 99988);
            bot.MiscSpells = new List<Spell> { speed };
            bot.CaptureCasts = true;
            player.PositionX = 1_000;
            Assert.That(CompanionFollowPolicy.CanStopForNativeTravelSpeed(bot, speed), Is.False);
            Assert.That(CompanionFollowPolicy.DeferBuff(bot, speed), Is.True,
                "Catch up before beginning a three-second stationary cast outside group range.");
            Assert.That(typeof(BotBrain).GetMethod("TryMaintainTravelAndClassBuffs", Hidden)
                .Invoke(bot.Brain, null), Is.EqualTo(false));
            Assert.That(bot.LastRequested, Is.Null);

            player.PositionX = 100;
            Field(typeof(GameBot), bot, "<IsAutonomousWorldBot>k__BackingField", true);
            Assert.That(CompanionFollowPolicy.CanStopForNativeTravelSpeed(bot, speed), Is.False);
            Assert.That(CompanionFollowPolicy.DeferBuff(bot, speed), Is.False,
                "Persistent world bots retain their existing speed maintenance path.");
            Assert.That(CompanionFollowPolicy.IsOrdinaryBuff(bot, speed), Is.True);
        }

        [TestCase(typeof(ClassBard))]
        [TestCase(typeof(ClassSkald))]
        [TestCase(typeof(ClassMinstrel))]
        public void StationaryCompanionFinishesOrdinaryBuffBatchBeforeResumingSongs(Type type)
        {
            Bot bot = NewBot(type); Player player = Actor<Player>(); player.Moving = false;
            CompanionGroup(bot, player);
            bot.CaptureCasts = true;
            Field(typeof(BotBrain), bot.Brain, "_nextInstrumentKitCheck", long.MaxValue);
            Spell buff = new(new DbSpell { SpellID = 99980, Type = "StrengthBuff", Target = "Group",
                Range = 1500, Duration = 600, Value = 10, CastTime = 3 }, 1);
            Spell song = Song(id: 99981);
            bot.MiscSpells = new List<Spell> { song, buff };
            MethodInfo maintain = typeof(BotBrain).GetMethod("TryMaintainTravelAndClassBuffs", Hidden);
            MethodInfo twist = typeof(BotBrain).GetMethod("TryMaintainClassicSongTwist", Hidden);
            Assert.That(maintain.Invoke(bot.Brain, null), Is.EqualTo(true));
            Assert.That(bot.LastRequested, Is.SameAs(buff));
            bot.LastRequested = null;
            twist.Invoke(bot.Brain, null);
            Assert.That(bot.LastRequested, Is.Null, "The song cannot interleave during the buff backoff.");

            bot.MiscSpells = new List<Spell> { song };
            typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime)).SetValue(null, 102_000L);
            Assert.That(maintain.Invoke(bot.Brain, null), Is.EqualTo(false));
            twist.Invoke(bot.Brain, null);
            Assert.That(bot.LastRequested, Is.SameAs(song), "The song resumes after the last buff is done.");
        }

        [TestCase(typeof(ClassBard))]
        [TestCase(typeof(ClassSkald))]
        [TestCase(typeof(ClassMinstrel))]
        public void MovingOwnerBreaksBuffBatchAndStartsSpeedInsteadOfPower(Type type)
        {
            Bot bot = NewBot(type); Player player = Actor<Player>(); player.Moving = false;
            CompanionGroup(bot, player);
            bot.CaptureCasts = true;
            Field(typeof(BotBrain), bot.Brain, "_nextInstrumentKitCheck", long.MaxValue);
            Spell buff = new(new DbSpell { SpellID = 99982, Type = "StrengthBuff", Target = "Group",
                Range = 1500, Duration = 600, Value = 10, CastTime = 3 }, 1);
            bot.MiscSpells = new List<Spell> { buff };
            MethodInfo maintain = typeof(BotBrain).GetMethod("TryMaintainTravelAndClassBuffs", Hidden);
            Assert.That(maintain.Invoke(bot.Brain, null), Is.EqualTo(true));

            Spell speed = Song("SpeedEnhancement", id: 99983);
            Spell secondary = Song(type == typeof(ClassBard) ? "EnduranceRegenBuff" : "HealthRegenBuff", id: 99984);
            Spell power = Song("PowerRegenBuff", id: 99985);
            bot.MiscSpells = new List<Spell> { power, secondary, speed };
            bot.LastRequested = null;
            player.Moving = true;
            typeof(BotBrain).GetMethod("TryMaintainClassicSongTwist", Hidden).Invoke(bot.Brain, null);
            Assert.That(bot.LastRequested, Is.SameAs(speed), "Owner movement releases the buff batch immediately.");
        }

        [TestCase(8)] [TestCase(40)] [TestCase(80)]
        public void FormationPolicyCoversEveryCompanionSizeAndExcludesGamebots(int size)
        {
            Bot bot = NewBot(typeof(ClassDruid)); Player player = Actor<Player>(); player.Moving = true;
            Group group = CompanionGroup(bot, player);
            if (size > 8) Assert.That(group.EnableCompanionRaid(player, size), Is.True);
            Assert.That(CompanionFollowPolicy.Applies(bot), Is.True);
            Assert.That(CompanionFollowPolicy.SendsDruidPet(bot), Is.True);
            Assert.That(BotPartyRoles.IsSupport(bot), Is.True, "Pet offense must not turn the Druid into a melee actor.");
            CompanionFollowPolicy.BeginFormation(bot, new(200, 0, 0));
            Assert.That(CompanionFollowPolicy.HasFormationOrder(bot), Is.True);
            Field(typeof(GameBot), bot, "<IsAutonomousWorldBot>k__BackingField", true);
            Assert.That(CompanionFollowPolicy.Applies(bot), Is.False);
            Assert.That(CompanionFollowPolicy.HasFormationOrder(bot), Is.False);
            Assert.That(CompanionFollowPolicy.SendsDruidPet(bot), Is.False);
            var buff = new Spell(new DbSpell { Type = "StrengthBuff", Target = "Realm", CastTime = 3 }, 1);
            Assert.That(CompanionFollowPolicy.DeferBuff(bot, buff), Is.False);
            Assert.That(CompanionFollowPolicy.SpeedLimit(bot, 191), Is.EqualTo(191));
        }

        [Test]
        public void FormationSpeedLeaseExpiresAndCannotBoostCastingAttackingOrGamebots()
        {
            Bot bot = NewBot(typeof(ClassCleric)); Player player = Actor<Player>(); player.SpeedForFollow = 400;
            CompanionGroup(bot, player);
            Field(typeof(GameLiving), bot, "<BuffBonusMultCategory1>k__BackingField", new MultiplicativePropertiesHybrid());
            Assert.That(CompanionFollowPolicy.SpeedLimit(bot, 191), Is.EqualTo(191));
            CompanionFollowPolicy.BeginFormation(bot, new(960, 0, 0));
            Assert.That(CompanionFollowPolicy.SpeedLimit(bot, 191), Is.EqualTo(480));
            bot.Attacking = true;
            Assert.That(CompanionFollowPolicy.SpeedLimit(bot, 191), Is.EqualTo(191));
            bot.Attacking = false;
            Active(bot, new Spell(new DbSpell { Type = "Heal", Target = "Realm", CastTime = 3 }, 1));
            Assert.That(CompanionFollowPolicy.SpeedLimit(bot, 191), Is.EqualTo(191));
            bot.castingComponent.ClearSpellHandlers();
            typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime)).SetValue(null, 100_751L);
            Assert.That(CompanionFollowPolicy.SpeedLimit(bot, 191), Is.EqualTo(191));
        }

        [Test]
        public void DruidCommandsOnlyPetKeepsSupportRoleAndHonorsDefensiveMode()
        {
            Bot bot = NewBot(typeof(ClassDruid)); Player player = Actor<Player>();
            CompanionGroup(bot, player);
            var brain = new DruidCommandBrain { Body = bot };
            Field(typeof(GameNPC), bot, "m_ownBrain", brain);
            BuffPet pet = Actor<BuffPet>(); pet.Alive = true;
            var petBrain = new DruidTestPetBrain(bot) { Body = pet };
            Field(typeof(GameLiving), bot, "m_controlledBrain", new IControlledBrain[] { petBrain });
            BuffPet target = Actor<BuffPet>(); target.Alive = true; target.PositionX = 600;
            var command = typeof(BotBrain).GetMethod("TryCommandCompanionDruidPet", Hidden);
            command.Invoke(brain, new object[] { target });
            Assert.That(petBrain.CommandTarget, Is.SameAs(target));
            Assert.That(BotPartyRoles.IsSupport(bot), Is.True);
            Assert.That(bot.Attacking, Is.False);
            Assert.That(bot.Walks, Is.Zero);
            petBrain.CommandTarget = null;
            CompanionEngagementMode.Set(player, true);
            command.Invoke(brain, new object[] { target });
            Assert.That(petBrain.CommandTarget, Is.Null);
            target.PositionX = 200;
            command.Invoke(brain, new object[] { target });
            Assert.That(petBrain.CommandTarget, Is.SameAs(target));
            petBrain.CommandTarget = null;
            Field(typeof(GameBot), bot, "<IsAutonomousWorldBot>k__BackingField", true);
            command.Invoke(brain, new object[] { target });
            Assert.That(petBrain.CommandTarget, Is.Null);
        }

        [Test]
        public void ServantTravelCancellationPreservesQueuedHealBehindSelfBuff()
        {
            Bot bot = NewBot(typeof(ClassNecromancer)); Player player = Actor<Player>(); player.Moving = true;
            CompanionGroup(bot, player);
            BuffPet pet = Actor<BuffPet>(); pet.Alive = true;
            var brain = new NecromancerPetBrain(bot) { Body = pet };
            Field(typeof(GameNPC), pet, "m_ownBrain", brain);
            var buff = new Spell(new DbSpell { Type = "StrengthBuff", Target = "Self", CastTime = 3 }, 1);
            var heal = new Spell(new DbSpell { Type = "Heal", Target = "Self", CastTime = 3 }, 1);
            Active(pet, buff);
            pet.castingComponent.SpellHandler.Target = pet;
            var queue = typeof(NecromancerPetBrain).GetMethod("AddToSpellQueue", Hidden);
            queue.Invoke(brain, new object[] { heal, Line, pet });
            brain.CancelCompanionTravelBuffs();
            Assert.That(pet.castingComponent.SpellHandler, Is.Null);
            Assert.That(brain.HasSpellsQueued(), Is.True, "The queued heal must survive native queue cleanup.");
        }

        private Group CompanionGroup(Bot bot, Player player)
        {
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", true);
            Field(typeof(GameBot), bot, "<Owner>k__BackingField", player);
            Field(typeof(GameBot), bot, "<PlayerGroupLeader>k__BackingField", player);
            var group = new Group(player);
            Field(typeof(Group), group, "_groupMembers", new List<GameLiving> { player, bot });
            player.Group = bot.Group = group;
            return group;
        }

        private static void Field(Type type, object target, string name, object value) =>
            type.GetField(name, Hidden).SetValue(target, value);

        [TestCase(typeof(ClassBard), "HealthRegenBuff", 3, 1)]
        [TestCase(typeof(ClassMinstrel), "HealthRegenBuff", 3, 1)]
        [TestCase(typeof(ClassSkald), "HealthRegenBuff", 0, 0)]
        [TestCase(typeof(ClassPaladin), "EnduranceRegenBuff", 0, 0)]
        [TestCase(typeof(ClassPaladin), "SpecArmorFactorBuff", 0, 0)]
        [TestCase(typeof(ClassPaladin), "CombatHeal", 0, 0)]
        [TestCase(typeof(ClassWarden), "Bladeturn", 0, 0)]
        public void MobileSongsRemainNonCombatWhileLogicalRecoveryNeverSetsSitting(Type type, string spellType, double cast, int instrument)
        {
            foreach (bool temporary in new[] { false, true })
            {
                Bot bot = NewBot(type);
                Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", temporary);
                bot.Mana = 20; bot.Health = 40; bot.Endurance = 20;
                Assert.That(bot.BeginRecoveryRest(), Is.True);
                Active(bot, Song(spellType, cast, instrument));
                Assert.That(BotSongTwistPolicy.HasMobileSongCast(bot), Is.True);
                Assert.That(BotRestRecovery.BlocksRest(bot), Is.False);
                Assert.That(bot.IsEnhancedResting, Is.True);
                Assert.That(bot.IsRecoveryResting, Is.True);
                Assert.That(bot.InCombat, Is.False);
                Assert.That(bot.LastAttackTick, Is.Zero);
                bot.PowerTick(); bot.HealthTick(); bot.EnduranceTick();
                Assert.That(bot.Mana, Is.EqualTo(30));
                Assert.That(bot.Health, Is.EqualTo(50));
                Assert.That(bot.Endurance, Is.EqualTo(30));
                Assert.That(bot.IsSitting, Is.False);
                Assert.That(bot.Walks, Is.Zero);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RecoveryStateNeverSetsGameBotSitting(bool temporary)
        {
            Bot bot = NewBot(typeof(ClassSkald));
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", temporary);
            Assert.That(temporary ? bot.BeginTemporaryCompanionRest() : bot.BeginRecoveryRest(), Is.True);
            Assert.That(bot.IsRecoveryResting, Is.True);
            Assert.That(bot.IsSitting, Is.False);
            Assert.That(typeof(GameBot).GetProperty(nameof(GameBot.IsSitting)).DeclaringType,
                Is.EqualTo(typeof(GameLiving)));
        }

        [Test]
        public void StrictTemporaryCompanionRestRejectsStaleMovementFollowAndAttack()
        {
            Bot bot = NewBot(typeof(ClassSkald));
            Player leader = Actor<Player>();
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", true);
            Assert.That(bot.BeginTemporaryCompanionRest(), Is.True);

            bot.InvokeGameBotWalkTo(new Vector3(50, 0, 0), 200);
            bot.InvokeGameBotPathTo(new Vector3(60, 0, 0), 200);
            bot.InvokeGameBotFollow(leader);
            bot.InvokeGameBotStartAttack(leader);

            Assert.That(bot.IsTemporaryCompanionRestLocked, Is.True);
            Assert.That(bot.IsRecoveryResting, Is.True);
            Assert.That(bot.IsSitting, Is.False);
            Assert.That(bot.IsAttacking, Is.False);
        }

        [Test]
        public void ExplicitWakeReleasesStrictTemporaryCompanionRest()
        {
            Bot bot = NewBot(typeof(ClassSkald));
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", true);
            Assert.That(bot.BeginTemporaryCompanionRest(), Is.True);

            bot.WakeTemporaryCompanionRest();

            Assert.That(bot.IsTemporaryCompanionRestLocked, Is.False);
            Assert.That(bot.IsSitting, Is.False);
        }

        [Test]
        public void TemporaryCompanionCanTwistMobileSongWhileRecovering()
        {
            Bot bot = NewBot(typeof(ClassSkald));
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", true);
            Assert.That(bot.BeginTemporaryCompanionRest(), Is.True);
            Assert.That(bot.CastSpell(Song("HealthRegenBuff"), Line, checkLos: false), Is.True);
            Assert.That(bot.IsTemporaryCompanionRestLocked, Is.True);
            Assert.That(bot.IsRecoveryResting, Is.True);
            Assert.That(bot.IsSitting, Is.False);
            Assert.That(bot.castingComponent.IsCasting || bot.castingComponent.HasPendingSkillRequests, Is.True);
        }

        [Test]
        public void ValidBuffPetOrCombatCastWakesTemporaryCompanionRest()
        {
            Bot bot = NewBot(typeof(ClassSkald));
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", true);
            Assert.That(bot.BeginTemporaryCompanionRest(), Is.True);

            Assert.That(bot.CastSpell(Song("DirectDamage"), Line, checkLos: false), Is.True);
            Assert.That(bot.IsTemporaryCompanionRestLocked, Is.False);
            Assert.That(bot.IsSitting, Is.False);
            Assert.That((long)typeof(BotBrain).GetField("_temporaryCompanionLastActionTick", Hidden)
                .GetValue(bot.Brain), Is.EqualTo(GameLoop.GameLoopTime));
        }

        [Test]
        public void UnaffordableRequiredCastKeepsRestLockedUntilPowerIsAvailable()
        {
            Bot bot = NewBot(typeof(ClassSkald));
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", true);
            Spell required = Song("DirectDamage", power: 25);
            bot.Mana = 0;
            Assert.That(bot.BeginTemporaryCompanionRest(), Is.True);

            Assert.That(bot.CastSpell(required, Line, checkLos: false), Is.False);
            Assert.That(bot.IsTemporaryCompanionRestLocked, Is.True);
            Assert.That(bot.IsRecoveryResting, Is.True);
            Assert.That(bot.IsSitting, Is.False);

            bot.Mana = 100;
            Assert.That(bot.CastSpell(required, Line, checkLos: false), Is.True);
            Assert.That(bot.IsTemporaryCompanionRestLocked, Is.False);
            Assert.That(bot.IsSitting, Is.False);

            long castActivity = (long)typeof(BotBrain)
                .GetField("_temporaryCompanionLastActionTick", Hidden).GetValue(bot.Brain);
            Assert.That(BotRestRecovery.ShouldTemporaryCompanionRest(
                true, true, false, false, castActivity + BotRestRecovery.QuietMilliseconds,
                castActivity, 100, 100, 75, 100, 100, 100), Is.True);
            Assert.That(BotRestRecovery.ShouldTemporaryCompanionRest(
                true, true, false, false, castActivity + BotRestRecovery.QuietMilliseconds,
                castActivity, 100, 100, 100, 100, 100, 100), Is.False);
        }

        [Test]
        public void PendingAndQueuedRequestsCannotHideOrdinaryCastsOrAnAbility()
        {
            Bot bot = NewBot(typeof(ClassSkald));
            Assert.That(BotSongTwistPolicy.HasMobileSongCast(bot), Is.False);
            Assert.That(bot.castingComponent.RequestCastSpell(Song(), Line, target: bot, checkLos: false), Is.True);
            Assert.That(BotSongTwistPolicy.HasMobileSongCast(bot), Is.True);
            Active(bot, Song());
            Field(typeof(CastingComponent), bot.castingComponent, "<QueuedSpellHandler>k__BackingField",
                new SpellHandler(bot, Song("DirectDamage"), Line));
            Assert.That(BotSongTwistPolicy.HasMobileSongCast(bot), Is.False);
            Field(typeof(CastingComponent), bot.castingComponent, "<QueuedSpellHandler>k__BackingField", null);
            bot.castingComponent.RequestUseAbility(null); // Deliberately never processed.
            Assert.That(BotSongTwistPolicy.HasMobileSongCast(bot), Is.False);
            Assert.That(BotRestRecovery.BlocksRest(bot), Is.True);
        }

        [TestCase("DirectDamage", 0, false)]
        [TestCase("HealthRegenBuff", 0, true)]
        [TestCase("HealthRegenBuff", 3, false)]
        public void HostileFocusAndStationarySpellsAreNotMobileRestExceptions(string type, double cast, bool focus)
        {
            Bot bot = NewBot(typeof(ClassSkald)); Active(bot, Song(type, cast, focus: focus));
            Assert.That(BotSongTwistPolicy.HasMobileSongCast(bot), Is.False);
            Assert.That(BotRestRecovery.BlocksRest(bot), Is.True);
        }

        [Test]
        public void OrdinaryActorsAndWrongClassChantsRemainExcluded()
        {
            Assert.That(BotSongTwistPolicy.IsMobileSong(Actor<GameNPC>(), Song()), Is.False);
            Assert.That(BotSongTwistPolicy.IsMobileSong(Actor<Player>(), Song()), Is.False);
            Assert.That(BotSongTwistPolicy.IsMobileSong(NewBot(typeof(ClassWizard)), Song()), Is.False);
            Assert.That(BotSongTwistPolicy.IsMobileSong(NewBot(typeof(ClassPaladin)), Song("Bladeturn")), Is.False);
            Assert.That(BotSongTwistPolicy.IsMobileSong(NewBot(typeof(ClassWarden)), Song("EnduranceRegenBuff")), Is.False);
            Assert.That(BotBrain.IsClassicSongClass(eCharacterClass.Paladin), Is.False);
            Assert.That(BotBrain.IsClassicSongClass(eCharacterClass.Warden), Is.False);
        }

        [TestCase(typeof(ClassPaladin), "SpecArmorFactorBuff")]
        [TestCase(typeof(ClassPaladin), "CombatHeal")]
        [TestCase(typeof(ClassPaladin), "BodyResistBuff")]
        [TestCase(typeof(ClassPaladin), "HeatColdMatterBuff")]
        [TestCase(typeof(ClassWarden), "EnduranceRegenBuff")]
        public void GenericUpkeepCannotReplaceTheManagedPulseSource(Type type, string spellType)
        {
            Bot bot = NewBot(type);
            var spell = Song(spellType);
            Assert.That(BotSongTwistPolicy.IsReservedPulse(bot, spell), Is.True);
            Assert.That(((Brain)bot.Brain).GenericAllows(spell), Is.False);
            Assert.That(((Brain)bot.Brain).InstantAllows(spell), Is.False);
            Assert.That(bot.castingComponent.HasPendingSkillRequests, Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void FollowStateMovesAlongsideActiveOrPendingSong(bool pending)
        {
            Bot bot = NewBot(typeof(ClassSkald)); Player player = Actor<Player>(); player.Moving = true;
            // Keep every name-derived formation slot beyond the 20-unit
            // arrival tolerance; otherwise this test randomly starts in place.
            player.PositionX = 1000;
            Field(typeof(GameBot), bot, "<PlayerGroupLeader>k__BackingField", player);
            if (pending) bot.castingComponent.RequestCastSpell(Song(), Line, target: bot, checkLos: false);
            else Active(bot, Song());
            object state = Activator.CreateInstance(typeof(BotBrain).GetNestedType("BotState_Follow", BindingFlags.NonPublic),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { bot.Brain }, null);
            state.GetType().GetMethod("Think").Invoke(state, null);
            Assert.That(bot.Walks, Is.EqualTo(1));
            Assert.That(bot.castingComponent.IsCasting || bot.castingComponent.HasPendingSkillRequests, Is.True);
        }

        [Test]
        public void FollowStateRestOverridesSongWithoutWalking()
        {
            Bot bot = NewBot(typeof(ClassSkald)); Player player = Actor<Player>(); player.Moving = false;
            bot.Mana = 20;
            Field(typeof(GameBot), bot, "<PlayerGroupLeader>k__BackingField", player);
            Active(bot, Song());
            object state = Activator.CreateInstance(typeof(BotBrain).GetNestedType("BotState_Follow", BindingFlags.NonPublic),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { bot.Brain }, null);
            state.GetType().GetMethod("Think").Invoke(state, null);
            Assert.That(bot.IsRecoveryResting, Is.True);
            Assert.That(bot.IsSitting, Is.False);
            Assert.That(bot.Walks, Is.Zero);
            Assert.That(bot.castingComponent.IsCasting || bot.castingComponent.HasPendingSkillRequests, Is.True);
        }

        [Test]
        public void DefensiveCompanionsAndTheirPetsWaitForPullButWorldBotsAndHumanPetsAreUntouched()
        {
            Bot bot = NewBot(typeof(ClassCabalist));
            Player player = Actor<Player>(); player.Moving = false; player.PositionX = 0;
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", true);
            Field(typeof(GameBot), bot, "<PlayerGroupLeader>k__BackingField", player);
            var group = new Group(player);
            Field(typeof(Group), group, "_groupMembers", new List<GameLiving> { player, bot });
            player.Group = bot.Group = group;
            BuffPet enemy = Actor<BuffPet>(); enemy.Alive = true; enemy.PositionX = 900;
            BuffPet pet = Actor<BuffPet>();
            pet.SetOwnBrain(new ControlledMobBrain(bot) { Body = pet });
            BuffPet humanPet = Actor<BuffPet>();
            humanPet.SetOwnBrain(new ControlledMobBrain(player) { Body = humanPet });
            Assert.That(CompanionEngagementMode.Allows(bot, enemy), Is.True, "Default remains aggressive");
            CompanionEngagementMode.Set(player, true);
            CompanionEngagementMode.RememberPull(player, enemy);
            Assert.That(CompanionEngagementMode.Allows(bot, enemy), Is.False);
            Assert.That(CompanionEngagementMode.Allows(pet, enemy), Is.False);
            Assert.That(CompanionEngagementMode.Allows(humanPet, enemy), Is.True);
            Assert.That(CompanionEngagementMode.NearbyPull(bot), Is.Null);
            enemy.PositionX = 350;
            Assert.That(CompanionEngagementMode.NearbyPull(bot), Is.SameAs(enemy));
            Assert.That(CompanionEngagementMode.Allows(pet, enemy), Is.True);
            enemy.PositionX = 351;
            Assert.That(CompanionEngagementMode.Allows(pet, enemy), Is.False);
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", false);
            Assert.That(CompanionEngagementMode.Allows(bot, enemy), Is.True);
            Assert.That(CompanionEngagementMode.Allows(pet, enemy), Is.True);
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", true);
            CompanionEngagementMode.Set(player, false);
            Assert.That(CompanionEngagementMode.Allows(bot, enemy), Is.True);
        }

        [TestCase("&defensive")]
        [TestCase("&aggressive")]
        public void CompanionModeCommandsAreUniqueAndPlayerAccessible(string name)
        {
            var matches = new List<CmdAttribute>();
            foreach (Type type in typeof(GameBot).Assembly.GetTypes())
                foreach (CmdAttribute command in type.GetCustomAttributes(typeof(CmdAttribute), false))
                    if (command.Cmd == name || System.Array.IndexOf(command.Aliases ?? System.Array.Empty<string>(), name) >= 0)
                        matches.Add(command);
            Assert.That(matches.Count, Is.EqualTo(1));
            Assert.That(matches[0].Level, Is.EqualTo((uint)ePrivLevel.Player));
        }

        [TestCase(false, 100, false)]
        [TestCase(true, 100, true)]
        [TestCase(false, 500, true)]
        public void CabalistFormationCorrectionDoesNotKeepRefreshingRestActivity(
            bool leaderMoving, int distance, bool expectRefresh)
        {
            Bot bot = NewBot(typeof(ClassCabalist));
            Player player = Actor<Player>();
            player.Moving = leaderMoving;
            player.PositionX = distance;
            bot.Mana = 20;
            bot.Moving = true;
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", true);
            Field(typeof(GameBot), bot, "<PlayerGroupLeader>k__BackingField", player);
            var group = new Group(player);
            Field(typeof(Group), group, "_groupMembers", new List<GameLiving> { player, bot });
            player.Group = bot.Group = group;
            typeof(BotBrain).GetMethod("ResetLeaderActivity", Hidden).Invoke(bot.Brain, new object[] { player });
            Field(typeof(BotBrain), bot.Brain, "_temporaryCompanionLastActionTick", 123L);
            typeof(BotBrain).GetMethod("ObserveLeaderActivity", Hidden).Invoke(bot.Brain, null);
            long activity = (long)typeof(BotBrain).GetField("_temporaryCompanionLastActionTick", Hidden).GetValue(bot.Brain);
            Assert.That(activity == 123L, Is.EqualTo(!expectRefresh));
        }

        [TestCase(typeof(ClassBard), "HealthRegenBuff", 3, 1)]
        [TestCase(typeof(ClassMinstrel), "HealthRegenBuff", 3, 1)]
        [TestCase(typeof(ClassSkald), "HealthRegenBuff", 0, 0)]
        [TestCase(typeof(ClassPaladin), "SpecArmorFactorBuff", 0, 0)]
        [TestCase(typeof(ClassWarden), "Bladeturn", 0, 0)]
        public void NativeFollowTickDoesNotTakeCastingStopBranchForMobileSongs(Type type, string spellType, double cast, int instrument)
        {
            Bot bot = NewBot(type);
            GameNPC inactiveTarget = Actor<GameNPC>();
            inactiveTarget.ObjectState = GameObject.eObjectState.Inactive;
            var movement = bot.movementComponent;
            MethodInfo follow = typeof(NpcMovementComponent).GetMethod("FollowTick", Hidden);
            // An inactive target returns zero only AFTER passing the casting
            // stop gate. No world/navmesh is needed to exercise the actual gate.
            Field(typeof(NpcMovementComponent), movement, "<FollowTarget>k__BackingField", inactiveTarget);
            Active(bot, Song(spellType, cast, instrument));
            Assert.That(follow.Invoke(movement, null), Is.EqualTo(0));
            Field(typeof(NpcMovementComponent), movement, "<FollowTarget>k__BackingField", inactiveTarget);
            Active(bot, Song("DirectDamage", 3));
            Assert.That(follow.Invoke(movement, null), Is.EqualTo(GS.ServerProperties.Properties.GAMENPC_FOLLOWCHECK_TIME));
        }

        [TestCase(typeof(ClassBard))] [TestCase(typeof(ClassMinstrel))]
        public void InstrumentSongMovementDoesNotInterruptIt(Type type)
        {
            Bot bot = NewBot(type);
            bot.Instrument = new DbInventoryItem { Object_Type = (int)eObjectType.Instrument, DPS_AF = 1 };
            Spell spell = Song(cast: 3, instrument: 1);
            Handler handler = new(bot, spell);
            Assert.That(handler.CheckBeginCast(bot, true), Is.True);
            Assert.That(bot.CastSpell(spell, Line, checkLos: false), Is.True);
            Assert.That(bot.IsSitting, Is.False);
            bot.Moving = true;
            handler.CasterMoves();
            Assert.That(handler.Interrupted, Is.False);
            Assert.That(handler.CheckDuringCast(bot, true), Is.True);
            Assert.That(bot.Stops, Is.Zero);
            Handler ordinary = new(bot, Song("Heal", 3));
            ordinary.CasterMoves();
            Assert.That(ordinary.Interrupted, Is.True);
        }

        [Test]
        public void WardensDoNotAlternateAnActiveBladeturnAndPaladinsDoNotToggleActiveChant()
        {
            // A solo Paladin now holds the damage chant in a fight (see SoloPaladinSwitchesOnceToTheDamageChant).
            foreach (Type type in new[] { typeof(ClassWarden) })
            {
                Bot bot = NewBot(type); bot.CaptureCasts = true; bot.Attacking = true;
                Spell anchor = Song(type == typeof(ClassWarden) ? "Bladeturn" : "EnduranceRegenBuff");
                bot.InstantMiscSpells = new List<Spell> { anchor, Song("DamageAdd", id: 99972) };
                var source = new ECSPulseEffect(new(bot, 0, 1, new SpellHandler(bot, anchor, Line)), anchor.Frequency);
                source.Start();
                bot.effectListComponent.BeginTick();
                typeof(BotBrain).GetMethod("TryMaintainTankChant", Hidden).Invoke(bot.Brain, null);
                Assert.That(bot.LastRequested, Is.Null, "No secondary without a safe native child window; never toggle anchor");
                Assert.That(source.IsEnding, Is.False);
            }
        }

        [Test]
        public void SoloPaladinSwitchesOnceToTheDamageChantAndThenHoldsIt()
        {
            Bot bot = NewBot(typeof(ClassPaladin)); bot.CaptureCasts = true; bot.Attacking = true;
            Spell endurance = Song("EnduranceRegenBuff");
            Spell damage = Song("DamageAdd", id: 99972);
            bot.InstantMiscSpells = new List<Spell> { endurance, damage };
            var source = new ECSPulseEffect(new(bot, 0, 1, new SpellHandler(bot, endurance, Line)), endurance.Frequency);
            source.Start();
            bot.effectListComponent.BeginTick();
            typeof(BotBrain).GetMethod("TryMaintainTankChant", Hidden).Invoke(bot.Brain, null);
            Assert.That(bot.LastRequested?.ID, Is.EqualTo(99972), "a solo Paladin in a fight holds the damage chant");
        }

        [TestCase(typeof(ClassSkald), "HealthRegenBuff")]
        [TestCase(typeof(ClassPaladin), "EnduranceRegenBuff")]
        [TestCase(typeof(ClassPaladin), "SpecArmorFactorBuff")]
        [TestCase(typeof(ClassPaladin), "CombatHeal")]
        [TestCase(typeof(ClassWarden), "Bladeturn")]
        public void NativeBeginCastAllowsApprovedVocalChantWithoutSitting(Type type, string spellType)
        {
            Bot bot = NewBot(type);
            var spell = Song(spellType);
            var handler = new SpellHandler(bot, spell, Line);
            Assert.That(handler.CheckBeginCast(bot, true), Is.True);
            Assert.That(bot.CastSpell(spell, Line, checkLos: false), Is.True);
            Assert.That(bot.IsSitting, Is.False);
            Assert.That(bot.Stops, Is.Zero);
            Assert.That(bot.InCombat, Is.False);
        }

        [TestCase(true, false, true, true, eSpellType.SpeedEnhancement)]
        [TestCase(true, true, true, true, eSpellType.Bladeturn)]
        [TestCase(false, false, true, true, eSpellType.Bladeturn)]
        [TestCase(false, false, false, true, eSpellType.DamageAdd)]
        [TestCase(false, true, true, false, eSpellType.DamageAdd)]
        public void WardenUsesOneSituationalAnchor(bool moving, bool combat, bool grouped, bool pbt, eSpellType expected) =>
            Assert.That(BotSongTwistPolicy.WardenAnchor(moving, combat, grouped, pbt), Is.EqualTo(expected));

        [TestCase(typeof(ClassPaladin), "EnduranceRegenBuff")]
        [TestCase(typeof(ClassPaladin), "SpecArmorFactorBuff")]
        [TestCase(typeof(ClassPaladin), "CombatHeal")]
        [TestCase(typeof(ClassWarden), "Bladeturn")]
        public void TankChantMaintenanceDoesNotStopMeleeAndRespectsReuse(Type type, string spellType)
        {
            Bot bot = NewBot(type); bot.CaptureCasts = true; bot.Attacking = true; bot.Health = 50;
            Spell chant = Song(spellType);
            bot.InstantMiscSpells = new List<Spell> { chant };
            MethodInfo maintain = typeof(BotBrain).GetMethod("TryMaintainTankChant", Hidden);
            maintain.Invoke(bot.Brain, null);
            Assert.That(bot.LastRequested, Is.SameAs(chant));
            Assert.That(bot.Attacking, Is.True);
            Assert.That(bot.Stops, Is.Zero);
            bot.LastRequested = null; bot.Cooldown = 8000;
            Field(typeof(BotBrain), bot.Brain, "_nextSongTwistTick", 0L);
            maintain.Invoke(bot.Brain, null);
            Assert.That(bot.LastRequested, Is.Null);
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using DOL.GS.PropertyCalc;
using DOL.GS.RealmAbilities;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_NecromancerStatParity
    {
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private readonly List<GameLiving> _actors = new();
        private GameServer _previousServer;
        private PetTestLanguageScope _language;

        private sealed class InertServer : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }

        private sealed class TestPlayer : GamePlayer
        {
            private byte _level;

            public TestPlayer() : base(null, null) { }

            public override byte Level { get => _level; set => _level = value; }
            public override ICharacterClass CharacterClass => new ClassNecromancer();
            public override int GetModifiedFromItems(eProperty property) => ItemBonus[property];
        }

        private sealed class TestBot : GameBot
        {
            private TestBot() : base((OfflineWorldBotRecord)null) { }

            public override byte Level { get; set; }
            public override ICharacterClass CharacterClass => new ClassNecromancer();
            public override int GetModifiedFromItems(eProperty property) => ItemBonus[property];
        }

        private sealed class TestNecromancerPet : NecromancerPet
        {
            private TestNecromancerPet() : base(null) { }

            public override byte Level { get; set; }
        }

        private sealed class TestNpc : GameNPC
        {
            public override byte Level { get; set; }
        }

        [SetUp]
        public void SetUp()
        {
            _language = new PetTestLanguageScope();
            _previousServer = GameServer.Instance;
            GameServer.LoadTestDouble((InertServer)RuntimeHelpers.GetUninitializedObject(typeof(InertServer)));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameLiving actor in _actors)
            {
                if (actor.effectListComponent != null)
                    ServiceObjectStore.Remove(actor.effectListComponent);
            }

            _actors.Clear();
            GameServer.LoadTestDouble(_previousServer);
            _language.Dispose();
        }

        [TestCase(1)]
        [TestCase(10)]
        [TestCase(50)]
        public void NecromancerPet_ResistsMatchForEqualPlayerAndBotOwners(int level)
        {
            var pair = Pair(level);
            ConfigureResistScenario(pair.Player, pair.Bot, pair.PlayerPet, pair.BotPet);
            ResistCalculator calculator = new();

            Assert.Multiple(() =>
            {
                Assert.That(calculator.CalcValueFromItems(pair.PlayerPet, eProperty.Resist_Body),
                    Is.EqualTo(calculator.CalcValueFromItems(pair.BotPet, eProperty.Resist_Body)), "owner item-resist cap");
                Assert.That(calculator.CalcValueFromBuffs(pair.PlayerPet, eProperty.Resist_Body),
                    Is.EqualTo(calculator.CalcValueFromBuffs(pair.BotPet, eProperty.Resist_Body)), "pet-only buff cap and debuff");
                Assert.That(calculator.CalcValue(pair.PlayerPet, eProperty.Resist_Body),
                    Is.EqualTo(calculator.CalcValue(pair.BotPet, eProperty.Resist_Body)), "item, ability and other resist layers");
            });
        }

        [TestCase(1)]
        [TestCase(10)]
        [TestCase(50)]
        public void NecromancerPet_HealthAndArmorFactorMatchForEqualPlayerAndBotOwners(int level)
        {
            var pair = Pair(level);
            int conItems = Math.Max(1, (int)(level * 1.5));
            int hitPointItems = level * 4;
            pair.Player.ItemBonus[eProperty.Constitution] = conItems;
            pair.Bot.ItemBonus[eProperty.Constitution] = conItems;
            pair.Player.ItemBonus[eProperty.MaxHealth] = hitPointItems;
            pair.Bot.ItemBonus[eProperty.MaxHealth] = hitPointItems;
            pair.PlayerPet.BaseBuffBonusCategory[eProperty.ArmorFactor] = 17;
            pair.BotPet.BaseBuffBonusCategory[eProperty.ArmorFactor] = 17;
            pair.PlayerPet.SpecBuffBonusCategory[eProperty.ArmorFactor] = 9;
            pair.BotPet.SpecBuffBonusCategory[eProperty.ArmorFactor] = 9;

            MaxHealthCalculator health = new();
            ArmorFactorCalculator armorFactor = new();
            Assert.Multiple(() =>
            {
                Assert.That(health.CalcValue(pair.PlayerPet, eProperty.MaxHealth),
                    Is.EqualTo(health.CalcValue(pair.BotPet, eProperty.MaxHealth)), "owner CON and HP item transfer");
                Assert.That(armorFactor.CalcValue(pair.PlayerPet, eProperty.ArmorFactor),
                    Is.EqualTo(armorFactor.CalcValue(pair.BotPet, eProperty.ArmorFactor)), "native pet AF and pet-applied AF buffs");
            });
        }

        [TestCase(1)]
        [TestCase(10)]
        [TestCase(50)]
        public void NecromancerPet_ToaBonusesMatchForEqualPlayerAndBotOwners(int level)
        {
            var pair = Pair(level);
            foreach (GameLiving owner in new GameLiving[] { pair.Player, pair.Bot })
            {
                owner.ItemBonus[eProperty.BuffEffectiveness] = 9;
                owner.AbilityBonus[eProperty.BuffEffectiveness] = 7;
                owner.ItemBonus[eProperty.CastingSpeed] = 10;
                owner.AbilityBonus[eProperty.CastingSpeed] = 5;
                owner.ItemBonus[eProperty.SpellDamage] = 10;
                owner.AbilityBonus[eProperty.SpellDamage] = 6;
            }

            pair.PlayerPet.BaseBuffBonusCategory[eProperty.SpellDamage] = 3;
            pair.BotPet.BaseBuffBonusCategory[eProperty.SpellDamage] = 3;
            BuffEffectivenessPercentCalculator buffEffectiveness = new();
            SpellCastSpeedPercentCalculator castingSpeed = new();
            SpellDamagePercentCalculator spellDamage = new();
            Assert.Multiple(() =>
            {
                Assert.That(buffEffectiveness.CalcValue(pair.PlayerPet, eProperty.BuffEffectiveness),
                    Is.EqualTo(buffEffectiveness.CalcValue(pair.BotPet, eProperty.BuffEffectiveness)));
                Assert.That(castingSpeed.CalcValue(pair.PlayerPet, eProperty.CastingSpeed),
                    Is.EqualTo(castingSpeed.CalcValue(pair.BotPet, eProperty.CastingSpeed)));
                Assert.That(spellDamage.CalcValue(pair.PlayerPet, eProperty.SpellDamage),
                    Is.EqualTo(spellDamage.CalcValue(pair.BotPet, eProperty.SpellDamage)));
            });
        }

        [Test]
        public void NecromancerPet_MeleeCriticalBaselineMatchesForPlayerAndBotOwners()
        {
            var pair = Pair(50);
            CriticalMeleeHitChanceCalculator calculator = new();

            Assert.That(calculator.CalcValue(pair.PlayerPet, eProperty.CriticalMeleeHitChance),
                Is.EqualTo(calculator.CalcValue(pair.BotPet, eProperty.CriticalMeleeHitChance)));
        }

        [Test]
        public void NecromancerPet_WildArcanaMatchesForPlayerAndBotOwners()
        {
            var pair = Pair(50);
            pair.Player.AddAbility(WildArcana(), false);
            pair.Bot.AddAbility(WildArcana(), false);
            CriticalDebuffHitChanceCalculator calculator = new();

            Assert.That(calculator.CalcValue(pair.PlayerPet, eProperty.CriticalDebuffHitChance),
                Is.EqualTo(calculator.CalcValue(pair.BotPet, eProperty.CriticalDebuffHitChance)));
        }

        [Test]
        public void NecromancerPet_WithOrdinaryNpcOwnerDoesNotInheritOwnerItemResists()
        {
            TestNpc owner = Actor<TestNpc>();
            owner.Level = 50;
            owner.ItemBonus[eProperty.Resist_Body] = 50;
            TestNecromancerPet pet = Pet(owner, 50);

            Assert.That(new ResistCalculator().CalcValueFromItems(pet, eProperty.Resist_Body), Is.Zero);
        }

        private (TestPlayer Player, TestBot Bot, TestNecromancerPet PlayerPet, TestNecromancerPet BotPet) Pair(int level)
        {
            TestPlayer player = Actor<TestPlayer>();
            player.Level = (byte)level;
            TestBot bot = Actor<TestBot>();
            bot.Level = (byte)level;
            return (player, bot, Pet(player, level), Pet(bot, level));
        }

        private static void ConfigureResistScenario(TestPlayer player, TestBot bot, TestNecromancerPet playerPet, TestNecromancerPet botPet)
        {
            foreach (GameLiving owner in new GameLiving[] { player, bot })
            {
                owner.ItemBonus[eProperty.Resist_Body] = 99;
                owner.AbilityBonus[eProperty.Resist_Body] = 12;
                owner.OtherBonus[eProperty.Resist_Body] = 8;
            }

            foreach (TestNecromancerPet pet in new[] { playerPet, botPet })
            {
                pet.BaseBuffBonusCategory[eProperty.Resist_Body] = 40;
                pet.DebuffCategory[eProperty.Resist_Body] = -6;
            }
        }

        private TestNecromancerPet Pet(GameLiving owner, int level)
        {
            TestNecromancerPet pet = Actor<TestNecromancerPet>();
            pet.Level = (byte)level;
            ControlledMobBrain brain = new(owner) { Body = pet };
            Field(typeof(GameNPC), pet, "m_ownBrain", brain);
            return pet;
        }

        private static AtlasOF_WildArcanaAbility WildArcana() => new(
            new DbAbility { KeyName = "Wild Arcana", Name = "Wild Arcana" }, 3);

        private T Actor<T>() where T : GameLiving
        {
            T actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            actor.ObjectState = GameObject.eObjectState.Active;
            Field(typeof(GameLiving), actor, "<BaseBuffBonusCategory>k__BackingField", new PropertyIndexer());
            Field(typeof(GameLiving), actor, "<SpecBuffBonusCategory>k__BackingField", new PropertyIndexer());
            Field(typeof(GameLiving), actor, "<ItemBonus>k__BackingField", new PropertyIndexer());
            Field(typeof(GameLiving), actor, "<AbilityBonus>k__BackingField", new PropertyIndexer());
            Field(typeof(GameLiving), actor, "<OtherBonus>k__BackingField", new PropertyIndexer());
            Field(typeof(GameLiving), actor, "<DebuffCategory>k__BackingField", new PropertyIndexer());
            Field(typeof(GameLiving), actor, "<SpecDebuffCategory>k__BackingField", new PropertyIndexer());
            Field(typeof(GameLiving), actor, "m_abilities", new Dictionary<string, Ability>());
            Field(typeof(GameLiving), actor, "_abilitiesLock", new Lock());
            if (actor is GameNPC)
                Field(typeof(GameNPC), actor, "m_brains", new ArrayList());
            actor.effectListComponent = EffectListComponent.Create(actor);
            _actors.Add(actor);
            return actor;
        }

        private static void Field(Type type, object target, string name, object value) => type
            .GetField(name, PrivateInstance).SetValue(target, value);
    }
}

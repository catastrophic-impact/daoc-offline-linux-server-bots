using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Database.Handlers;
using DOL.GS;
using DOL.GS.PlayerClass;
using DOL.GS.PropertyCalc;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.UnitTests;

/// <summary>
/// Guards the native Animist ownership and summon-stat paths used by both a
/// human character and an ownerless persistent world bot.  These are not AI
/// simulations: the assertions deliberately exercise the real pet brains and
/// GameSummonedPet level calculation.
/// </summary>
[TestFixture, NonParallelizable]
public sealed class UT_AnimistRewardParity
{
    private static readonly IObjectDatabase EmptyDatabase =
        DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
    private GameServer _previousServer;
    private IPropertyCalculator _previousMaxHealthCalculator;
    private short _previousPetConBase;

    private sealed class InertServer : GameServer
    {
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
    }

    private sealed class TestPlayer : GamePlayer
    {
        private TestPlayer() : base(null, null) { }
        public override byte Level { get; set; }
        public override ICharacterClass CharacterClass => new ClassAnimist();
    }

    private sealed class TestBot : GameBot
    {
        private TestBot() : base((OfflineWorldBotRecord)null) { }
        public override byte Level { get; set; }
        public override ICharacterClass CharacterClass => new ClassAnimist();
    }

    [SetUp]
    public void SetUp()
    {
        _previousServer = GameServer.Instance;
        _previousPetConBase = DOL.GS.ServerProperties.Properties.PET_AUTOSET_CON_BASE;
        DOL.GS.ServerProperties.Properties.PET_AUTOSET_CON_BASE = 30; // Declared native default, normally loaded at startup.
        GameServer.LoadTestDouble((InertServer)RuntimeHelpers.GetUninitializedObject(typeof(InertServer)));
        IPropertyCalculator[] calculators = (IPropertyCalculator[])typeof(GameLiving)
            .GetField("m_propertyCalc", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        _previousMaxHealthCalculator = calculators[(int)eProperty.MaxHealth];
        calculators[(int)eProperty.MaxHealth] = new MaxHealthCalculator();
    }

    [TearDown]
    public void TearDown()
    {
        ((IPropertyCalculator[])typeof(GameLiving)
            .GetField("m_propertyCalc", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null))[(int)eProperty.MaxHealth] =
            _previousMaxHealthCalculator;
        GameServer.LoadTestDouble(_previousServer);
        DOL.GS.ServerProperties.Properties.PET_AUTOSET_CON_BASE = _previousPetConBase;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BomberAndFnFTurretResolveTheirNativeRewardOwner(bool persistentBot)
    {
        GameLiving owner = persistentBot ? Actor<TestBot>() : Actor<TestPlayer>();
        owner.Level = 20;
        GameLiving expected = owner;

        Spell bomberSpell = new(new DbSpell
        {
            SpellID = 11317,
            Name = "Wisp Heat",
            Target = eSpellTarget.ENEMY.ToString(),
            Type = eSpellType.Bomber.ToString(),
            SubSpellID = 11341,
        }, 1);
        SpellLine creeping = new("Creeping Path", "test", "test", true);

        IControlledBrain bomber = new BomberBrain(owner, bomberSpell, creeping);
        IControlledBrain fnf = new TurretFNFBrain(owner) { IsMainPet = false };

        Assert.Multiple(() =>
        {
            Assert.That(bomber.GetLivingOwner(), Is.SameAs(expected), "Bomber payload must credit the caster/root owner.");
            Assert.That(fnf.GetLivingOwner(), Is.SameAs(expected), "FnF damage must credit the caster/root owner.");
            Assert.That(bomber.Owner, Is.SameAs(owner));
            Assert.That(fnf.Owner, Is.SameAs(owner));
        });
    }

    [TestCase(1, 1)]
    [TestCase(10, 8)]
    [TestCase(50, 44)]
    public void FnFPetLevelAndHealthMatchForEqualHumanAndPersistentBotOwners(int ownerLevel, int expectedPetLevel)
    {
        TestPlayer player = Actor<TestPlayer>();
        TestBot bot = Actor<TestBot>();
        player.Level = (byte)ownerLevel;
        bot.Level = (byte)ownerLevel;

        TurretFnfPet playerPet = FnF(player);
        TurretFnfPet botPet = FnF(bot);

        Assert.Multiple(() =>
        {
            Assert.That(playerPet.Level, Is.EqualTo(expectedPetLevel), "live FnF summon formula: owner level x 88%, capped at 44");
            Assert.That(botPet.Level, Is.EqualTo(playerPet.Level));
            Assert.That(botPet.MaxHealth, Is.EqualTo(playerPet.MaxHealth), "same turret type and equal owner level must not change HP by owner kind");
            Assert.That(botPet.MaxHealth, Is.GreaterThan(0));
            Assert.That(botPet.MaxHealthScalingFactor, Is.EqualTo(0.36 * 1.33));
        });
    }

    [Test] public void EveryShroomTypeReceivesTheSameHealthMultiplier()
    {
        var fnf = (TurretFnfPet)RuntimeHelpers.GetUninitializedObject(typeof(TurretFnfPet));
        var caster = (TurretMainPetCaster)RuntimeHelpers.GetUninitializedObject(typeof(TurretMainPetCaster));
        var tank = (TurretMainPetTank)RuntimeHelpers.GetUninitializedObject(typeof(TurretMainPetTank));
        Assert.That(fnf.MaxHealthScalingFactor, Is.EqualTo(0.36 * 1.33));
        Assert.That(caster.MaxHealthScalingFactor, Is.EqualTo(0.8 * 1.33));
        Assert.That(tank.MaxHealthScalingFactor, Is.EqualTo(1.33));
        var ordinaryPet = (GameSummonedPet)RuntimeHelpers.GetUninitializedObject(typeof(GameSummonedPet));
        Assert.That(ordinaryPet.MaxHealthScalingFactor, Is.EqualTo(1.0));
    }

    private static TurretFnfPet FnF(GameLiving owner)
    {
        TurretFnfPet pet = Actor<TurretFnfPet>();
        IControlledBrain brain = new TurretFNFBrain(owner) { Body = pet, IsMainPet = false };
        Field(typeof(GameNPC), pet, "m_ownBrain", brain);
        pet.SummonSpellDamage = -88;
        pet.SummonSpellValue = 44;
        pet.SetPetLevel();
        // This fixture bypasses constructors; reproduce template-load stat
        // initialization even when the clamped level is already one.
        pet.SetStats();
        return pet;
    }

    private static T Actor<T>() where T : GameLiving
    {
        T actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        Field(typeof(GameLiving), actor, "<BaseBuffBonusCategory>k__BackingField", new PropertyIndexer());
        Field(typeof(GameLiving), actor, "<SpecBuffBonusCategory>k__BackingField", new PropertyIndexer());
        Field(typeof(GameLiving), actor, "<ItemBonus>k__BackingField", new PropertyIndexer());
        Field(typeof(GameLiving), actor, "<AbilityBonus>k__BackingField", new PropertyIndexer());
        Field(typeof(GameLiving), actor, "<OtherBonus>k__BackingField", new PropertyIndexer());
        Field(typeof(GameLiving), actor, "<DebuffCategory>k__BackingField", new PropertyIndexer());
        Field(typeof(GameLiving), actor, "<SpecDebuffCategory>k__BackingField", new PropertyIndexer());
        Field(typeof(GameLiving), actor, "m_charStat", new short[8]);
        if (actor is GameNPC npc)
            Field(typeof(GameNPC), npc, "m_brains", new ArrayList());
        return actor;
    }

    private static void Field(Type type, object target, string name, object value) => type
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        .SetValue(target, value);
}

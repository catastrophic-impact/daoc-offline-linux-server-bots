using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using DOL.Logging;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable]
public class UT_PlayerPetSpellScaling
{
    private sealed class Server : GameServer
    {
        protected override IObjectDatabase DataBaseImpl => DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
    }
    private GameServer _previous;
    private PetTestLanguageScope _language;
    [OneTimeSetUp]
    public void InitializeLogging() => LoggerManager.InitializeWithExplicitLibrary(null, LogLibrary.None);
    [SetUp]
    public void Setup()
    {
        _language = new PetTestLanguageScope();
        _previous = GameServer.Instance;
        GameServer.LoadTestDouble(Empty<Server>());
    }
    [TearDown]
    public void Cleanup()
    {
        GameServer.LoadTestDouble(_previous);
        _language.Dispose();
    }

    private sealed class Player : GamePlayer
    {
        private Player() : base(null, null) { }
        public ICharacterClass Class;
        public override ICharacterClass CharacterClass => Class;
        public byte TestLevel = 7;
        public override byte Level { get => TestLevel; set => TestLevel = value; }
    }

    private sealed class Bot : GameBot
    {
        private Bot() : base((OfflineWorldBotRecord)null) { }
        public ICharacterClass Class;
        public override ICharacterClass CharacterClass => Class;
        public byte TestLevel;
        public override byte Level => TestLevel;
    }

    private sealed class Pet : GameSummonedPet
    {
        private Pet() : base((INpcTemplate)null) { }
        public override byte Level { get; set; }
    }

    private sealed class CasterMinion : BdCasterSubPet
    {
        private CasterMinion() : base(null) { }
        public override byte Level { get; set; }
    }
    private sealed class HealerMinion : BdHealerSubPet
    {
        private HealerMinion() : base(null) { }
        public override byte Level { get; set; }
    }

    private static T Empty<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void Attach(GameNPC npc, GameLiving owner)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(GameNPC).GetField("m_brains", hidden).SetValue(npc, new ArrayList());
        typeof(GameNPC).GetField("m_ownBrain", hidden).SetValue(npc, new ControlledMobBrain(owner) { Body = npc });
    }

    private static Pet Create(GameLiving owner)
    {
        Pet pet = Empty<Pet>();
        Attach(pet, owner);
        pet.SummonSpellDamage = -88;
        pet.SummonSpellValue = 50;
        pet.Level = 6;
        return pet;
    }

    private static ICharacterClass Class(eCharacterClass value) => value switch
    {
        eCharacterClass.Enchanter => new ClassEnchanter(),
        eCharacterClass.Cabalist => new ClassCabalist(),
        eCharacterClass.Bonedancer => new ClassBonedancer(),
        eCharacterClass.Spiritmaster => new ClassSpiritmaster(),
        eCharacterClass.Theurgist => new ClassTheurgist(),
        eCharacterClass.Druid => new ClassDruid(),
        eCharacterClass.Animist => new ClassAnimist(),
        eCharacterClass.Hunter => new ClassHunter(),
        _ => new ClassNecromancer()
    };

    [Test]
    public void LevelSevenPlayerCompanionGetsLevelSixScaledNuke()
    {
        Player owner = Empty<Player>();
        owner.TestLevel = 7;
        owner.Class = new ClassEnchanter();
        Pet pet = Create(owner);
        pet.Level = 0;
        Assert.That(pet.SetPetLevel(), Is.True);
        Assert.That(pet.Level, Is.EqualTo(6));
        Spell template = new(new DbSpell { SpellID = 60020, Type = "DamageSpeedDecrease", Target = "Enemy", Damage = 179, Value = 35, CastTime = 3.5 }, 1);
        pet.Spells = [template];
        Assert.That(pet.HarmfulSpells[0].Damage, Is.EqualTo(24.41));
        Assert.That(pet.HarmfulSpells[0].Value, Is.EqualTo(35), "Do not change the snare");
        for (int i = 0; i < 3; i++) pet.SortSpells();
        Assert.That(pet.HarmfulSpells[0].Damage, Is.EqualTo(24.41), "Do not scale twice");
        Assert.That(template.Damage, Is.EqualTo(179), "Never change the shared template");
        pet.Level = 44;
        pet.SortSpells();
        Assert.That(pet.HarmfulSpells[0].Damage, Is.EqualTo(179), "Keep endgame base damage");
    }

    [TestCase(eCharacterClass.Enchanter)]
    [TestCase(eCharacterClass.Cabalist)]
    [TestCase(eCharacterClass.Bonedancer)]
    [TestCase(eCharacterClass.Spiritmaster)]
    [TestCase(eCharacterClass.Theurgist)]
    [TestCase(eCharacterClass.Druid)]
    public void PlayerAndBotOwnersScaleAllSpellListsAndNestedPets(eCharacterClass characterClass)
    {
        Player player = Empty<Player>();
        player.Class = Class(characterClass);
        Bot bot = Empty<Bot>();
        bot.Class = Class(characterClass);
        // A companion's human owner must not override its own class.
        Player human = Empty<Player>();
        human.Class = new ClassAnimist();
        Attach(bot, human);
        foreach (GameLiving owner in new GameLiving[] { player, bot, Create(player), Create(bot) })
        {
            Pet pet = Create(owner);
            pet.Spells = [
                new(new DbSpell { Type = "DirectDamage", Target = "Enemy", Damage = 176, CastTime = 2 }, 1),
                new(new DbSpell { Type = "DirectDamage", Target = "Enemy", Damage = 176, CastTime = 0 }, 1),
                new(new DbSpell { Type = "Heal", Target = "Realm", Value = 176, CastTime = 2 }, 1),
                new(new DbSpell { Type = "Heal", Target = "Realm", Value = 176, CastTime = 0 }, 1),
                new(new DbSpell { Type = "StrengthBuff", Target = "Self", Value = 176, CastTime = 2 }, 1),
                new(new DbSpell { Type = "StrengthBuff", Target = "Self", Value = 176, CastTime = 0 }, 1)
            ];
            Assert.That(pet.HarmfulSpells[0].Damage, Is.EqualTo(24));
            Assert.That(pet.InstantHarmfulSpells[0].Damage, Is.EqualTo(24));
            Assert.That(pet.HealSpells[0].Value, Is.EqualTo(24));
            Assert.That(pet.InstantHealSpells[0].Value, Is.EqualTo(24));
            Assert.That(pet.MiscSpells[0].Value, Is.EqualTo(24));
            Assert.That(pet.InstantMiscSpells[0].Value, Is.EqualTo(24));
        }
    }

    [TestCase(eCharacterClass.Animist)]
    [TestCase(eCharacterClass.Hunter)]
    [TestCase(eCharacterClass.Necromancer)]
    public void ClassesWithSeparateSpellRulesAreStillExcluded(eCharacterClass characterClass)
    {
        Player player = Empty<Player>();
        player.Class = Class(characterClass);
        Pet pet = Create(player);
        pet.Spells = [new(new DbSpell { Type = "DirectDamage", Target = "Enemy", Damage = 179, CastTime = 2 }, 1)];
        Assert.That(pet.HarmfulSpells[0].Damage, Is.EqualTo(179));
    }

    [TestCase(6)]
    [TestCase(17)]
    [TestCase(44)]
    public void AllUnderhillSpellTemplatesScaleWithoutChangingSharedData(int level)
    {
        Player player = Empty<Player>();
        player.Class = new ClassEnchanter();
        Pet pet = Create(player);
        pet.Level = (byte)level;
        Spell zealot = new(new DbSpell { SpellID = 61019, Type = "DirectDamageWithDebuff", Target = "Enemy", Damage = 179, Value = 15, Duration = 15, CastTime = 3.5 }, 1);
        Spell heal = new(new DbSpell { SpellID = 60015, Type = "Heal", Target = "Realm", Value = 140, CastTime = 3 }, 1);
        Spell regen = new(new DbSpell { SpellID = 60014, Type = "HealthRegenBuff", Target = "Realm", Value = 6, Duration = 600, CastTime = 2 }, 1);
        Spell armor = new(new DbSpell { SpellID = 60018, Type = "SpecArmorFactorBuff", Target = "Self", Value = 41, Duration = 1200, CastTime = 0 }, 1);
        pet.Spells = [zealot, heal, regen, armor];
        double scale = level / 44.0;
        for (int i = 0; i < 3; i++)
        {
            Assert.That(pet.HarmfulSpells[0].Damage, Is.EqualTo(Math.Round(179 * scale, 2)));
            Assert.That(pet.HarmfulSpells[0].Value, Is.EqualTo(Math.Round(15 * scale, 2)));
            Assert.That(pet.HealSpells[0].Value, Is.EqualTo(Math.Round(140 * scale, 2)));
            Assert.That(pet.HealSpells[1].Value, Is.EqualTo(Math.Round(6 * scale, 2)));
            Assert.That(pet.InstantMiscSpells[0].Value, Is.EqualTo(Math.Round(41 * scale, 2)));
            pet.SortSpells();
        }
        Assert.That(zealot.Damage, Is.EqualTo(179));
        Assert.That(heal.Value, Is.EqualTo(140));
        Assert.That(regen.Value, Is.EqualTo(6));
        Assert.That(armor.Value, Is.EqualTo(41));
    }

    [TestCase(false, 20, 15)]
    [TestCase(false, 50, 37)]
    [TestCase(true, 20, 15)]
    [TestCase(true, 50, 37)]
    public void BonedancerRealSubPetTypesScaleThroughCommander(bool isBot, int ownerLevel, int petLevel)
    {
        Player player = Empty<Player>();
        player.Class = new ClassBonedancer();
        player.TestLevel = (byte)ownerLevel;
        Bot bot = Empty<Bot>();
        bot.Class = new ClassBonedancer();
        bot.TestLevel = (byte)ownerLevel;
        Attach(bot, player);
        CommanderPet commander = Empty<CommanderPet>();
        Attach(commander, isBot ? bot : player);
        CasterMinion caster = Empty<CasterMinion>();
        HealerMinion healer = Empty<HealerMinion>();
        foreach (GameSummonedPet pet in new GameSummonedPet[] { caster, healer })
        {
            Attach(pet, commander);
            pet.SummonSpellDamage = -75;
            pet.SummonSpellValue = 45;
            pet.SetPetLevel();
            Assert.That(pet.Level, Is.EqualTo(petLevel));
            Assert.That(pet.GetSpellScalingFactor(), Is.EqualTo(petLevel / 37.0));
        }
        // Actual installed template values: 60125, 60119, 60105, 10308.
        caster.Spells = [
            new(new DbSpell { Type = "DamageSpeedDecrease", Target = "Enemy", Damage = 120, Value = 35, CastTime = 4 }, 1),
            new(new DbSpell { Type = "DirectDamage", Target = "Enemy", Damage = 145, CastTime = 3 }, 1)
        ];
        healer.Spells = [
            new(new DbSpell { Type = "Heal", Target = "Realm", Value = 140, CastTime = 4 }, 1),
            new(new DbSpell { Type = "HealthRegenBuff", Target = "Realm", Value = 6, CastTime = 4 }, 1)
        ];
        Assert.That(caster.HarmfulSpells[0].Damage, Is.EqualTo(Math.Round(120 * petLevel / 37.0, 2)));
        Assert.That(caster.HarmfulSpells[1].Damage, Is.EqualTo(Math.Round(145 * petLevel / 37.0, 2)));
        Assert.That(healer.HealSpells[0].Value, Is.EqualTo(Math.Round(140 * petLevel / 37.0, 2)));
        Assert.That(healer.HealSpells[1].Value, Is.EqualTo(Math.Round(6 * petLevel / 37.0, 2)));
    }
}

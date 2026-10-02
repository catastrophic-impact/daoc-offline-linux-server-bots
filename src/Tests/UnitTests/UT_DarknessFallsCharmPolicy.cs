using DOL.Database;
using DOL.GS;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public sealed class UT_DarknessFallsCharmPolicy
{
    private static DbMob Creature(string name, int model, int level) => new()
    {
        Name = name,
        Model = (ushort)model,
        Level = (byte)level,
        Region = DarknessFallsCharmPolicy.RegionId,
        Realm = 0,
        ClassType = DbMob.DEFAULT_NPC_CLASSTYPE
    };

    private static Spell Charm(int id, int type, int maxLevel = 50) =>
        new(new DbSpell { SpellID = id, Type = "Charm", Target = "Enemy",
            AmnesiaChance = type, Value = maxLevel }, 1);

    [TestCase(568, 1)]
    [TestCase(104, 1)]
    [TestCase(103, 1)]
    [TestCase(649, 1)]
    [TestCase(134, 1)]
    [TestCase(587, 7)]
    [TestCase(640, 7)]
    [TestCase(641, 7)]
    public void FamiliarAppearanceDeterminesPeriodCharmType(int model, int type)
    {
        Assert.That(DarknessFallsCharmPolicy.TryGetCharmBodyType(
            Creature("demoniac familiar", model, 30), out ushort actual), Is.True);
        Assert.That(actual, Is.EqualTo(type));
    }

    [Test]
    public void DemonAndHumanoidAreNotMistakenForAnimals()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DarknessFallsCharmPolicy.TryGetCharmBodyType(
                Creature("avernal quasit", 639, 34), out ushort demon) && demon == 2, Is.True);
            Assert.That(DarknessFallsCharmPolicy.TryGetCharmBodyType(
                Creature("necyomancer", 7, 32), out ushort human) && human == 6, Is.True);
            Assert.That(DarknessFallsCharmPolicy.TryGetCharmBodyType(
                Creature("plated fiend", 657, 28), out _), Is.False);
            Assert.That(DarknessFallsCharmPolicy.TryGetCharmBodyType(
                Creature("Legion", 634, 83), out _), Is.False);
        });
    }

    [Test]
    public void PlayerMenuAddsDungeonSpeciesWithoutReplacingOrNarrowingExistingChoices()
    {
        DbMob rat = Creature("demoniac familiar", 568, 15);
        DbMob ant = Creature("demoniac familiar", 587, 19);
        DbMob quasit = Creature("avernal quasit", 639, 34);
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(
                eCharacterClass.Sorcerer, 249), Is.True);
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(
                eCharacterClass.Sorcerer, 1), Is.True,
                "The old realm choices remain alongside the dungeon creatures.");
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(
                eCharacterClass.Mentalist, 249), Is.True);
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(
                eCharacterClass.Minstrel, 249), Is.True);
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(
                eCharacterClass.Hunter, 249), Is.True);
            Assert.That(DarknessFallsCharmPolicy.AllowsPlayerChoice(rat, Charm(952, 0, 15), eCharacterClass.Sorcerer), Is.True);
            Assert.That(DarknessFallsCharmPolicy.AllowsPlayerChoice(ant, Charm(952, 0, 15), eCharacterClass.Sorcerer), Is.True,
                "The installed spell's all-types value remains authoritative.");
            Assert.That(DarknessFallsCharmPolicy.AllowsPlayerChoice(ant, Charm(952, 4, 15), eCharacterClass.Sorcerer), Is.False);
            Assert.That(DarknessFallsCharmPolicy.AllowsPlayerChoice(ant, Charm(953, 5, 26), eCharacterClass.Sorcerer), Is.True);
            Assert.That(DarknessFallsCharmPolicy.AllowsPlayerChoice(quasit, Charm(953, 0, 40), eCharacterClass.Sorcerer), Is.True);
            Assert.That(DarknessFallsCharmPolicy.AllowsPlayerChoice(quasit, Charm(954, 0, 40), eCharacterClass.Sorcerer), Is.True);
            Assert.That(DarknessFallsCharmPolicy.AllowsPlayerChoice(quasit, Charm(4215, 7), eCharacterClass.Mentalist), Is.False);
            Assert.That(DarknessFallsCharmPolicy.AllowsPlayerChoice(quasit, Charm(4216, 0), eCharacterClass.Mentalist), Is.True);
            Assert.That(DarknessFallsCharmPolicy.AllowsPlayerChoice(quasit, Charm(1156, 6), eCharacterClass.Minstrel), Is.False);
        });
    }

    [Test]
    public void BotSyntheticPoolUsesItsExistingSpellTypeWithoutNewLevelRanks()
    {
        DbMob ant = Creature("demoniac familiar", 587, 20);
        DbMob quasit = Creature("avernal quasit", 639, 34);
        Assert.Multiple(() =>
        {
            Assert.That(DarknessFallsCharmPolicy.AllowsBotChoice(ant, Charm(0, 0), eCharacterClass.Mentalist, 16), Is.True);
            Assert.That(DarknessFallsCharmPolicy.AllowsBotChoice(ant, Charm(0, 0), eCharacterClass.Mentalist, 17), Is.True);
            Assert.That(DarknessFallsCharmPolicy.AllowsBotChoice(quasit, Charm(0, 0), eCharacterClass.Mentalist, 41), Is.True);
            Assert.That(DarknessFallsCharmPolicy.AllowsBotChoice(quasit, Charm(0, 0), eCharacterClass.Mentalist, 42), Is.True);
            Assert.That(DarknessFallsCharmPolicy.AllowsBotChoice(ant, Charm(0, 4), eCharacterClass.Mentalist, 42), Is.False);
            Assert.That(AutonomousPetSupport.TryGetGeneratedCharmProfile(eCharacterClass.Hunter, out _), Is.False,
                "Hunter GameBots retain their existing wolf summon, not a new DF synthetic charm.");
        });
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable]
public class UT_CompanionBonedancerSpellRanks
{
    private const string Key = "UnitTestCompanionBoneRanks";
    private GameServer _oldServer;
    private sealed class EmptyServer : GameServer
    {
        protected override IObjectDatabase DataBaseImpl =>
            DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
    }
    [SetUp]
    public void SetUp()
    {
        _oldServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
    }
    [TearDown]
    public void TearDown() => GameServer.LoadTestDouble(_oldServer);
    private sealed class Bot : GameBot
    {
        private Bot() : base((OfflineWorldBotRecord)null) { }
        public byte TestLevel;
        public override byte Level { get => TestLevel; set => TestLevel = value; }
        public void SetClass(bool bone) => m_characterClass = bone ? new ClassBonedancer() : new ClassCabalist();
    }

    private static T Cache<T>(string name) => (T)typeof(SkillBase)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

    [TestCase(true, false, true, 17, 17, 17)]
    [TestCase(true, false, true, 15, 15, 15)]
    [TestCase(true, false, true, 17, 1, 1)]
    [TestCase(true, false, true, 14, 14, 14)]
    [TestCase(false, true, true, 17, 17, 13)]
    [TestCase(true, true, true, 17, 17, 13)]
    [TestCase(true, false, false, 17, 17, 13)]
    public void ActualSpellCatalogUsesTrainedRankOnlyForCompanionBonedancer(
        bool helper, bool world, bool bone, int characterLevel, int trained, int expected)
    {
        var bot = (Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
        bot.TestLevel = (byte)characterLevel;
        bot.SetClass(bone);
        typeof(GameBot).GetProperty(nameof(GameBot.IsTemporaryGroupHelper)).SetValue(bot, helper);
        typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot, world);
        var specs = Cache<Dictionary<string, List<Tuple<SpellLine, int>>>>("m_specsSpellLines");
        var spells = Cache<Dictionary<string, List<Spell>>>("m_lineSpells");
        try
        {
            // Exercise both class-hinted and fallback spell-line paths.
            foreach (int hint in new[] { bot.CharacterClass.ID, 0 })
            {
                specs[Key] = [Tuple.Create(new SpellLine(Key, Key, Key, false), hint)];
                spells[Key] = [new Spell(new DbSpell { SpellID = 10001, Name = "Summon Bonemage", Type = "SummonMinion", Target = "Self" }, 15)];
                var spec = new Specialization(Key, Key, 0) { Level = trained };
                Assert.That(spec.GetSpellLinesForLiving(bot).Single().Level, Is.EqualTo(expected));
                var known = spec.GetLinesSpellsForLiving(bot).Values.SelectMany(x => x).OfType<Spell>().ToArray();
                Assert.That(known.Length, Is.EqualTo(expected >= 15 ? 1 : 0));
                specs[Key] = [Tuple.Create(new SpellLine(Key, Key, Key, true), hint)];
                Assert.That(spec.GetSpellLinesForLiving(bot).Single().Level, Is.EqualTo(characterLevel), "Baseline is unchanged");
            }
        }
        finally { specs.Remove(Key); spells.Remove(Key); }
    }
}

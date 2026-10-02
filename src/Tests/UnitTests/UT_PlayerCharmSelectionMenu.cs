using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_PlayerCharmSelectionMenu
    {
        private sealed class TestRegion : Region
        {
            private TestRegion() : base(default) { }
            public bool Capital;
            public override bool IsCapitalCity => Capital;
        }
        private sealed class Player : GamePlayer
        {
            private Player() : base(null, null) { }
            public byte TestLevel;
            public ushort RegionId;
            public override ICharacterClass CharacterClass => new ClassSorcerer();
            public override byte Level => TestLevel;
            public override bool IsAlive => true;
            public override ushort CurrentRegionID { get => RegionId; set => RegionId = value; }
        }

        [Test]
        public void AllEligibleNamesAreShownInsteadOfRandomShortlist()
        {
            DbMob[] input = Enumerable.Range(0, 24).Select(i => new DbMob { Name = "mob " + i, Level = 30 }).ToArray();
            DbMob[] pool = PlayerGeneratedCharmPolicy.SelectTemplates(input.Concat(input), 30);
            Assert.That(pool, Has.Length.EqualTo(24));
            string first = PlayerCharmSelectionMenu.BuildPage(pool, 0, 30);
            string second = PlayerCharmSelectionMenu.BuildPage(pool, 1, 30);
            foreach (int index in Enumerable.Range(0, 24))
                Assert.That(index < 12 ? first : second, Does.Contain("[" + PlayerCharmSelectionMenu.ChoiceLabel(index, pool[index]) + "]"));
            Assert.That(first, Does.Contain("[Next]").And.Not.Contain("[Previous]"));
            Assert.That(second, Does.Contain("[Previous]").And.Not.Contain("[Next]"));
            Assert.That(first, Does.Contain("level 30"));
            Assert.That(PlayerCharmSelectionMenu.LifetimeMilliseconds, Is.EqualTo(600_000));
        }

        [Test]
        public void NamesCannotInjectMenuLinks()
        {
            var choices = new[] { new DbMob { Name = "strange [Next]\nname", Level = 30 } };
            string page = PlayerCharmSelectionMenu.BuildPage(choices, 0, 30);
            Assert.That(Regex.Matches(page, @"\[([^\]]+)\]"), Has.Count.EqualTo(2));
            Assert.That(page, Does.Contain("[1: strange (Next) name (Melee)]"));
        }

        [TestCase(954, 30, 1, false, true)]
        [TestCase(955, 30, 1, false, false)]
        [TestCase(954, 29, 1, false, false)]
        [TestCase(954, 30, 2, false, false)]
        [TestCase(954, 30, 1, true, false)]
        public void SelectionIsSingleUseAndBoundToSpellLevelAndRegion(int spellId, int savedLevel,
            int savedRegion, bool expired, bool expected)
        {
            Player player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            player.TestLevel = 30;
            player.RegionId = 1;
            player.ObjectState = GameObject.eObjectState.Active;
            player.CurrentRegion = (TestRegion)RuntimeHelpers.GetUninitializedObject(typeof(TestRegion));
            typeof(GameLiving).GetField("<TempProperties>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(player, new PropertyCollection());
            DbMob mob = new() { Name = "chosen creature", Level = 30 };
            Type choiceType = typeof(PlayerCharmSelectionMenu).GetNestedType("Choice", BindingFlags.NonPublic);
            object choice = Activator.CreateInstance(choiceType, spellId, savedLevel, (ushort)savedRegion,
                GameLoop.GameLoopTime + (expired ? -1L : 30_000L), mob);
            player.TempProperties.SetProperty("OfflineDAoC.PlayerCharmChoice", choice);
            var spell = new Spell(new DbSpell { SpellID = 954, Type = "Charm", Target = "Enemy" }, 20);
            Assert.That(PlayerCharmSelectionMenu.TakeChoice(player, spell), expected ? Is.SameAs(mob) : Is.Null);
            Assert.That(PlayerCharmSelectionMenu.TakeChoice(player, spell), Is.Null);
        }

        [Test]
        public void PlayerOnlyApiCannotAcceptGameBotsAndRankExamplesStayUnchanged()
        {
            Assert.That(typeof(GamePlayer).IsAssignableFrom(typeof(GameBot)), Is.False);
            Assert.That(PlayerGeneratedCharmPolicy.TargetLevel(eCharacterClass.Sorcerer, 954, 30), Is.EqualTo(30));
            Assert.That(PlayerGeneratedCharmPolicy.TargetLevel(eCharacterClass.Sorcerer, 955, 32), Is.EqualTo(33));
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.GS;
using DOL.GS.Quests.Hibernia;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_SluaghbinderEpicQuestFixes
    {
        private static Skill Raise(int id, int level) =>
            new Spell(new DbSpell { SpellID = id, Name = "raise " + level, Type = "SummonMinion", Target = "Self" }, level);

        [Test]
        public void EpicSpellsPageShowsOnlyEarnedRaiseRanks()
        {
            var page = new List<Skill> { Raise(59080, 10), Raise(59081, 20), Raise(59082, 30), Raise(59083, 40), Raise(59084, 50) };

            // A level 50 who has finished only the level 10 quest.
            var shown = SluaghbinderEpicSpellsSpecialization.EarnedOnly(page, id => id == 59080);
            Assert.That(shown.Cast<Spell>().Select(s => s.ID), Is.EqualTo(new[] { 59080 }));

            // Quests 10 and 20 finished.
            Assert.That(SluaghbinderEpicSpellsSpecialization.EarnedOnly(page, id => id is 59080 or 59081), Has.Count.EqualTo(2));

            // All five quests finished: every rank shows.
            Assert.That(SluaghbinderEpicSpellsSpecialization.EarnedOnly(page, _ => true), Has.Count.EqualTo(5));
        }

        [Test]
        public void SpellsOutsideTheRewardRangeAreNeverHidden()
        {
            var other = new Spell(new DbSpell { SpellID = 12345, Name = "other", Type = "Heal", Target = "Self" }, 10);
            Assert.That(SluaghbinderEpicSpellsSpecialization.EarnedOnly(new List<Skill> { other }, _ => false), Has.Count.EqualTo(1));
        }

        [Test]
        public void GravewardenQuestPointsToCaillteGarran()
        {
            var definition = SluaghbinderEpicQuestRuntime.GetDefinition(typeof(SluaghbinderEpic30));
            Assert.That(definition.Clue, Does.Contain("Caillte Garran"));
            Assert.That(definition.Clue, Does.Not.Contain("Vale of Balor"));
        }

        [Test]
        public void QuestTargetsAreNotPlacedOnTopOfOtherNpcs()
        {
            // Frang stands at 32213, 32765 in Muire Tomb; the old ossuary spot was exactly on him.
            var ossuary = SluaghbinderEpicQuestRuntime.GetDefinition(typeof(SluaghbinderEpic20));
            Assert.That((ossuary.X, ossuary.Y), Is.Not.EqualTo((32213, 32765)));

            // The death-scribe stands among the three black wraiths, not on the old tree-trunk spot.
            var scribe = SluaghbinderEpicQuestRuntime.GetDefinition(typeof(SluaghbinderEpic40));
            Assert.That((scribe.X, scribe.Y), Is.Not.EqualTo((479700, 503630)));
        }
    }
}

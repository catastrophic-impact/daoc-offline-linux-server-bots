using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Bots fight with their best damage ranks, never a weak low rank.</summary>
    [TestFixture]
    public class UT_BotHighestDamageRanks
    {
        private static Spell Make(string type, int level, double cast = 2.6, int radius = 0, int damage = 50, string target = "Enemy") =>
            new(new DbSpell { SpellID = 800000 + level * 10 + radius, Name = "test", Type = type, Target = target,
                CastTime = cast, Damage = damage, Radius = radius, Range = 1500 }, level);

        [Test]
        public void AWeakLowLevelLineIsSkippedNextToTheMainLine()
        {
            Spell obsidianStrike = Make("DirectDamage", 50);
            Spell dissolveSkeleton = Make("Lifedrain", 4, cast: 0);
            Spell[] known = { obsidianStrike, dissolveSkeleton };
            Assert.Multiple(() =>
            {
                Assert.That(BotBrain.IsOutclassedDamageSpell(dissolveSkeleton, known, 50), Is.True, "level 4 lifedrain at level 50");
                Assert.That(BotBrain.IsOutclassedDamageSpell(obsidianStrike, known, 50), Is.False);
            });
        }

        [Test]
        public void LowerRanksOfTheSameSpellAreSkipped()
        {
            Spell top = Make("DirectDamage", 43);
            Spell lower = Make("DirectDamage", 33);
            Spell aoe = Make("DirectDamage", 30, radius: 350);
            Spell[] known = { top, lower, aoe };
            Assert.Multiple(() =>
            {
                Assert.That(BotBrain.IsOutclassedDamageSpell(lower, known, 50), Is.True, "lower rank of the same nuke");
                Assert.That(BotBrain.IsOutclassedDamageSpell(top, known, 50), Is.False);
                Assert.That(BotBrain.IsOutclassedDamageSpell(aoe, known, 50), Is.False, "an area nuke is a different kind");
            });
        }

        [Test]
        public void ASecondLineAtAReasonableLevelStaysAndNonDamageIsUntouched()
        {
            Spell main = Make("DirectDamage", 50);
            Spell secondLine = Make("DamageSpeedDecrease", 30, cast: 3.0);
            Spell mez = Make("Mesmerize", 2, damage: 0);
            Spell[] known = { main, secondLine, mez };
            Assert.Multiple(() =>
            {
                Assert.That(BotBrain.IsOutclassedDamageSpell(secondLine, known, 50), Is.False, "30 is at least half of 50");
                Assert.That(BotBrain.IsOutclassedDamageSpell(mez, known, 50), Is.False, "crowd control is not a damage rank");
            });
        }

        [Test]
        public void LowLevelBotsKeepTheirFirstSpells()
        {
            Spell first = Make("DirectDamage", 1);
            Spell second = Make("Lifedrain", 3, cast: 0);
            Spell[] known = { first, second };
            Assert.That(BotBrain.IsOutclassedDamageSpell(first, known, 4), Is.False, "less than 5 levels apart");
        }
    }
}

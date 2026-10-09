using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_ShortCombatShield
    {
        private static Spell Shield(int durationSeconds, string target = "Realm", int frequency = 0, int concentration = 0) =>
            new(new DbSpell
            {
                SpellID = 2876, Name = "Boon of the Fallen", Type = "DamageShield", Target = target,
                Duration = durationSeconds, CastTime = 4, Frequency = frequency, Concentration = concentration,
            }, 20);

        [Test]
        public void SpiritmasterOneMinuteShieldIsCombatOnly()
        {
            Assert.That(BotBrain.IsShortCombatShield(Shield(60)), Is.True);
        }

        [Test]
        public void LongShieldsStayRoutineUpkeep()
        {
            // Gift of the Fallen lasts five minutes and is still worth casting between fights.
            Assert.That(BotBrain.IsShortCombatShield(Shield(300)), Is.False);
        }

        [Test]
        public void PetFocusShieldsAndConcentrationShieldsAreUnchanged()
        {
            Assert.That(BotBrain.IsShortCombatShield(Shield(5, "Pet", frequency: 50)), Is.False);
            Assert.That(BotBrain.IsShortCombatShield(Shield(60, "Pet")), Is.False);
            Assert.That(BotBrain.IsShortCombatShield(Shield(60, concentration: 1)), Is.False);
        }

        [Test]
        public void OtherBuffsAreNotAffected()
        {
            var buff = new Spell(new DbSpell { SpellID = 1, Name = "str", Type = "StrengthBuff", Target = "Realm", Duration = 60 }, 20);
            Assert.That(BotBrain.IsShortCombatShield(buff), Is.False);
        }
    }
}

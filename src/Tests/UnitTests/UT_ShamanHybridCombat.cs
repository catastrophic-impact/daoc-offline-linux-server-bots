using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_ShamanHybridCombat
    {
        private static Spell S(string type, double value = 0, double castTime = 3) =>
            new(new DbSpell { Type = type, Target = "Enemy", Value = value, CastTime = castTime }, 1);

        [Test]
        public void ShamansMeleeBetweenCastsOtherCastersStayAtRange()
        {
            foreach (bool grouped in new[] { false, true })
            foreach (bool autonomous in new[] { false, true })
            {
                Assert.That(BotBrain.PrefersSpellRange(new ClassShaman(), grouped, autonomous), Is.False);
                Assert.That(BotBrain.PrefersSpellRange(new ClassSpiritmaster(), grouped, autonomous), Is.True);
                Assert.That(BotBrain.PrefersSpellRange(new ClassRunemaster(), grouped, autonomous), Is.True);
            }
        }

        [Test]
        public void ShamanRootStaysOutOfTheOrdinaryRotation()
        {
            Spell root = S("SpeedDecrease", 99, 2.5);
            Assert.Multiple(() =>
            {
                Assert.That(ShamanBotCombatPolicy.IsRoot(root), Is.True);
                Assert.That(ShamanBotCombatPolicy.IsRoot(S("SpeedDecrease", 40)), Is.False, "a snare is not a root");
                Assert.That(ShamanBotCombatPolicy.AllowsOrdinaryOffense(eCharacterClass.Shaman, root), Is.False);
                Assert.That(ShamanBotCombatPolicy.AllowsOrdinaryOffense(eCharacterClass.Shaman, S("DirectDamage")), Is.True);
                Assert.That(ShamanBotCombatPolicy.AllowsOrdinaryOffense(eCharacterClass.Shaman, S("DamageOverTime")), Is.True);
                Assert.That(ShamanBotCombatPolicy.AllowsOrdinaryOffense(eCharacterClass.Healer, root), Is.False, "healers keep roots for adds");
                Assert.That(ShamanBotCombatPolicy.AllowsOrdinaryOffense(eCharacterClass.Druid, root), Is.False);
                Assert.That(ShamanBotCombatPolicy.AllowsOrdinaryOffense(eCharacterClass.Healer, S("DirectDamage")), Is.True);
                Assert.That(ShamanBotCombatPolicy.AllowsOrdinaryOffense(eCharacterClass.Wizard, root), Is.True, "non-healers unchanged");
            });
        }

        [Test]
        public void NukesComeBeforeTheDotAndTheDisease()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ShamanBotCombatPolicy.RotationPriority(S("Bolt", 0, 2.5)), Is.EqualTo(0));
                Assert.That(ShamanBotCombatPolicy.RotationPriority(S("DirectDamage")), Is.EqualTo(0));
                Assert.That(ShamanBotCombatPolicy.RotationPriority(S("DamageOverTime")), Is.EqualTo(1));
                Assert.That(ShamanBotCombatPolicy.RotationPriority(S("Disease")), Is.EqualTo(2));
            });
        }

        // ready cast, hit by the monster, own swing timer running -> stop melee and wait to cast
        [TestCase(true, false, true, true)]
        [TestCase(true, true, true, false)]    // being hit: keep fighting in melee
        [TestCase(true, false, false, false)]  // not interrupted: the cast simply starts
        [TestCase(false, false, true, false)]  // nothing ready: keep swinging
        public void ReadyCastEndsMeleeUnlessTheShamanIsBeingHit(bool ready, bool byOther, bool self, bool hold) =>
            Assert.That(ShamanBotCombatPolicy.HoldMeleeForCast(ready, byOther, self), Is.EqualTo(hold));

        [TestCase(true, 900, true)]
        [TestCase(true, 200, false)]   // already swinging at its victim
        [TestCase(false, 900, false)]  // not a safe add (solo, the kill target, or already hurt)
        public void OnlyAnIncomingSafeAddIsRooted(bool safe, double distance, bool root) =>
            Assert.That(ShamanBotCombatPolicy.IsRootableAdd(safe, distance), Is.EqualTo(root));

        [Test]
        public void SoloShamansNeverQualifyForTheAddRoot()
        {
            Assert.That(BardBotCrowdControlPolicy.HasGroupForPveAdd(1), Is.False);
            Assert.That(BardBotCrowdControlPolicy.HasGroupForPveAdd(null), Is.False);
            Assert.That(BardBotCrowdControlPolicy.HasGroupForPveAdd(2), Is.True);
        }
    }
}

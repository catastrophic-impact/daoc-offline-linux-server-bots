using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_BardBotCrowdControlPolicy
    {
        [TestCase(null, false)]
        [TestCase(0, false)]
        [TestCase(1, false)]
        [TestCase(2, true)]
        [TestCase(8, true)]
        public void PveAddMezRequiresAnActualParty(int? memberCount, bool expected)
        {
            Assert.That(BardBotCrowdControlPolicy.HasGroupForPveAdd(memberCount), Is.EqualTo(expected));
        }

        [TestCase(eSpellType.Mez)]
        [TestCase(eSpellType.Mesmerize)]
        public void BardMezDoesNotReplaceOrdinaryCombat(eSpellType spellType)
        {
            Assert.That(BardBotCrowdControlPolicy.AllowsOrdinaryOffense(eCharacterClass.Bard, spellType), Is.False);
        }

        [TestCase(eSpellType.DirectDamage)]
        [TestCase(eSpellType.SpeedDecrease)]
        [TestCase(eSpellType.Stun)]
        public void BardOtherOffenseIsUnchanged(eSpellType spellType)
        {
            Assert.That(BardBotCrowdControlPolicy.AllowsOrdinaryOffense(eCharacterClass.Bard, spellType), Is.True);
        }

        [TestCase(eCharacterClass.Minstrel)]
        [TestCase(eCharacterClass.Sorcerer)]
        [TestCase(eCharacterClass.Healer)]
        public void OtherClassesKeepTheirOrdinaryCrowdControl(eCharacterClass characterClass)
        {
            Assert.That(BardBotCrowdControlPolicy.AllowsOrdinaryOffense(characterClass, eSpellType.Mesmerize), Is.True);
        }

        [Test]
        public void SoloBardDoesNotMesmerizePveTarget()
        {
            Assert.That(BardBotCrowdControlPolicy.IsSafePveAdd(false, true, true, false, false,
                100, false, 10_000, 0), Is.False);
        }

        [Test]
        public void GroupBardMayMesmerizeUntouchedAddAttackingParty()
        {
            Assert.That(BardBotCrowdControlPolicy.IsSafePveAdd(true, true, true, false, false,
                100, false, 10_000, 0), Is.True);
        }

        [TestCase(false, true, false, false, 100, false, 0)]
        [TestCase(true, false, false, false, 100, false, 0)]
        [TestCase(true, true, true, false, 100, false, 0)]
        [TestCase(true, true, false, true, 100, false, 0)]
        [TestCase(true, true, false, false, 74, false, 0)]
        [TestCase(true, true, false, false, 100, true, 0)]
        [TestCase(true, true, false, false, 100, false, 20_000)]
        public void UnsafeOrRetryingAddDoesNotInterruptCombat(bool otherTarget,
            bool attacksGroup, bool groupTargetsAdd, bool bardTargetsAdd,
            int healthPercent, bool controlled, long retryUntil)
        {
            Assert.That(BardBotCrowdControlPolicy.IsSafePveAdd(true, otherTarget,
                attacksGroup, groupTargetsAdd, bardTargetsAdd, healthPercent,
                controlled, 10_000, retryUntil), Is.False);
        }
    }
}

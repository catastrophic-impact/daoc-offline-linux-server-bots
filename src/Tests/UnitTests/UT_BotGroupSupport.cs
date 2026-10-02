using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    public class UT_BotGroupSupport
    {
        [TestCase(0, 0, 0)]
        [TestCase(1, 0, 0)]
        [TestCase(2, 0, 1)]
        [TestCase(6, 0, 3)]
        [TestCase(6, 5, 1)]
        [TestCase(2, 2, 0)]
        public void CombatKeepsHealingCapacity(int healers, int critical, int expected) =>
            Assert.That(BotGroupSupport.CombatResurrectionBudget(healers, critical), Is.EqualTo(expected));

        [Test]
        public void IncomingHealingDistributesTargetsWithoutPreventingNeededSecondHeal()
        {
            Assert.That(BotGroupSupport.ProjectedHealthPercent(200, 1000, 400), Is.EqualTo(60));
            Assert.That(BotGroupSupport.ProjectedHealthPercent(400, 1000, 0), Is.LessThan(60));
            Assert.That(BotGroupSupport.ProjectedHealthPercent(100, 1000, 200), Is.LessThan(40));
            Assert.That(BotGroupSupport.ProjectedHealthPercent(600, 1000, 400), Is.EqualTo(100));
        }

        [Test]
        public void OrdinaryTwoHealerCombatPartyReservesOnlyOneResurrector()
        {
            var reservations = new CompanionRaidResurrectionReservations<object>();
            int budget = BotGroupSupport.CombatResurrectionBudget(2, 0);
            object caster = new(), corpse = new();
            Assert.That(reservations.TryReserve(caster, corpse, 0, 4000, true, true, budget + 1, false), Is.True);
            Assert.That(reservations.TryReserve(new(), corpse, 0, 4000, true, true, budget + 1, false), Is.False);
            Assert.That(reservations.TryReserve(new(), new(), 0, 4000, true, true, budget + 1, false), Is.False);
            reservations.ReleaseCaster(caster);
            Assert.That(reservations.TryReserve(new(), corpse, 0, 4000, true, true, budget + 1, false), Is.True);
        }
    }
}

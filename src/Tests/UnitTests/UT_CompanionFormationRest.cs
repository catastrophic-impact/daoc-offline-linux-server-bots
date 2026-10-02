using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_CompanionFormationRest
    {
        [TestCase(true, false, true, false, true, true)]
        [TestCase(false, true, true, false, true, false)]
        [TestCase(true, true, true, false, true, false)]
        [TestCase(true, false, false, false, true, false)]
        [TestCase(true, false, true, true, true, false)]
        [TestCase(true, false, true, false, false, false)]
        public void SettlingOnlyExemptsNearbyRecoveringCompanionMovement(bool temporary,
            bool autonomous, bool atPlayer, bool leaderMoving, bool deficit, bool expected)
        {
            Assert.That(BotRestRecovery.CanSettleForRest(temporary, autonomous, atPlayer,
                leaderMoving, deficit), Is.EqualTo(expected));
        }

        [TestCase(false, 1999, false)]
        [TestCase(false, 2000, true)]
        [TestCase(true, 2000, false)]
        public void SettlingStillRequiresQuietWindowAndNoCombat(bool combat, long elapsed, bool expected)
        {
            Assert.That(BotRestRecovery.ShouldTemporaryCompanionRest(true, true, false,
                combat, 10000 + elapsed, 10000, 100, 100, 40, 100, 100, 100), Is.EqualTo(expected));
        }
    }
}

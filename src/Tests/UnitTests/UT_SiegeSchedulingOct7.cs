using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Concurrent chance-based sieges: likely soon when none runs, rarer as more run.</summary>
    [TestFixture]
    public class UT_SiegeSchedulingOct7
    {
        [TestCase(0, 0d, 0.25, TestName = "No siege running: a fair chance every minute")]
        [TestCase(0, 3d, 1d, TestName = "No siege for three minutes: one starts")]
        [TestCase(1, 0d, 0.04, TestName = "One running: an occasional second")]
        [TestCase(2, 0d, 0.012, TestName = "Two running: a rare third")]
        [TestCase(3, 30d, 0d, TestName = "All three realms attacking: none more")]
        public void StartChanceFallsAsMoreSiegesRun(int active, double idleMinutes, double expected) =>
            Assert.That(AutonomousRvrEventLayer.SiegeStartChancePerMinute(active, idleMinutes), Is.EqualTo(expected).Within(1e-9));

        [Test]
        public void RelicSiegesAreTheRarerEvent() =>
            Assert.That(AutonomousRvrEventLayer.RelicSiegeShare, Is.LessThan(0.2).And.GreaterThan(0));
    }
}

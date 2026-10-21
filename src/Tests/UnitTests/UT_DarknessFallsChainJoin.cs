using System.Linq;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// 2026-10-16: 108 Hibernian Prince Abdin raiders who could not join the Darkness Falls chain tested a strict leg to
    /// every waypoint (~300 path queries, 1-3 s) on every AI turn.
    /// </summary>
    [TestFixture]
    public class UT_DarknessFallsChainJoin
    {
        [Test]
        public void AJoinTestsOnlyTheNearestWaypoints()
        {
            Vector3[] chain = Enumerable.Range(0, 256).Select(i => new Vector3(i * 100, 0, 0)).ToArray();
            int legs = 0;
            bool joined = AutonomousDarknessFallsRoutePlan.TryRejoin(new Vector3(-500, 0, 0), chain,
                (_, _) => { legs++; return false; }, out _, 32);
            Assert.That(joined, Is.False);
            Assert.That(legs, Is.EqualTo(32));

            legs = 0;
            joined = AutonomousDarknessFallsRoutePlan.TryRejoin(new Vector3(-500, 0, 0), chain,
                (_, to) => { legs++; return to.X == 300; }, out int next, 32);
            Assert.That(joined, Is.True);
            Assert.That(next, Is.EqualTo(3));
            Assert.That(legs, Is.EqualTo(4), "nearest first");
        }

        [Test]
        public void AFailedJoinWaitsUntilTimeDistanceOrPostChange()
        {
            var at = new Vector3(1000, 1000, 0);
            const long retry = 50_000;
            Assert.That(AutonomousDarknessFallsRoutePlan.ShouldRetryJoin("raid:a", "raid:a", at, at + new Vector3(100, 0, 0), 40_000, retry, 256), Is.False);
            Assert.That(AutonomousDarknessFallsRoutePlan.ShouldRetryJoin("raid:a", "raid:a", at, at, retry, retry, 256), Is.True);
            Assert.That(AutonomousDarknessFallsRoutePlan.ShouldRetryJoin("raid:a", "raid:a", at, at + new Vector3(300, 0, 0), 40_000, retry, 256), Is.True);
            Assert.That(AutonomousDarknessFallsRoutePlan.ShouldRetryJoin("raid:a", "raid:b", at, at, 40_000, retry, 256), Is.True);
            Assert.That(AutonomousDarknessFallsRoutePlan.ShouldRetryJoin(null, "raid:a", at, at, 40_000, retry, 256), Is.True);
        }
    }
}

using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_DragonFlightLanding
    {
        [TestCase(eRealm.Albion)]
        [TestCase(eRealm.Midgard)]
        [TestCase(eRealm.Hibernia)]
        public void CannotBecomeGroundedWhileAboveOrAwayFromHome(eRealm realm)
        {
            var home = DragonLairPlacement.Home(realm);
            Assert.That(DragonFlightLanding.HasArrived(new Point3D(home.X, home.Y, home.Z + 3000), home), Is.False);
            Assert.That(DragonFlightLanding.HasArrived(new Point3D(home.X + 5000, home.Y, home.Z), home), Is.False);
            Assert.That(DragonFlightLanding.HasArrived(new Point3D(home.X, home.Y, home.Z + 33), home), Is.False);
            Assert.That(DragonFlightLanding.HasArrived(new Point3D(home.X + 20, home.Y, home.Z + 20), home), Is.True);
            Assert.That(DragonFlightLanding.HasArrived(home, home), Is.True);
        }

        [TestCase(eRealm.Albion)]
        [TestCase(eRealm.Midgard)]
        [TestCase(eRealm.Hibernia)]
        public void ReturnDescentDoesNotEnableCombatUntilTheLastStep(eRealm realm)
        {
            var home = DragonLairPlacement.Home(realm);
            // The route is finished, but the last waypoint is still airborne.
            // Model the straight 3D return, independent of ground pathfinding.
            for (int remaining = 10; remaining > 0; remaining--)
            {
                var position = new Point3D(home.X + 500 * remaining, home.Y + 200 * remaining, home.Z + 300 * remaining);
                Assert.That(DragonFlightLanding.HasArrived(position, home), Is.False);
            }
            Assert.That(DragonFlightLanding.HasArrived(home, home), Is.True);
        }
    }
}

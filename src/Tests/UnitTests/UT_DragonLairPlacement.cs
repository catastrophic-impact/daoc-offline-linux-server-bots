using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_DragonLairPlacement
    {
        [TestCase(eRealm.Albion, 390)]
        [TestCase(eRealm.Midgard, 3014)]
        [TestCase(eRealm.Hibernia, 2964)]
        public void GroundHomeIsReachableByNormalStationaryMelee(eRealm realm, int ground)
        {
            var home = DragonLairPlacement.Home(realm);
            Assert.That(new Point3D(home.X + 80, home.Y, ground).IsWithinRadius(home, 128), Is.True);
            Assert.That(new Point3D(home.X + 900, home.Y, ground).IsWithinRadius(home, 800), Is.False);
            home.Z += 3000;
            Assert.That(DragonLairPlacement.Home(realm).Z, Is.LessThan(home.Z));
            Assert.That(DragonLairPlacement.GroundFlags(GameNPC.eFlags.FLYING | GameNPC.eFlags.PEACE), Is.EqualTo(GameNPC.eFlags.PEACE));
        }

        [Test]
        public void LivingDamageSourcesIncludeNpcBasedCompanionsAndPets()
        {
            Assert.That(DragonLairPlacement.CanReceiveDamageType(typeof(GamePlayer)), Is.True);
            Assert.That(DragonLairPlacement.CanReceiveDamageType(typeof(GameBot)), Is.True);
            Assert.That(DragonLairPlacement.CanReceiveDamageType(typeof(GameSummonedPet)), Is.True);
            Assert.That(DragonLairPlacement.CanReceiveDamageType(typeof(GameNPC)), Is.True);
            Assert.That(DragonLairPlacement.CanReceiveDamageType(typeof(GameObject)), Is.False);
        }
    }
}

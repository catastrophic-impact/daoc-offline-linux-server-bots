using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Changes ported from the stefanrows/OfflineDAoC fork (group motion, Savage shield
    /// loop).
    /// </summary>
    [TestFixture]
    public class UT_StefanrowsPortOct5
    {
        [Test]
        public void FollowersNeverGetASpeedBelowTheirOwn()
        {
            // The fork's leader-pace matching slowed followers to a walk; it must not come back.
            Assert.That(typeof(AutonomousGroupMotion).GetMethod("FollowSpeed"), Is.Null);
        }

        [Test]
        public void FollowersReSteerOnlyWhenTheirSpotMovesMoreThan48Units()
        {
            Assert.That(AutonomousGroupMotion.ResteerDistance, Is.EqualTo(48f));
            Assert.That(AutonomousGroupMotion.LookaheadSeconds, Is.EqualTo(0.8));
        }

        [Test]
        public void AShieldNeverReplacesAWeaponWornInTheLeftHand()
        {
            var shield = new DbInventoryItem { Object_Type = (int)eObjectType.Shield };
            var handToHand = new DbInventoryItem { Object_Type = (int)eObjectType.HandToHand };
            var oldShield = new DbInventoryItem { Object_Type = (int)eObjectType.Shield };
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousBotEconomy.DisplacesOffhandWeapon(shield, handToHand), Is.True);
                Assert.That(AutonomousBotEconomy.DisplacesOffhandWeapon(shield, oldShield), Is.False);
                Assert.That(AutonomousBotEconomy.DisplacesOffhandWeapon(shield, null), Is.False);
                Assert.That(AutonomousBotEconomy.DisplacesOffhandWeapon(handToHand, oldShield), Is.False);
            });
        }
    }
}

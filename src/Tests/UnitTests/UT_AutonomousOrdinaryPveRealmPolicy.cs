using NUnit.Framework;

namespace DOL.GS.Tests
{
    [TestFixture]
    public sealed class UT_AutonomousOrdinaryPveRealmPolicy
    {
        [TestCase(1, 11, eRealm.Albion)]
        [TestCase(1, 12, eRealm.Albion)]
        [TestCase(1, 14, eRealm.Albion)]
        [TestCase(1, 15, eRealm.Albion)]
        [TestCase(100, 111, eRealm.Midgard)]
        [TestCase(100, 112, eRealm.Midgard)]
        [TestCase(100, 113, eRealm.Midgard)]
        [TestCase(100, 115, eRealm.Midgard)]
        [TestCase(200, 210, eRealm.Hibernia)]
        [TestCase(200, 211, eRealm.Hibernia)]
        [TestCase(200, 212, eRealm.Hibernia)]
        [TestCase(200, 214, eRealm.Hibernia)]
        public void FrontierCampBelongsOnlyToItsHomeRealm(int regionId, int zoneId, eRealm owner)
        {
            Assert.That(AutonomousOrdinaryPveRealmPolicy.CanAssignCamp(owner, (ushort)regionId, (ushort)zoneId), Is.True);
            foreach (eRealm other in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
            {
                if (other != owner)
                    Assert.That(AutonomousOrdinaryPveRealmPolicy.CanAssignCamp(other, (ushort)regionId, (ushort)zoneId),
                        Is.False, $"{other} must not receive ordinary PvE in {owner} frontier {regionId}/{zoneId}");
            }
        }

        [TestCase(1, 10)]
        [TestCase(100, 110)]
        [TestCase(200, 209)]
        [TestCase(249, 0)]
        [TestCase(221, 0)]
        public void NonFrontierZoneStaysWithExistingAccessAndDungeonPolicies(int regionId, int zoneId)
        {
            foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
                Assert.That(AutonomousOrdinaryPveRealmPolicy.CanAssignCamp(realm, (ushort)regionId, (ushort)zoneId), Is.True);
        }
    }
}

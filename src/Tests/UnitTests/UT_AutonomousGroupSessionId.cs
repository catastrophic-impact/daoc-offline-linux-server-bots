using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_AutonomousGroupSessionId
    {
        [TestCase(eRealm.Albion, "albion-")]
        [TestCase(eRealm._FirstPlayerRealm, "albion-")]
        [TestCase(eRealm.Midgard, "midgard-")]
        [TestCase(eRealm.Hibernia, "hibernia-")]
        public void GroupIdsNameTheRealm(eRealm realm, string prefix)
        {
            string id = AutonomousBotGroupCoordinator.SessionId(realm, 22);
            Assert.That(id, Does.StartWith(prefix));
            Assert.That(id, Does.EndWith("-022"));
            Assert.That(id, Does.Not.Contain("firstplayerrealm"));
        }
    }
}

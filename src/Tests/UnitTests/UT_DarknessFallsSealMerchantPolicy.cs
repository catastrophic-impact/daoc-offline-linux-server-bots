using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    public class UT_DarknessFallsSealMerchantPolicy
    {
        [TestCase(eRealm.Albion, eRealm.Albion, true)]
        [TestCase(eRealm.Midgard, eRealm.Midgard, true)]
        [TestCase(eRealm.Hibernia, eRealm.Hibernia, true)]
        [TestCase(eRealm.Albion, eRealm.Midgard, false)]
        [TestCase(eRealm.Midgard, eRealm.Hibernia, false)]
        [TestCase(eRealm.Hibernia, eRealm.Albion, false)]
        [TestCase(eRealm.None, eRealm.Albion, false)]
        public void SealMerchantRequiresItsOwnRealm(eRealm merchantRealm, eRealm playerRealm, bool allowed)
        {
            Assert.That(GameDarknessFallsSealsMerchant.CanTrade(merchantRealm, playerRealm), Is.EqualTo(allowed));
        }
    }
}

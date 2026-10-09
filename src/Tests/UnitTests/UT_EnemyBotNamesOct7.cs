using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Goal 12: enemy-realm gamebots are shown by race/rank; friendly ones keep their real names.</summary>
    [TestFixture]
    public class UT_EnemyBotNamesOct7
    {
        [TestCase(true, eRealm.Midgard, eRealm.Albion, 1, false, ExpectedResult = true, TestName = "Enemy gamebot is masked")]
        [TestCase(true, eRealm.Albion, eRealm.Albion, 1, true, ExpectedResult = false, TestName = "Friendly gamebot keeps its name")]
        [TestCase(true, eRealm.Hibernia, eRealm.Albion, 1, true, ExpectedResult = false, TestName = "Rules say same realm: name shown")]
        [TestCase(false, eRealm.Midgard, eRealm.Albion, 1, false, ExpectedResult = false, TestName = "Companion bot keeps its name")]
        [TestCase(true, eRealm.Midgard, eRealm.Albion, 3, false, ExpectedResult = false, TestName = "Game master sees real names")]
        [TestCase(true, eRealm.None, eRealm.Albion, 1, false, ExpectedResult = false, TestName = "Realmless bot is not masked")]
        public bool MaskOnlyEnemyGamebots(bool autonomous, eRealm bot, eRealm viewer, int priv, bool sameRealm) =>
            AutonomousNameMask.ShouldMask(autonomous, bot, viewer, priv, sameRealm);
    }
}

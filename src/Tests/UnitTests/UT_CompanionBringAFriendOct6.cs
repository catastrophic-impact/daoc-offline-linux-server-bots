using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Companion bots count toward Bring A Friend like players; gamebots never do.</summary>
    [TestFixture]
    public class UT_CompanionBringAFriendOct6
    {
        [TestCase(true, true, false, true, ExpectedResult = true, TestName = "A player's /spawn or /raid helper is a companion")]
        [TestCase(true, false, true, false, ExpectedResult = false, TestName = "A gamebot is never a companion")]
        [TestCase(true, true, true, true, ExpectedResult = false, TestName = "An autonomous bot is never a companion even if marked as a helper")]
        [TestCase(true, true, false, false, ExpectedResult = false, TestName = "A helper without a player owner is not a companion")]
        [TestCase(false, true, false, true, ExpectedResult = false, TestName = "Only bots can be companions")]
        public bool OnlyPlayerOwnedHelperBotsAreCompanions(bool isBot, bool helper, bool autonomous, bool owner) =>
            CompanionBringAFriend.IsCompanion(isBot, helper, autonomous, owner);

        // Today's database: baf_initial_chance 0, baf_additional_chance 50.
        [TestCase(1, ExpectedResult = 0, TestName = "Solo: no adds")]
        [TestCase(4, ExpectedResult = 150, TestName = "Player and three companions: one add, half a chance of a second")]
        [TestCase(8, ExpectedResult = 350, TestName = "Full group of eight: three adds, half a chance of a fourth")]
        [TestCase(40, ExpectedResult = 1950, TestName = "Raid 40 scales up with its size")]
        [TestCase(80, ExpectedResult = 3950, TestName = "Raid 80 scales up with its size")]
        [TestCase(0, ExpectedResult = 0, TestName = "Nobody counted is treated as one attacker")]
        public int TheChanceScalesWithTheCountedMembers(int counted) =>
            CompanionBringAFriend.ChancePercent(counted, 0, 50);
    }
}

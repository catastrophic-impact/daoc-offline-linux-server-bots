using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>A raid party that lost members takes replacements up to its size when it joined.</summary>
    [TestFixture]
    public class UT_RaidRefillOct6
    {
        [TestCase(8, 7, ExpectedResult = true, TestName = "A party of eight that lost one is refilled")]
        [TestCase(8, 6, ExpectedResult = true, TestName = "A party of eight that lost two is refilled")]
        [TestCase(4, 3, ExpectedResult = true, TestName = "The final four is refilled too")]
        [TestCase(8, 8, ExpectedResult = false, TestName = "A full party takes nobody")]
        [TestCase(4, 4, ExpectedResult = false, TestName = "A full final four takes nobody")]
        [TestCase(8, 0, ExpectedResult = false, TestName = "A party with nobody left is not rebuilt")]
        [TestCase(0, 5, ExpectedResult = false, TestName = "A party with no recorded size is left alone")]
        public bool OnlyShortPartiesAreRefilled(int target, int members) =>
            RealmRaidRecruitmentPolicy.NeedsReplacement(target, members);
    }
}

using DOL.AI.Brain;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Casters with no power for any attack spell fight with their staff.</summary>
    [TestFixture]
    public class UT_OutOfPowerCasterMelee
    {
        [Test]
        public void OutOfPowerMeansNoAttackSpellIsAffordable()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BrainOutOfPower(new[] { 20, 35 }, 10), Is.True, "nothing affordable");
                Assert.That(BrainOutOfPower(new[] { 20, 35 }, 20), Is.False, "the cheap nuke is affordable");
                Assert.That(BrainOutOfPower(new[] { 0, 35 }, 0), Is.False, "a free spell can still be cast");
                Assert.That(BrainOutOfPower(new int[0], 0), Is.False, "no attack spells known");
            });
        }

        private static bool BrainOutOfPower(int[] costs, int mana) => BotBrain.OutOfPower(costs, mana);
    }
}

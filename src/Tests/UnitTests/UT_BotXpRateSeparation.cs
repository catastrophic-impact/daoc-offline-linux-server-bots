using System.Reflection;
using DOL.GS;
using DOL.GS.ServerProperties;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public sealed class UT_BotXpRateSeparation
    {
        private double _playerRate;
        private double _botRate;
        private double _rvrRate;

        [SetUp]
        public void SaveRates()
        {
            _playerRate = Properties.XP_RATE;
            _botRate = Properties.BOT_XP_RATE;
            _rvrRate = Properties.RvR_XP_RATE;
        }

        [TearDown]
        public void RestoreRates()
        {
            Properties.XP_RATE = _playerRate;
            Properties.BOT_XP_RATE = _botRate;
            Properties.RvR_XP_RATE = _rvrRate;
        }

        [Test]
        public void AutonomousRateDoesNotReadOrModifyPlayerRate()
        {
            Properties.XP_RATE = 10;
            Properties.BOT_XP_RATE = 3;
            Properties.RvR_XP_RATE = 2;

            MethodInfo scale = typeof(GameBot).GetMethod("ScaleAutonomousExperience",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(scale.Invoke(null, new object[] { 100L, false }), Is.EqualTo(300L));
            Assert.That(scale.Invoke(null, new object[] { 100L, true }), Is.EqualTo(600L));
            Assert.That(Properties.XP_RATE, Is.EqualTo(10));
        }

        [TestCase(1, 100L)]
        [TestCase(2, 200L)]
        [TestCase(3, 300L)]
        [TestCase(5, 500L)]
        [TestCase(10, 1000L)]
        public void LauncherPresetsScaleAutonomousExperience(double rate, long expected)
        {
            Properties.BOT_XP_RATE = rate;
            Properties.RvR_XP_RATE = 1;
            MethodInfo scale = typeof(GameBot).GetMethod("ScaleAutonomousExperience",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(scale.Invoke(null, new object[] { 100L, false }), Is.EqualTo(expected));
        }
    }
}

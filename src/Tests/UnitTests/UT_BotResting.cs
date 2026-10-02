using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_BotResting
    {
        [TestCase(11999, true)]
        [TestCase(12000, false)]
        [TestCase(12001, false)]
        public void BotCombatQuietWindowIsTwoSeconds(int now, bool blocked)
        {
            Assert.That(BotRestRecovery.RecentlyFought(now, 10000), Is.EqualTo(blocked));
        }

        [TestCase(0, 100, 10)] [TestCase(3, 100, 10)]
        [TestCase(20, 100, 20)] [TestCase(1, 19, 2)]
        public void BotFastRestIsTenPercentFloorWithoutNerfingStrongerNativeRegen(int native, int maximum, int expected)
        {
            Assert.That(BotRestRecovery.RecoveryAmount(native, maximum), Is.EqualTo(expected));
        }

        [TestCase(false, false, 6000)]
        [TestCase(true, false, 3000)]
        [TestCase(false, true, 14000)]
        [TestCase(true, true, 10000)]
        public void SharedPlayerAndBotHealthPowerTiming(bool sitting, bool combat, int interval)
        {
            Assert.That(ClassicRestRegeneration.HealthAndPowerInterval(sitting, combat), Is.EqualTo(interval));
        }

        [TestCase(false, false, false, 1)]
        [TestCase(true, false, false, 4)]
        [TestCase(false, true, false, 0)]
        [TestCase(true, true, false, 0)]
        [TestCase(false, false, true, 0)]
        [TestCase(true, false, true, 0)]
        [TestCase(false, true, true, 0)]
        [TestCase(true, true, true, 0)]
        public void SharedPlayerAndBotEnduranceBase(bool sitting, bool combat, bool moving, int amount)
        {
            Assert.That(ClassicRestRegeneration.BaseEndurancePerTick(sitting, combat, moving), Is.EqualTo(amount));
        }

        [Test]
        public void SeatedRecoveryContinuesThroughStartThresholdUntilFull()
        {
            bool sitting = false;
            for (byte percent = 20; percent < 100; percent++)
            {
                sitting = AutonomousRestPolicy.ShouldRest(true, false, false, sitting, percent, percent, percent, true);
                Assert.That(sitting, Is.True, $"Recovery interrupted at {percent}%");
            }
            Assert.That(AutonomousRestPolicy.ShouldRest(true, false, false, sitting, 100, 100, 100, true), Is.False);
        }

        [TestCase(false, false, false)] // Leader left / task or rest point changed.
        [TestCase(true, true, false)]   // Leader or bot moving.
        [TestCase(true, false, true)]   // Attacked while recovering.
        public void RestYieldsToTravelAndCombat(bool atRestPoint, bool moving, bool combat)
        {
            Assert.That(AutonomousRestPolicy.ShouldRest(atRestPoint, moving, combat, true, 40, 40, 40, true), Is.False);
        }

        [TestCase(69, 100, 100, true, true)]
        [TestCase(100, 44, 100, true, true)]
        [TestCase(100, 100, 34, false, true)]
        [TestCase(99, 99, 99, true, false)]
        [TestCase(100, 0, 100, false, false)]
        public void RestStartsOnlyForGenuineDeficits(byte hp, byte power, byte endurance, bool usesPower, bool expected)
        {
            Assert.That(AutonomousRestPolicy.ShouldRest(true, false, false, false, hp, power, endurance, usesPower), Is.EqualTo(expected));
        }

        [Test]
        public void NonCasterDoesNotWaitForAPowerPoolItCannotHave()
        {
            Assert.That(AutonomousRestPolicy.ShouldRest(true, false, false, true, 100, 0, 100, false), Is.False);
        }

        [TestCase(99, 100, 100)]
        [TestCase(100, 99, 100)]
        [TestCase(100, 100, 99)]
        public void EveryUsedResourceMustFinishRecovery(byte hp, byte power, byte endurance)
        {
            Assert.That(AutonomousRestPolicy.ShouldRest(true, false, false, true, hp, power, endurance, true), Is.True);
        }

        [TestCase(99, 100, 100)]
        [TestCase(100, 99, 100)]
        [TestCase(100, 100, 99)]
        public void CompletedPullHasAResourceDeficitUntilEveryPoolIsFull(byte hp, byte power, byte endurance)
        {
            Assert.That(AutonomousRestPolicy.IsFullyRecovered(hp, power, endurance, true), Is.False);
        }

        [TestCase(true, false, true, false)]
        [TestCase(true, false, false, true)]
        [TestCase(false, true, true, true)]
        [TestCase(false, false, false, false)]
        public void OnlyMeaningfulTravelBlocksRest(
            bool botMoving, bool leaderMoving, bool ambientWanderMovement, bool expected)
        {
            Assert.That(AutonomousRestPolicy.MovementBlocksRest(
                botMoving, leaderMoving, ambientWanderMovement), Is.EqualTo(expected));
        }

        [Test]
        public void AmbientRoleplayMovementCannotInterruptRecovery()
        {
            bool moving = AutonomousRestPolicy.MovementBlocksRest(true, false, true);

            Assert.That(AutonomousRestPolicy.ShouldRest(
                true, moving, false, false, 40, 40, 40, true), Is.True);
        }

        [Test]
        public void TemporaryCompanionStartsRestForAnyMissingResourceAfterTwoQuietSeconds()
        {
            Assert.That(BotRestRecovery.ShouldTemporaryCompanionRest(
                true, true, false, false, 11_999, 10_000,
                999, 1000, 1000, 1000, 1000, 1000), Is.False);
            Assert.That(BotRestRecovery.ShouldTemporaryCompanionRest(
                true, true, false, false, 12_000, 10_000,
                999, 1000, 1000, 1000, 1000, 1000), Is.True);
            Assert.That(BotRestRecovery.ShouldTemporaryCompanionRest(
                true, true, false, false, 12_000, 10_000,
                1000, 1000, 999, 1000, 1000, 1000), Is.True);
            Assert.That(BotRestRecovery.ShouldTemporaryCompanionRest(
                true, true, false, false, 12_000, 10_000,
                1000, 1000, 1000, 1000, 999, 1000), Is.True);
        }

        [Test]
        public void CompanionCastActivityGetsItsOwnQuietWindowBeforeRest()
        {
            long leaderIdleSince = 10_000;
            long companionCastEnded = 11_500;
            long latest = BotRestRecovery.LatestRestActivityTick(leaderIdleSince, companionCastEnded);

            Assert.That(BotRestRecovery.ShouldTemporaryCompanionRest(
                true, true, false, false, 12_000, latest,
                900, 1000, 900, 1000, 900, 1000), Is.False,
                "A gap between casts is not a rest-mode trigger.");
            Assert.That(BotRestRecovery.ShouldTemporaryCompanionRest(
                true, true, false, false, 13_500, latest,
                900, 1000, 900, 1000, 900, 1000), Is.True,
                "Rest becomes eligible only after the companion itself is quiet for two seconds.");
        }

        [TestCase(false, true, false, false)]
        [TestCase(true, false, false, false)]
        [TestCase(true, true, true, false)]
        [TestCase(true, true, false, true)]
        public void TemporaryCompanionRestKeepsScopeMovementAndCombatGuards(
            bool temporary, bool atPlayer, bool moving, bool combat)
        {
            Assert.That(BotRestRecovery.ShouldTemporaryCompanionRest(
                temporary, atPlayer, moving, combat, 12_000, 10_000,
                500, 1000, 500, 1000, 500, 1000), Is.False);
        }

        [Test]
        public void TemporaryCompanionDoesNotRestWhenAlreadyFull()
        {
            Assert.That(BotRestRecovery.ShouldTemporaryCompanionRest(
                true, true, false, false, 12_000, 10_000,
                1000, 1000, 1000, 1000, 1000, 1000), Is.False);
        }
    }
}

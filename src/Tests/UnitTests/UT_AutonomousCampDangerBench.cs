using System;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_AutonomousCampDangerBench
    {
        private static readonly DateTime Start = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

        [SetUp]
        public void Reset() => AutonomousCampDangerBench.ResetForTests();

        private static void Kills(string camp, int count, DateTime at)
        {
            for (int i = 0; i < count; i++)
                AutonomousCampDangerBench.RecordKill(camp, at);
        }

        [Test]
        public void ThreeDifferentBotsDyingAtAPoorCampBenchIt()
        {
            Kills("skeletal pawn", 2, Start);
            Assert.That(AutonomousCampDangerBench.RecordDeath("skeletal pawn", 1, Start), Is.Null);
            Assert.That(AutonomousCampDangerBench.RecordDeath("skeletal pawn", 2, Start.AddMinutes(20)), Is.Null);
            Assert.That(AutonomousCampDangerBench.RecordDeath("skeletal pawn", 3, Start.AddMinutes(40)),
                Is.EqualTo(TimeSpan.FromHours(3)));
            Assert.That(AutonomousCampDangerBench.IsBenched("skeletal pawn", Start.AddHours(2)), Is.True);
            Assert.That(AutonomousCampDangerBench.IsBenched("skeletal pawn", Start.AddHours(4)), Is.False);
        }

        [Test]
        public void AProductiveCampIsNeverBenchedForOrdinaryDeaths()
        {
            Kills("young lynx", 150, Start);
            for (int bot = 1; bot <= 4; bot++)
                Assert.That(AutonomousCampDangerBench.RecordDeath("young lynx", bot, Start.AddMinutes(bot * 10)), Is.Null);
            Assert.That(AutonomousCampDangerBench.IsBenched("young lynx", Start.AddHours(1)), Is.False);
        }

        [Test]
        public void OneUnluckyBotDyingRepeatedlyDoesNotBenchACamp()
        {
            for (int i = 0; i < 5; i++)
                Assert.That(AutonomousCampDangerBench.RecordDeath("rat boy", 7, Start.AddMinutes(i * 10)), Is.Null);
        }

        [Test]
        public void DeathsOutsideTheWindowAreForgotten()
        {
            AutonomousCampDangerBench.RecordDeath("mudman", 1, Start);
            AutonomousCampDangerBench.RecordDeath("mudman", 2, Start.AddMinutes(30));
            Assert.That(AutonomousCampDangerBench.RecordDeath("mudman", 3, Start.AddHours(4)), Is.Null);
        }

        [Test]
        public void RepeatBenchesGetLongerUpToADay()
        {
            DateTime at = Start;
            TimeSpan?[] benches = new TimeSpan?[5];
            for (int round = 0; round < benches.Length; round++)
            {
                AutonomousCampDangerBench.RecordDeath("botonid seedling", 1, at);
                AutonomousCampDangerBench.RecordDeath("botonid seedling", 2, at);
                benches[round] = AutonomousCampDangerBench.RecordDeath("botonid seedling", 3, at);
                at += benches[round] ?? TimeSpan.Zero;
                at = at.AddMinutes(1);
            }
            Assert.That(benches, Is.EqualTo(new TimeSpan?[]
            {
                TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
                TimeSpan.FromHours(24), TimeSpan.FromHours(24),
            }));
        }

        [Test]
        public void OutdoorSpawnWeightIsCapped()
        {
            AutonomousBotDecisionEngine.Camp Camp(int live) => new("c", "Zone", "wolf", eRealm.Albion, 1,
                ConColor.BLUE, ConColor.BLUE, true, false, false, live, 0, 0);
            Assert.That(AutonomousBotDecisionEngine.OutdoorSpawnWeight(Camp(0)), Is.EqualTo(1));
            Assert.That(AutonomousBotDecisionEngine.OutdoorSpawnWeight(Camp(2)), Is.EqualTo(2));
            Assert.That(AutonomousBotDecisionEngine.OutdoorSpawnWeight(Camp(40)), Is.EqualTo(6));
        }
    }
}

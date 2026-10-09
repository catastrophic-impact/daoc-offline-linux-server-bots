using System.Linq;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>Fixes from the Oct 3-5 audit: sieges, rallies, raids, group pace, dungeons, classes.</summary>
    [TestFixture]
    public class UT_AuditFixesOct5
    {
        [Test]
        public void SiegeSlotsPreferTheKitTheBotAlreadyCarries()
        {
            Assert.That(AutonomousSiegeJobs.SlotKind(0, true), Is.EqualTo(BotSiegeKind.Ram));
            Assert.That(AutonomousSiegeJobs.SlotKind(2, true), Is.EqualTo(BotSiegeKind.Trebuchet));
            Assert.That(AutonomousSiegeJobs.SlotKind(3, true), Is.EqualTo(BotSiegeKind.Catapult));
            Assert.That(AutonomousSiegeJobs.SlotKind(4, true), Is.EqualTo(BotSiegeKind.Ballista));
            Assert.That(AutonomousSiegeJobs.SlotOrder(6, true, BotSiegeKind.Catapult).First(), Is.EqualTo(3));
            // Defenders never get a trebuchet (no spot inside the keep clears its 2,000 minimum range).
            Assert.That(Enumerable.Range(0, 6).Select(i => AutonomousSiegeJobs.SlotKind(i, false)), Has.None.EqualTo(BotSiegeKind.Trebuchet));
            Assert.That(AutonomousSiegeJobs.SlotOrder(6, true, null), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5 }));
        }

        [Test]
        public void OnlyAMeleeAttackerAtTheOperatorsHeightInterruptsASiegeJob()
        {
            Assert.That(AutonomousWorldBotController.IsSiegeOperatorThreat(150, 20), Is.True, "beside the ram");
            Assert.That(AutonomousWorldBotController.IsSiegeOperatorThreat(200, 400), Is.False, "archer on the wall above");
            Assert.That(AutonomousWorldBotController.IsSiegeOperatorThreat(420, 0), Is.False, "caster at range");
        }

        [Test]
        public void AWarbandLeavesWithoutAMemberWhoIsNeverReadyAfterAMinute()
        {
            Assert.That(AutonomousFrontierTransport.PartialDepartureDue(0, 100_000), Is.False);
            Assert.That(AutonomousFrontierTransport.PartialDepartureDue(50_000, 100_000), Is.False);
            Assert.That(AutonomousFrontierTransport.PartialDepartureDue(40_000, 100_000), Is.True);
        }

        [Test]
        public void PooledForcedPartiesKeepTheFinalFourRule()
        {
            Assert.That(AutonomousBotGroupCoordinator.ForcedPartyCanUseFinalFour(4, 4), Is.True);
            Assert.That(AutonomousBotGroupCoordinator.ForcedPartyCanUseFinalFour(4, 12), Is.False, "never strand the rest");
            Assert.That(AutonomousBotGroupCoordinator.ForcedPartyCanUseFinalFour(3, 4), Is.False);
        }

        [Test]
        public void ARaidWithNoReachableEncounterEndsAfterTwentyMinutes()
        {
            Assert.That(RealmRaidDungeonRoute.BlockedTooLong(0, 10_000_000), Is.False);
            Assert.That(RealmRaidDungeonRoute.BlockedTooLong(1_000_000, 1_000_000 + 19 * 60_000), Is.False);
            Assert.That(RealmRaidDungeonRoute.BlockedTooLong(1_000_000, 1_000_000 + 20 * 60_000), Is.True);
        }

        [Test]
        public void RaidRouteProbesFromAPartyOnTheFloorAboveTheFront()
        {
            // Tuscaran Oct 3-4: front on a pad 96 below the floor where 280 bots stood.
            var front = new Vector3(34519, 27689, 17012);
            Vector3[] anchors = RealmRaidDungeonRoute.DistinctAnchors(front, new[]
            {
                new Vector3(34411, 27651, 17112),
                new Vector3(34420, 27660, 17110),
                new Vector3(40000, 30000, 17000),
            }, 6);
            Assert.That(anchors, Is.EqualTo(new[] { front, new Vector3(34411, 27651, 17112), new Vector3(40000, 30000, 17000) }));
            Assert.That(RealmRaidDungeonRoute.DistinctAnchors(front,
                Enumerable.Range(1, 20).Select(i => new Vector3(i * 1000, 0, 0)), 6), Has.Length.EqualTo(7));
        }

        [TestCase(300f, false, AutonomousGroupPace.Decision.FullSpeed)]
        [TestCase(900f, false, AutonomousGroupPace.Decision.FullSpeed)]
        [TestCase(1500f, false, AutonomousGroupPace.Decision.FullSpeed)]
        [TestCase(1600f, false, AutonomousGroupPace.Decision.Wait)]
        [TestCase(float.MaxValue, false, AutonomousGroupPace.Decision.Wait)]
        [TestCase(1000f, true, AutonomousGroupPace.Decision.Wait)]
        [TestCase(900f, true, AutonomousGroupPace.Decision.FullSpeed)]
        public void GroupLeaderNeverSlowsDownAndStopsOnlyForAMemberFarBehind(float farthest, bool waiting,
            AutonomousGroupPace.Decision expected) =>
            Assert.That(AutonomousGroupPace.Decide(farthest, waiting), Is.EqualTo(expected));

        [Test]
        public void AvalonCityIsAProvenDungeonNow()
        {
            Assert.That(AutonomousDungeonPolicy.IsSupportedDungeonZone(50), Is.True);
            Assert.That(AutonomousDungeonGoalCatalog.VerifiedSpawnCountForRegion(50), Is.GreaterThanOrEqualTo(1000));
        }

        [Test]
        public void SavagesStopBleedingHealthIntoBuffs()
        {
            Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageDPSBuff, 69, 0), Is.False, "nothing below 70%");
            Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageCombatSpeedBuff, 75, 1), Is.True);
            Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageEvadeBuff, 85, 2), Is.False);
            Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageEvadeBuff, 95, 2), Is.True);
            Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageParryBuff, 100, 3), Is.False, "at most three");
            Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageCrushResistanceBuff, 100, 0), Is.False, "resists never");
            Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageDPSBuff, 100, 0, 50, 30), Is.False, "target nearly dead");
            Assert.That(SavageBotCombatPolicy.ShouldUseBuff(eSpellType.SavageDPSBuff, 100, 0, 50, 80), Is.True);
        }

        [Test]
        public void SoloPaladinsHoldOneChant()
        {
            Assert.That(BotSongTwistPolicy.SoloPaladinAnchor(true, 90), Is.EqualTo(eSpellType.DamageAdd));
            Assert.That(BotSongTwistPolicy.SoloPaladinAnchor(true, 50), Is.EqualTo(eSpellType.CombatHeal));
            Assert.That(BotSongTwistPolicy.SoloPaladinAnchor(false, 50), Is.EqualTo(eSpellType.EnduranceRegenBuff));
        }
    }
}

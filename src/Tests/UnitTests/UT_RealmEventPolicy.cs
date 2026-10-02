using DOL.GS;
using NUnit.Framework;

namespace DOL.Tests
{
    [TestFixture]
    public class UT_RealmEventPolicy
    {
        [Test]
        public void ReturnStepsCannotUseAWholeZoneAsALocalSurfaceQuery()
        {
            var origin = new System.Numerics.Vector3(100, 200, 300);
            var bounded = AutonomousThreatAwarePathing.BoundStep(origin, new(10100,200,1300),900);
            Assert.That(bounded.X, Is.EqualTo(1000).Within(0.01));
            Assert.That(bounded.Z, Is.EqualTo(390).Within(0.01));
            Assert.That(AutonomousThreatAwarePathing.BoundStep(origin,origin,900), Is.EqualTo(origin));
        }
        [TestCase(true, 160, EDtPolyFlags.Jump, EDtPolyFlags.Walk, true)]
        [TestCase(true, 160, EDtPolyFlags.Walk, EDtPolyFlags.Jump, true)]
        [TestCase(true, 160, EDtPolyFlags.Walk, EDtPolyFlags.Walk, false)]
        [TestCase(true, 249, EDtPolyFlags.Jump, EDtPolyFlags.Walk, true)]
        [TestCase(true, 249, EDtPolyFlags.Walk, EDtPolyFlags.Jump, true)]
        [TestCase(true, 249, EDtPolyFlags.Walk, EDtPolyFlags.Walk, false)]
        [TestCase(true, 60, EDtPolyFlags.Jump, EDtPolyFlags.Walk, false)]
        [TestCase(false, 160, EDtPolyFlags.Jump, EDtPolyFlags.Walk, false)]
        [TestCase(false, 249, EDtPolyFlags.Jump, EDtPolyFlags.Walk, false)]
        public void OnlyGlacierAndDarknessFallsBotClimbsDisableFloorOnlyCornerSkipping(bool bot, int zone,
            EDtPolyFlags current, EDtPolyFlags next, bool expected) =>
            Assert.That(Pathfinder.RequiresExactClimbNode(bot, (ushort)zone, current, next), Is.EqualTo(expected));
        [TestCase(2000, 100, 600, 0)]
        [TestCase(500, 200, 500, 100)]
        [TestCase(100, 0, 100, 0)]
        [TestCase(0, 0, 0, 0)]
        public void DragonCapIncludesCriticalsWithoutRaisingWeakHits(int damage, int critical, int expected, int expectedCritical)
        {
            var attack = new AttackData { Damage = damage, CriticalDamage = critical };
            RealmEventPolicy.CapDragonDamage(attack);
            Assert.That(attack.Damage, Is.EqualTo(expected));
            Assert.That(attack.CriticalDamage, Is.EqualTo(expectedCritical));
        }

        [Test]
        public void AssaultReadinessDoesNotNeedAnOpposingRealm()
        {
            Assert.That(RealmEventPolicy.AttackersReady(1000, 181000, 8, 8), Is.True);
            Assert.That(RealmEventPolicy.AttackersReady(1000, 181000, 8, 7), Is.False);
            Assert.That(RealmEventPolicy.AttackersReady(1000, 2000, 8, 8), Is.False);
            Assert.That(RealmEventPolicy.AttackersReady(1000, 181000, 0, 0), Is.False);
            Assert.That(RealmEventPolicy.CanReact(false), Is.False);
            Assert.That(RealmEventPolicy.CanReact(true), Is.True);
        }

        [Test]
        public void RecruitmentDeadlineAllowsOneHourForBothSides()
        {
            Assert.That(RealmEventPolicy.RecruitmentMilliseconds(0), Is.EqualTo(60 * 60000));
            Assert.That(RealmEventPolicy.RecruitmentMilliseconds(1), Is.EqualTo(60 * 60000));
        }

        [Test]
        public void LegacyReadinessNeverRequiresDefenderAttendance()
        {
            Assert.That(RealmEventPolicy.SiegeReady(false, true, 128, 0), Is.True);
            Assert.That(RealmEventPolicy.SiegeReady(false, true, 32, 16), Is.True);
            Assert.That(RealmEventPolicy.SiegeReady(false, false, 108, 64), Is.True);
            Assert.That(RealmEventPolicy.SiegeReady(true, true, 47, 24), Is.False);
            Assert.That(RealmEventPolicy.SiegeReady(true, true, 48, 24), Is.True);
            Assert.That(RealmEventPolicy.SiegeReady(true, false, 170, 96), Is.True);
        }

        [Test]
        public void DefenderAlarmPrecedesThirdRealmWithoutRequiringCombat()
        {
            Assert.That(RealmEventPolicy.CanRecruitRealm(true, false, 16, 0, 128), Is.True);
            Assert.That(RealmEventPolicy.CanRecruitRealm(false, false, 16, 0, 128), Is.False);
            Assert.That(RealmEventPolicy.CanRecruitRealm(false, false, 32, 32, 128), Is.True);
            Assert.That(RealmEventPolicy.CanRecruitRealm(true, true, 0, 0, 128), Is.True);
        }

        [Test]
        public void UnsupportedGlacierGriffonPatrolCannotIssueFlightMovement()
        {
            Assert.That(EpicEncounterMechanics.AllowGlacierGriffonPatrol, Is.False);
            // No Body exists: the disabled mechanism must return before even
            // reading/moving an actor, without changing the dragon flight code.
            Assert.DoesNotThrow(() => new DOL.AI.Brain.TorstBrain().TorstFlyingPath());
            Assert.DoesNotThrow(() => new DOL.AI.Brain.HurikaBrain().HurikaFlyingPath());
        }

        [Test]
        public void DisabledBroodmotherTeleportDoesNotInspectOrMoveAnyTarget()
        {
            var brain = new DOL.AI.Brain.SpindlerBroodmotherBrain();
            Assert.That(brain.PickTeleportPlayer(null), Is.Zero);
            Assert.That(brain.TeleportPlayer(null), Is.Zero);
        }

        [Test]
        public void OnlyOrdinaryFlyingPatrolsMayBeDeferredBeforeTheFinalTrigger()
        {
            Assert.That(RealmRaidDungeonRoute.CanDeferAirbornePatrol(typeof(GameEpicNPC), GameNPC.eFlags.FLYING), Is.True);
            Assert.That(RealmRaidDungeonRoute.CanDeferAirbornePatrol(typeof(GameEpicNPC), 0), Is.False);
            Assert.That(RealmRaidDungeonRoute.CanDeferAirbornePatrol(typeof(Fames), GameNPC.eFlags.FLYING), Is.False);
            Assert.That(RealmRaidDungeonRoute.CanDeferAirbornePatrol(typeof(Torst), GameNPC.eFlags.FLYING), Is.False);
            Assert.That(RealmRaidDungeonRoute.CanDeferAirbornePatrol(null, GameNPC.eFlags.FLYING), Is.False);
        }
    }
}

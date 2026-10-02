using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_CompanionRaidResurrectionReservations
    {
        private sealed class Owner : GamePlayer
        {
            private Owner() : base(null, null) { }
            public override byte Level { get => 50; set { } }
        }

        [Test]
        public void RaidCapacityCanSwitchWithoutChangingOrdinaryGroupLimit()
        {
            var owner = (Owner)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Owner));
            var group = new Group(owner);
            var members = (System.Collections.Generic.List<GameLiving>)typeof(Group).GetField("_groupMembers",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(group);
            members.Add(owner);
            Assert.That(group.MaximumMemberCount, Is.EqualTo(DOL.GS.ServerProperties.Properties.GROUP_MAX_MEMBER));
            Assert.That(group.EnableCompanionRaid(owner, 40), Is.True);
            Assert.That(group.MaximumMemberCount, Is.EqualTo(40));
            Assert.That(group.EnableCompanionRaid(owner, 80), Is.True);
            Assert.That(group.MaximumMemberCount, Is.EqualTo(80));
            Assert.That(group.EnableCompanionRaid(owner, 60), Is.False);
            Assert.That(group.MaximumMemberCount, Is.EqualTo(80));
            Assert.That(group.EnableCompanionRaid(owner, 40), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EightyRaidHas79DistinctFormationSlots(bool interior)
        {
            var points = new System.Collections.Generic.HashSet<(double, double)>();
            for (int i = 0; i < 79; i++)
            {
                var slot = CompanionRaidFormation.Slot(i, interior);
                Assert.That(slot.Radius, Is.InRange(90, 390));
                Assert.That(points.Add(slot), Is.True);
            }
        }

        [Test]
        public void NonHealingResurrectorDoesNotUseTheLastHealerSlot()
        {
            var leases = new CompanionRaidResurrectionReservations<object>();
            Assert.That(leases.TryReserve(new object(), new object(), 0, 5000, true, true, 1, false, false), Is.True);
            Assert.That(leases.TryReserve(new object(), new object(), 0, 5000, true, true, 1, false), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FormationHas39DistinctNearbySlots(bool interior)
        {
            var points = new System.Collections.Generic.HashSet<(double, double)>();
            for (int i = 0; i < 39; i++)
            {
                var slot = CompanionRaidFormation.Slot(i, interior);
                Assert.That(slot.Radius, Is.InRange(90, 250));
                Assert.That(points.Add(slot), Is.True);
            }
        }

        [Test]
        public void OneCorpseCannotReserveAllHealers()
        {
            var leases = new CompanionRaidResurrectionReservations<object>();
            var corpse = new object();
            Assert.That(leases.TryReserve(new object(), corpse, 0, 5000, true, true, 6, false), Is.True);
            for (int i = 0; i < 5; i++)
                Assert.That(leases.TryReserve(new object(), corpse, 0, 5000, true, true, 6, false), Is.False);
        }

        [Test]
        public void DifferentCorpsesCanHaveDifferentCasters()
        {
            var leases = new CompanionRaidResurrectionReservations<object>();
            Assert.That(leases.TryReserve(new object(), new object(), 0, 5000, true, true, 3, false), Is.True);
            Assert.That(leases.TryReserve(new object(), new object(), 0, 5000, true, true, 3, false), Is.True);
            Assert.That(leases.TryReserve(new object(), new object(), 0, 5000, true, true, 3, false), Is.False);
        }

        [Test]
        public void InterruptedCasterReleasesReservation()
        {
            var leases = new CompanionRaidResurrectionReservations<object>();
            var healer = new object();
            var corpse = new object();
            Assert.That(leases.TryReserve(healer, corpse, 0, 5000, true, true, 3, false), Is.True);
            Assert.That(leases.TryReserve(healer, corpse, 100, 5000, false, true, 3, false), Is.False);
            Assert.That(leases.TryReserve(new object(), corpse, 101, 5000, true, true, 3, false), Is.True);
        }

        [Test]
        public void ExpiredAndCompletedReservationsDoNotStrandCorpses()
        {
            var leases = new CompanionRaidResurrectionReservations<object>();
            var corpse = new object();
            Assert.That(leases.TryReserve(new object(), corpse, 0, 5000, true, false, 1, false), Is.True);
            Assert.That(leases.TryReserve(new object(), corpse, 7000, 5000, true, false, 1, false), Is.True);
            leases.ReleaseCorpse(corpse);
            Assert.That(leases.TryReserve(new object(), corpse, 7001, 5000, true, false, 1, false), Is.True);
        }

        [Test]
        public void OneCasterCannotReserveTwoCorpses()
        {
            var leases = new CompanionRaidResurrectionReservations<object>();
            var healer = new object();
            Assert.That(leases.TryReserve(healer, new object(), 0, 5000, true, false, 5, false), Is.True);
            Assert.That(leases.TryReserve(healer, new object(), 0, 5000, true, false, 5, false), Is.False);
        }

        [TestCase(true, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        public void LastHealerStaysOnSurvivorsUnlessBattleHasEnded(bool combat, bool urgent, bool expected)
        {
            var leases = new CompanionRaidResurrectionReservations<object>();
            Assert.That(leases.TryReserve(new object(), new object(), 0, 5000, true, combat, 1, urgent), Is.EqualTo(expected));
        }
    }
}

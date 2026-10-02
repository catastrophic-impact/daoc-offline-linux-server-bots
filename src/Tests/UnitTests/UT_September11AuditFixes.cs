using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_September11AuditFixes
    {
        [Test]
        public void DisbandedMembershipDoesNotThrowDuringSessionLookup()
        {
            var lookup = typeof(AutonomousBotGroupCoordinator).GetMethod("TryGetSession",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.That(lookup.Invoke(null, new object[] { null, null }), Is.False);
        }

        [TestCase(false, false, 500)]
        [TestCase(false, true, 500)]
        [TestCase(true, false, 500)]
        [TestCase(true, true, 1100)]
        public void OnlyMandatoryDungeonBlockersRelaxPullCohesion(bool blocker, bool dungeon, int expected)
        {
            Assert.That(AutonomousBotGroupCoordinator.PullCohesionRadius(blocker, dungeon), Is.EqualTo(expected));
        }

        [TestCase("offlinebot:12", 0, 1, "item", true)]
        [TestCase("offlinebot:13", 0, 1, "item", false)]
        [TestCase("offlinebot:12", 1, 1, "item", false)]
        [TestCase("offlinebot:12", 0, 2, "item", false)]
        [TestCase("offlinebot:12", 0, 1, "different", false)]
        public void PartialInsertRecoveryCannotAdoptSomeoneElsesOrListedItem(string owner, int lot, int count, string id, bool expected)
        {
            var current = new DbInventoryItem { ObjectId = "item", OwnerID = "offlinebot:12", Count = 1 };
            var stored = new DbInventoryItem { ObjectId = id, OwnerID = owner, OwnerLot = (ushort)lot, Count = count };
            Assert.That(BotInventory.CanReconcileSavedItem(current, stored), Is.EqualTo(expected));
        }
    }
}

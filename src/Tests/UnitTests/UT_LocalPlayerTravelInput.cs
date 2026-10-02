using DOL.GS.Commands;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_LocalPlayerTravelInput
    {
        [Test]
        public void ReadsExistingForwardScanCodeWithoutChangingBindings()
        {
            string[] profile = { "[keyboard]", "key00=17", "shift00=0", "[main]", "key00=30" };
            Assert.That(LocalPlayerTravelInput.TryReadForwardKey(profile, out ushort scan), Is.True);
            Assert.That(scan, Is.EqualTo(17));
        }

        [TestCase("109", "0")]
        [TestCase("0", "0")]
        [TestCase("17", "1")]
        [TestCase("unknown", "0")]
        public void UnknownUnboundOrModifiedKeysFailClosed(string key, string modifier)
        {
            Assert.That(LocalPlayerTravelInput.TryReadForwardKey(new[] { "[keyboard]", $"key00={key}", $"shift00={modifier}" }, out _), Is.False);
        }

        [Test]
        public void TravelNoLongerDiscardsClientMovementOrFabricatesPacketReceipts()
        {
            Assert.That(PlayerMobNavigator.SuppressClientMovement(null), Is.False);
        }

        [TestCase(1024, 1024, false)]
        [TestCase(1024, 1032, false)]
        [TestCase(4092, 4, false)]
        [TestCase(4, 4092, false)]
        [TestCase(5120, 1024, false)]
        [TestCase(1024, 1040, true)]
        [TestCase(1024, 2048, true)]
        [TestCase(4090, 12, true)]
        public void ContinuousRunningOnlySendsHeadingCorrectionsForActualTurns(int current, int desired, bool expected)
        {
            Assert.That(PlayerMobNavigator.NeedsTravelHeadingUpdate((ushort)current, (ushort)desired), Is.EqualTo(expected));
        }
    }
}

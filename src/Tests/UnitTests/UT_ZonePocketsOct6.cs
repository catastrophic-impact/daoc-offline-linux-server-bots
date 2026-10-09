using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>navmesh/pockets.json: walled-off mesh areas a bot is relocated out of.</summary>
    [TestFixture]
    public class UT_ZonePocketsOct6
    {
        // Valley of Bri Leith (zone 206): a shelf at y=483392 around z 7,000-7,400 (cell 512).
        private static readonly AutonomousZonePockets.Data Data = AutonomousZonePockets.Parse(
            "{\"version\":1,\"cell\":512,\"zones\":{\"206\":[[752,944,7010,7390]]}}");

        [TestCase(385088f, 483424f, 7262f, ExpectedResult = true, TestName = "A bot on the shelf is in a pocket")]
        [TestCase(385088f, 483424f, 7450f, ExpectedResult = true, TestName = "Within 96 units of the shelf floor still counts")]
        [TestCase(385088f, 483424f, 5100f, ExpectedResult = false, TestName = "Real ground far below the shelf is not")]
        [TestCase(386100f, 483424f, 7262f, ExpectedResult = false, TestName = "The next cell is not listed")]
        public bool PocketsAreFoundByCellAndFloorHeight(float x, float y, float z) =>
            AutonomousZonePockets.Contains(Data, 206, x, y, z);

        [Test]
        public void OtherZonesAreNeverPockets() =>
            Assert.That(AutonomousZonePockets.Contains(Data, 200, 385088, 483424, 7262), Is.False);
    }
}

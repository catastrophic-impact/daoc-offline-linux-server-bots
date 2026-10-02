using System;
using System.Linq;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_LongRunRoutingPolicy
    {
        private static Zone Zone(ushort id, int x, int y)
        {
            return new(null, id, "test", x, y, 65536, 65536, id, false, 0, false, 0, 0, 0, 0, 0);
        }

        [Test]
        public void FortAtlaWalkUsesEastSvealandInsteadOfCrossingAnEmptyMapGap()
        {
            var vale = Zone(100, 753664, 671744);
            var east = Zone(101, 720896, 737280);
            var gotar = Zone(103, 720896, 802816);
            Assert.That(AutonomousZoneItinerary.FindZoneRoute(new[] { vale, east, gotar }, vale, gotar)
                .Select(z => z.ID), Is.EqualTo(new ushort[] { 100, 101, 103 }));
            Assert.That(AutonomousZoneItinerary.TryGetSharedEdge(vale, east, out var edge), Is.True);
            var step = edge.At(794319, 4700);
            Assert.That(step.Outside.X, Is.LessThan(east.XOffset + east.Width));
            Assert.That(step.Outside.Y, Is.GreaterThan(east.YOffset));
        }

        [Test]
        public void GapsAndCornerOnlyNeighborsAreNeverWalkingSeams()
        {
            var start = Zone(1, 0, 0);
            Assert.That(AutonomousZoneItinerary.TryGetSharedEdge(start, Zone(2, 65537, 0), out _), Is.False);
            Assert.That(AutonomousZoneItinerary.TryGetSharedEdge(start, Zone(3, 65536, 65536), out _), Is.False);
            Assert.That(AutonomousZoneItinerary.FindZoneRoute(new[] { start, Zone(4, 131072, 0) }, start, Zone(5, 196608, 0)), Is.Empty);
        }

        [TestCase(220, true)] [TestCase(257, false)]
        public void BoardingHeightCorrectionIsBounded(int delta, bool expected)
        {
            Assert.That(AutonomousStableRoutePlanner.TryResolveBoardingPoint(new(803840, 721833, 4687),
                p => p + new Vector3(0, 0, delta), out _), Is.EqualTo(expected));
        }

        [Test]
        public void BoardingCannotJumpSidewaysOrAcceptNoMesh()
        {
            Assert.That(AutonomousStableRoutePlanner.TryResolveBoardingPoint(Vector3.Zero, _ => null, out _), Is.False);
            Assert.That(AutonomousStableRoutePlanner.TryResolveBoardingPoint(Vector3.Zero, p => p + new Vector3(49, 0, 0), out _), Is.False);
            Assert.That(AutonomousStableRoutePlanner.TryResolveBoardingPoint(Vector3.Zero, _ => new Vector3(float.NaN), out _), Is.False);
        }
    }
}

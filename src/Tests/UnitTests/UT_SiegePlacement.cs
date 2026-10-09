using System.Linq;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_SiegePlacement
    {
        // Bledmeer Faste (DB): keep centre 648740,583654; outer gate 648622,585225 heading 2080; inner door 648665,583533 heading 2021.
        private static readonly Vector2 Centre = new(648740, 583654);

        [Test]
        public void RamSpotsLieInFrontOfTheOuterFaceWithinReach()
        {
            var gate = new Vector3(648622, 585225, 7431);
            var spots = SiegePlacement.RamCandidates(gate, 2080, Centre).ToArray();
            Assert.That(spots, Is.Not.Empty);
            foreach (var spot in spots)
            {
                float reach = Vector2.Distance(new(spot.X, spot.Y), new(gate.X, gate.Y));
                Assert.That(reach, Is.InRange(200f, 375f));
                // Outside the keep: farther from the centre than the gate itself.
                Assert.That(Vector2.Distance(new(spot.X, spot.Y), Centre), Is.GreaterThan(Vector2.Distance(new(gate.X, gate.Y), Centre)));
            }
        }

        [Test]
        public void InnerDoorRamFacesTheCourtyard()
        {
            // Every inner-door ram spot lies on the gate's side of the keep door (the courtyard), not inside the keep.
            var inner = new Vector3(648665, 583533, 7440);
            var gate = new Vector2(648622, 585225);
            foreach (var spot in SiegePlacement.RamCandidates(inner, 2021, Centre, gate))
                Assert.That(Vector2.Distance(new(spot.X, spot.Y), gate), Is.LessThan(Vector2.Distance(new(inner.X, inner.Y), gate)));
        }

        [Test]
        public void KeepDoorRamFacesTheGateWhenTheKeepCentreLiesInTheCourtyard()
        {
            // Caer Benowyc (DB): keep centre 653430,345890; gate 652009,346442; keep door 653401,345539 heading 2178.
            // "Away from the centre" put these spots inside the keep building (owner test 2026-10-07).
            var centre = new Vector2(653430, 345890);
            var door = new Vector3(653401, 345539, 6474);
            var gate = new Vector2(652009, 346442);
            var spots = SiegePlacement.RamCandidates(door, 2178, centre, gate).ToArray();
            Assert.That(spots, Is.Not.Empty);
            foreach (var spot in spots)
                Assert.That(Vector2.Dot(new Vector2(spot.X, spot.Y) - new Vector2(door.X, door.Y), gate - new Vector2(door.X, door.Y)), Is.GreaterThan(0f));
        }

        [Test]
        public void ArtilleryRingsStayInsideTheEngineBandOnTheRightSide()
        {
            var gate = new Vector3(648622, 585225, 7431);
            foreach (var kind in new[] { BotSiegeKind.Catapult, BotSiegeKind.Trebuchet })
            {
                var (min, max) = SiegePlacement.Range(kind);
                var attack = SiegePlacement.ArtilleryCandidates(gate, Centre, new(648622, 588000), min, max, true).ToArray();
                Assert.That(attack, Is.Not.Empty);
                foreach (var spot in attack)
                {
                    float d = Vector2.Distance(new(spot.X, spot.Y), new(gate.X, gate.Y));
                    Assert.That(d, Is.InRange(min + 50f, max - 100f));
                    Assert.That(Vector2.Distance(new(spot.X, spot.Y), Centre), Is.GreaterThan(Vector2.Distance(new(gate.X, gate.Y), Centre)));
                }
                var defend = SiegePlacement.ArtilleryCandidates(gate, Centre, new(648740, 583654), min, max, false).ToArray();
                Assert.That(defend.All(spot => Vector2.Distance(new(spot.X, spot.Y), Centre) < Vector2.Distance(new(gate.X, gate.Y), Centre)));
            }
        }

        [Test]
        public void OnlyCatapultsAndTrebuchetsArc()
        {
            Assert.That(SiegePlacement.Arcing(BotSiegeKind.Catapult), Is.True);
            Assert.That(SiegePlacement.Arcing(BotSiegeKind.Trebuchet), Is.True);
            Assert.That(SiegePlacement.Arcing(BotSiegeKind.Ballista), Is.False);
            Assert.That(SiegePlacement.Arcing(BotSiegeKind.Ram), Is.False);
        }
    }
}

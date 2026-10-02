using System;
using System.Collections.Generic;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    public class UT_RealmRaidFormation
    {
        internal sealed class Mesh : PathfindingMgrBase
        {
            public bool Connected = true, Visible = true;
            public float HeightOffset;
            public int Samples;
            public override bool IsAvailable => true;
            public override bool HasNavmesh(Zone zone) => true;
            public override Vector3? GetClosestPoint(Zone zone, Vector3 point, float x, float y, float z, EDtPolyFlags[] filters)
            { Samples++; return point + new Vector3(0, 0, HeightOffset); }
            public override bool HasLineOfSight(Zone zone, Vector3 from, Vector3 to, EDtPolyFlags[] filters) => Visible;
            public override PathfindingResult GetPathStraight(Zone zone, Vector3 from, Vector3 to, EDtPolyFlags[] filters, Span<WrappedPathfindingNode> result)
            {
                result[0] = new(Connected ? to : from, EDtPolyFlags.Walk);
                return new(Connected ? PathfindingStatus.PathFound : PathfindingStatus.PartialPathFound, 1);
            }
        }
        internal static Zone Zone() => new(null, 160, "raid floor", 0, 0, 65536, 65536, 160, false, 0, false, 0, 0, 0, 0, 0);

        [Test]
        public void ThirtyPartyPostsAreDistinctAndRemainWithinMutualSupportRange()
        {
            var occupied = new List<Vector3>();
            var nav = new Mesh();
            Vector3 center = new(20000, 20000, 1000);
            for (int slot = 0; slot < RealmRaidRecruitmentPolicy.MaximumParties; slot++)
            {
                Assert.That(RealmRaidFormation.TryResolve(nav, Zone(), center, slot, occupied, out Vector3 position), Is.True);
                Assert.That(Vector3.Distance(position, center), Is.LessThan(1000));
                foreach (Vector3 other in occupied) Assert.That(Vector3.Distance(position, other), Is.GreaterThanOrEqualTo(120));
                occupied.Add(position);
            }
            Assert.That(nav.Samples, Is.LessThanOrEqualTo(RealmRaidRecruitmentPolicy.MaximumParties * 6));
        }

        [TestCase("wall")] [TestCase("island")] [TestCase("floor")]
        public void UnusablePostsAreRejectedInsteadOfSendingPartiesThroughGeometry(string reason)
        {
            var nav = new Mesh { Visible = reason != "wall", Connected = reason != "island", HeightOffset = reason == "floor" ? 500 : 0 };
            Assert.That(RealmRaidFormation.TryResolve(nav, Zone(), new(10000, 10000, 1000), 5, [], out _), Is.False);
            Assert.That(nav.Samples, Is.EqualTo(22));
        }
    }
}

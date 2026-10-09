using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Coincident straight-path nodes (several polygon crossings at one shared vertex, as at the
    /// Iarnwood four-tile corner) are merged, keeping door flags and the final node.
    /// </summary>
    [TestFixture]
    public class UT_PathNodeMergeOct5
    {
        [Test]
        public void TheSameCornerReportedThreeTimesBecomesOneNode()
        {
            var nodes = new[]
            {
                new WrappedPathfindingNode(new(436500, 411000, 4700), EDtPolyFlags.Walk),
                new WrappedPathfindingNode(new(436838.4f, 411340.8f, 4803.206f), EDtPolyFlags.Walk),
                new WrappedPathfindingNode(new(436838.4f, 411340.8f, 4803.200f), EDtPolyFlags.Door),
                new WrappedPathfindingNode(new(436838.4f, 411340.8f, 4803.191f), EDtPolyFlags.Walk),
                new WrappedPathfindingNode(new(437465, 411833, 5107), EDtPolyFlags.Walk),
            };
            int count = LocalPathfindingMgr.MergeCoincidentNodes(nodes, nodes.Length);
            Assert.Multiple(() =>
            {
                Assert.That(count, Is.EqualTo(3));
                Assert.That(nodes[1].Position.Z, Is.EqualTo(4803.206f));
                Assert.That(nodes[1].Flags & EDtPolyFlags.Door, Is.EqualTo(EDtPolyFlags.Door), "A door crossing survives the merge.");
                Assert.That(nodes[2].Position, Is.EqualTo(new Vector3(437465, 411833, 5107)), "The destination is kept.");
            });
        }

        [Test]
        public void NodesAFewUnitsApartAreKept()
        {
            var nodes = new[]
            {
                new WrappedPathfindingNode(new(0, 0, 0), EDtPolyFlags.Walk),
                new WrappedPathfindingNode(new(3, 0, 0), EDtPolyFlags.Walk),
                new WrappedPathfindingNode(new(6, 0, 0), EDtPolyFlags.Walk),
            };
            Assert.That(LocalPathfindingMgr.MergeCoincidentNodes(nodes, nodes.Length), Is.EqualTo(3));
        }
    }
}

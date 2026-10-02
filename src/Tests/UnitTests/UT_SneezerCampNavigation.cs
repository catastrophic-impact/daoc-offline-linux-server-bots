using System;
using System.Numerics;
using System.Text;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

// Run explicitly against the installed Domnann mesh before placing camp spawns.
[TestFixture, NonParallelizable, Explicit("Reads the installed Domnann navmesh")]
public sealed class UT_SneezerCampNavigation
{
    [Test]
    public void ProbeCampFloor()
    {
        string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        Assert.That(root, Is.Not.Null.And.Not.Empty);
        string previous = Environment.CurrentDirectory;
        var zone = new Zone(null, 181, "Domnann", 393216, 393216, 65536, 65536,
            181, false, 0, false, 0, 0, 0, 0, 0);
        try
        {
            Environment.CurrentDirectory = root;
            LocalPathfindingMgr.LoadNavMesh(zone);
            var nav = PathfindingProvider.LocalPathfindingMgr;
            Assert.That(nav.HasNavmesh(zone), Is.True);
            Vector3 original = new(417984, 408257, 4378);
            Vector3? originalFloor = nav.GetClosestPoint(zone, original, 8, 8, 96, nav.DefaultFilters);
            Assert.That(originalFloor.HasValue, Is.True);
            TestContext.WriteLine($"Original: {original}, nav floor: {originalFloor.Value}");
            for (int y = 407900; y <= 408900; y += 100)
            {
                var row = new StringBuilder($"{y}: ");
                for (int x = 417500; x <= 418500; x += 100)
                {
                    Vector3 proposed = new(x, y, 4378);
                    Vector3? floor = nav.GetClosestPoint(zone, proposed, 8, 8, 96, nav.DefaultFilters);
                    bool usable = floor.HasValue &&
                        Vector2.Distance(new(proposed.X, proposed.Y), new(floor.Value.X, floor.Value.Y)) < 16 &&
                        Math.Abs(proposed.Z - floor.Value.Z) < 80 &&
                        AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, original, floor.Value);
                    row.Append(usable ? '.' : '#');
                }
                TestContext.WriteLine(row.ToString());
            }
            Vector3[] proposedSpawns =
            [
                new(417650, 408050, 4378), new(418300, 408100, 4379),
                new(417520, 408420, 4378), new(418420, 408410, 4382),
                new(417700, 408730, 4382), new(418150, 408800, 4387),
                new(418450, 408850, 4391)
            ];
            foreach (Vector3 proposed in proposedSpawns)
            {
                Vector3? floor = nav.GetClosestPoint(zone, proposed, 8, 8, 96, nav.DefaultFilters);
                Assert.That(floor.HasValue, Is.True, $"Spawn {proposed} must have a walkable floor");
                Assert.That(Vector2.Distance(new(proposed.X, proposed.Y),
                    new(floor.Value.X, floor.Value.Y)), Is.LessThan(16));
                Assert.That(AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, original,
                    floor.Value), Is.True, $"Spawn {proposed} must be reachable from the original");
                TestContext.WriteLine($"Spawn: {proposed}, nav floor: {floor.Value}");
            }
        }
        finally
        {
            LocalPathfindingMgr.UnloadNavMesh(zone);
            Environment.CurrentDirectory = previous;
        }
    }
}
